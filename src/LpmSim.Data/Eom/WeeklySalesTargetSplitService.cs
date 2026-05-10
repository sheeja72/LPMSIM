using LpmSim.Core.Entities;
using LpmSim.Data.Auditing;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data.Eom;

/// <summary>
/// CRUD + validation for the Weekly Sales Target Split admin page.
/// One save persists the 4 weekly splits for a single
/// <c>(Country, Year, Month, DivCode)</c> tuple as 4 rows in
/// <c>dbo.LPM_WeeklySalesTargetSplit</c> (one per <c>WeekNo</c>). The
/// service enforces sum(splits) = 100 ± 0.01 before writing — the
/// <see cref="EomCalculator"/> assumes any saved row is balanced and only
/// falls back to the hard-coded 20/20/25/35 default when the row is
/// missing entirely.
/// </summary>
public class WeeklySalesTargetSplitService(
    IDbContextFactory<LpmDbContext> dbFactory,
    IActionLogger actionLog)
{
    /// <summary>The default split applied per-week when no row is configured.</summary>
    public static readonly decimal[] DefaultSplit = { 20m, 20m, 25m, 35m };

    /// <summary>
    /// Returns one <see cref="WeeklySplitRow"/> per (Country, Year, Month, Div)
    /// tuple in the requested filter, with <c>Wk1..Wk4</c> populated either
    /// from saved rows or from <see cref="DefaultSplit"/> when none exist.
    /// The Division dimension comes from <c>dbo.Division</c> so admins always
    /// see every division for the period — even those without a saved row —
    /// and can pin a custom split by editing the default values inline.
    /// </summary>
    public async Task<List<WeeklySplitRow>> ListAsync(
        string country, int year, int month, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var divisions = await db.Divisions.AsNoTracking()
            .OrderBy(d => d.DivCode)
            .Select(d => new { d.DivCode, d.Name })
            .ToListAsync(ct);

        var saved = await db.LpmWeeklySalesTargetSplits.AsNoTracking()
            .Where(s => s.Country == country && s.Year1 == year && s.Month1 == month && s.IsActive)
            .ToListAsync(ct);

        var byDiv = saved
            .GroupBy(s => s.DivCode)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => (int)x.WeekNo, x => x.SplitPct));

        var result = new List<WeeklySplitRow>(divisions.Count);
        foreach (var d in divisions)
        {
            decimal Pick(int weekNo)
                => byDiv.TryGetValue(d.DivCode, out var weeks)
                   && weeks.TryGetValue(weekNo, out var pct)
                       ? pct
                       : DefaultSplit[weekNo - 1];

            var hasSaved = byDiv.ContainsKey(d.DivCode);
            result.Add(new WeeklySplitRow(
                Country:  country,
                Year:     year,
                Month:    month,
                DivCode:  d.DivCode,
                Division: d.Name ?? "",
                Wk1:      Pick(1),
                Wk2:      Pick(2),
                Wk3:      Pick(3),
                Wk4:      Pick(4),
                IsCustom: hasSaved));
        }
        return result;
    }

    /// <summary>
    /// Save 4 weekly splits for a single (Country, Year, Month, Div). Throws
    /// when the splits don't sum to 100 (± 0.01 tolerance) or any value is
    /// outside [0, 100]. Existing rows for the same key are updated in place
    /// (<c>WeekNo</c> 1..4 has a UNIQUE constraint), so saves are idempotent.
    /// </summary>
    public async Task SaveAsync(WeeklySplitRow row, CancellationToken ct = default)
    {
        ValidateOrThrow(row);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = await db.LpmWeeklySalesTargetSplits
            .Where(s => s.Country  == row.Country
                     && s.Year1    == row.Year
                     && s.Month1   == row.Month
                     && s.DivCode  == row.DivCode)
            .ToListAsync(ct);

        var existingByWeek = existing.ToDictionary(x => (int)x.WeekNo);

        decimal[] pcts = { row.Wk1, row.Wk2, row.Wk3, row.Wk4 };
        var now = DateTime.Now;
        for (int w = 1; w <= 4; w++)
        {
            if (existingByWeek.TryGetValue(w, out var e))
            {
                e.SplitPct  = pcts[w - 1];
                e.IsActive  = true;
                e.UpdatedTS = now;
                e.UpdatedBy = (await GetUserAsync()) ?? "system";
            }
            else
            {
                db.LpmWeeklySalesTargetSplits.Add(new LpmWeeklySalesTargetSplit
                {
                    Country  = row.Country,
                    Year1    = row.Year,
                    Month1   = row.Month,
                    DivCode  = row.DivCode,
                    WeekNo   = (byte)w,
                    SplitPct = pcts[w - 1],
                    IsActive = true,
                    CreateTS = now,
                    CreateBy = (await GetUserAsync()) ?? "system",
                });
            }
        }

        await db.SaveChangesAsync(ct);

        await actionLog.LogAsync(
            "WeeklySalesTargetSplit",
            $"{row.Country}/{row.Year}-{row.Month:D2}/Div{row.DivCode}",
            new
            {
                row.Country, row.Year, row.Month, row.DivCode,
                row.Wk1, row.Wk2, row.Wk3, row.Wk4,
            }, ct);
    }

    /// <summary>
    /// Delete all 4 weekly splits for a single (Country, Year, Month, Div) so
    /// the EomCalculator falls back to the 20/20/25/35 default. Hard delete
    /// — there's no "deactivate one weekly row" UX since splits are saved as
    /// a balanced set of 4. Audited.
    /// </summary>
    public async Task DeleteAsync(string country, int year, int month, int divCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.LpmWeeklySalesTargetSplits
            .Where(s => s.Country == country && s.Year1 == year && s.Month1 == month && s.DivCode == divCode)
            .ToListAsync(ct);
        if (existing.Count == 0) return;

        db.LpmWeeklySalesTargetSplits.RemoveRange(existing);
        await db.SaveChangesAsync(ct);

        await actionLog.LogAsync(
            "WeeklySalesTargetSplit",
            $"{country}/{year}-{month:D2}/Div{divCode}",
            new { Action = "Delete", Removed = existing.Count }, ct);
    }

    /// <summary>
    /// Pure-function validation — exposed so the Razor page can preview the
    /// "Total" cell + Save-disabled state without a server round-trip.
    /// </summary>
    public static (bool Ok, string? Error) Validate(decimal wk1, decimal wk2, decimal wk3, decimal wk4)
    {
        decimal[] pcts = { wk1, wk2, wk3, wk4 };
        for (int i = 0; i < pcts.Length; i++)
        {
            if (pcts[i] < 0 || pcts[i] > 100)
                return (false, $"Week {i + 1}: split must be between 0 and 100 (got {pcts[i]}).");
        }
        var total = wk1 + wk2 + wk3 + wk4;
        if (Math.Abs(total - 100m) > 0.01m)
            return (false, $"Splits must sum to 100 (got {total:0.##}).");
        return (true, null);
    }

    private static void ValidateOrThrow(WeeklySplitRow row)
    {
        var (ok, err) = Validate(row.Wk1, row.Wk2, row.Wk3, row.Wk4);
        if (!ok) throw new InvalidOperationException(err);
    }

    // No DI for ICurrentUser here — the audit logger captures it via the
    // ActionLogger's own ICurrentUser dependency. We want a "best effort"
    // user string for the entity's CreateBy/UpdatedBy columns; "system"
    // is a fine fallback when no user context is available (e.g. background).
    private static Task<string?> GetUserAsync() => Task.FromResult<string?>(null);
}

/// <summary>
/// Flat shape returned to the admin page — one row per
/// <c>(Country, Year, Month, Div)</c> with the 4 weekly percentages spread
/// across columns. <c>IsCustom = false</c> means the row is showing the
/// hard-coded default split (no DB rows for this Div).
/// </summary>
public record WeeklySplitRow(
    string  Country,
    int     Year,
    int     Month,
    int     DivCode,
    string  Division,
    decimal Wk1,
    decimal Wk2,
    decimal Wk3,
    decimal Wk4,
    bool    IsCustom);
