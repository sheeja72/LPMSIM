using LpmSim.Core;
using LpmSim.Core.Entities;
using LpmSim.Data.Warehouse;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data.LpmSim;

/// <summary>
/// ADM (Allocation Distribution Model) — assigns the run period's LPM
/// boxes to weeks 1..N using a need-driven algorithm:
///
/// <list type="number">
///   <item>Compute the weekly target as <c>TotalMonthlyLPM × Week1TargetPct</c>
///         (default 25 % so 4 weeks split equally).</item>
///   <item>Order divisions DESC by FillGap (= 1 − SOH/TargetEOM) so the
///         most under-served division gets first crack at each week.</item>
///   <item>Within a division, walk boxes oldest-first (with a tiebreak
///         for fresh-brand boxes when <c>ApplyVarietyBonus</c> is on).</item>
///   <item>Place each box in the earliest week with capacity that doesn't
///         break the brand-cap (default 25 % of weekly target per brand).</item>
///   <item>Boxes that can't fit any week's caps end with <c>Week = NULL</c>
///         and a <c>Reason</c> code (BRAND_CAP, WEEK_FULL, NO_DIV).</item>
/// </list>
///
/// SIM (item → store) and ADM (box → week) are independent — ADM works
/// directly off <c>racks.dbo.whboxitems</c> and <c>LPM_EOM_Output</c>;
/// it doesn't read or write any SIM batch.
/// </summary>
public class LpmAdmService(IDbContextFactory<LpmDbContext> dbFactory, ICurrentUser currentUser)
{
    /// <summary>Inputs passed by the UI when the user clicks Generate.</summary>
    public sealed class GenerateRequest
    {
        public string   Country { get; set; } = "";
        public DateTime RunDate { get; set; }
        public int      NumWeeks { get; set; } = 4;
        public decimal  Week1TargetPct { get; set; } = 25.00m;
        public decimal  BrandCapPct { get; set; } = 25.00m;
        public bool     ApplyVarietyBonus { get; set; } = true;
    }

    /// <summary>Result returned to the UI immediately after Generate.</summary>
    public sealed class GenerateResult
    {
        public long AdmRunNo { get; set; }
        public int  TotalEligibleBoxes { get; set; }
        public long TotalEligibleQty   { get; set; }
        public int  ScheduledBoxes     { get; set; }
        public long ScheduledQty       { get; set; }
        public int  DeferredBoxes      { get; set; }
        public long DeferredQty        { get; set; }
        /// <summary>Per-week target qty the engine used (for the run header banner).</summary>
        public long WeekTargetQty      { get; set; }
        /// <summary>SUM of MerchNeedWeek across divisions — what the engine pulled from EOM.</summary>
        public long TotalWeeklyNeed    { get; set; }
        /// <summary>True when supply was bounded by demand (otherwise spread by supply).</summary>
        public bool DemandBound        { get; set; }
        public long ElapsedMs          { get; set; }
    }

    /// <summary>
    /// Generate (or replace) the Draft ADM run for (Country, RunDate). If a
    /// previously-Approved run exists for the same key, the call fails — the
    /// caller must Delete it first (mirrors the SIM Generate pattern).
    /// </summary>
    public async Task<GenerateResult> GenerateAsync(GenerateRequest req, CancellationToken ct = default)
    {
        if (req.NumWeeks < 1 || req.NumWeeks > 8)
            throw new ArgumentOutOfRangeException(nameof(req.NumWeeks), "NumWeeks must be 1..8.");
        if (req.Week1TargetPct <= 0 || req.Week1TargetPct > 100)
            throw new ArgumentOutOfRangeException(nameof(req.Week1TargetPct), "Week1TargetPct must be > 0 and ≤ 100.");
        if (req.BrandCapPct <= 0 || req.BrandCapPct > 100)
            throw new ArgumentOutOfRangeException(nameof(req.BrandCapPct), "BrandCapPct must be > 0 and ≤ 100.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Check for existing run on (Country, RunDate); fail if Approved, else delete.
        var existing = await db.LpmSimAdmRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Country == req.Country && r.RunDate == req.RunDate.Date, ct);
        if (existing is { Status: "Approved" })
            throw new InvalidOperationException(
                $"An Approved ADM run (#{existing.AdmRunNo}) already exists for {req.Country} on {req.RunDate:yyyy-MM-dd}. Delete it first.");
        if (existing is not null)
        {
            db.Database.SetCommandTimeout(300);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"DELETE FROM dbo.LPMSIM_AdmRun WHERE AdmRunNo = {existing.AdmRunNo};", ct);
            db.ChangeTracker.Clear();
        }

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        var runYear  = req.RunDate.Year;
        var runMonth = req.RunDate.Month;
        var endExclusive = new DateTime(runYear, runMonth, 1).AddMonths(1);

        // 1.14.61 — Country-aware whboxitems source: UAE → racks.dbo.whboxitems;
        // other countries → [<DataName>].dbo.WHBoxItemsExport. Resolved once
        // here; reused by both queries below.
        var whSrc = await WhBoxItemsSource.ResolveAsync(conn, req.Country, ct);

        // ── 1) Pull eligible LPM boxes for the run month ───────────────────
        // Each row is (BoxNo, Warehouse, LPM, ItemCode, Qty, LPMDt). We
        // aggregate to (Box, Brand, MaxLPMDt) below — division comes from
        // a per-box MIN(div) lookup so a single box gets one division.
        var boxes = new List<BoxRow>();
        using (var cmd = (SqlCommand)conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT  w.BoxNo,
                        MAX(w.Warehouse) AS Warehouse,
                        MAX(w.LPM)       AS LPMBrand,
                        SUM(CAST(ISNULL(w.Qty, 0) AS bigint)) AS BoxQty,
                        MAX(w.LPMDt)     AS LPMDt
                  FROM {whSrc} w
                  INNER JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
                 WHERE pt.PalletCategory = 'ELIGIBLE'
                   AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
                   AND w.LPMDt IS NOT NULL
                   AND w.LPMDt >= @start AND w.LPMDt < @end
                 GROUP BY w.BoxNo;";
            cmd.Parameters.Add(new SqlParameter("@start", new DateTime(runYear, runMonth, 1)));
            cmd.Parameters.Add(new SqlParameter("@end",   endExclusive));
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                if (rdr.IsDBNull(0)) continue;
                boxes.Add(new BoxRow(
                    BoxNo:     rdr.GetString(0),
                    Warehouse: rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    Brand:     rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    Qty:       rdr.IsDBNull(3) ? 0    : (int)rdr.GetInt64(3),
                    LPMDt:     rdr.IsDBNull(4) ? null : (DateTime?)rdr.GetDateTime(4)));
            }
        }
        if (boxes.Count == 0)
        {
            // No eligible boxes — write an empty run header so the UI shows
            // "no boxes" cleanly rather than a blank page.
            return await PersistEmptyRunAsync(db, req, sw, ct);
        }

        // ── 2) Per-box division lookup (TOP 1 per box via MIN) ─────────────
        // Same join shape used elsewhere: itemcode → upc_subclass → subclassmaster.
        // A box typically has items from one division; if it has multiple, MIN()
        // picks the lowest-coded division (deterministic).
        var boxDiv = new Dictionary<string, (int DivCode, string Division)>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = (SqlCommand)conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT w.BoxNo,
                       MIN(d.DivCode) AS DivCode,
                       MAX(d.Division) AS Division
                  FROM {whSrc} w
                  INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = w.ItemCode
                  INNER JOIN Datareporting.dbo.subclassmaster sm  ON sm.MH4ID   = u.MH4ID
                  INNER JOIN dbo.Division                      d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                 WHERE w.LPMDt >= @start AND w.LPMDt < @end
                 GROUP BY w.BoxNo;";
            cmd.Parameters.Add(new SqlParameter("@start", new DateTime(runYear, runMonth, 1)));
            cmd.Parameters.Add(new SqlParameter("@end",   endExclusive));
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                if (rdr.IsDBNull(0) || rdr.IsDBNull(1)) continue;
                boxDiv[rdr.GetString(0)] = (rdr.GetInt32(1),
                                            rdr.IsDBNull(2) ? "" : rdr.GetString(2));
            }
        }

        // ── 3) Per-(Store, Div) demand + SOH from LPM_EOM_Output ───────────
        // Aggregated to division level for the headline FillGap ranking.
        var divNeed     = new Dictionary<int, long>();      // SUM MerchNeedWeek per div
        var divSoh      = new Dictionary<int, long>();      // SUM SOH per div
        var divTargetEom= new Dictionary<int, decimal>();   // SUM TargetEOM per div
        var divName     = new Dictionary<int, string>();    // pretty name
        using (var cmd = (SqlCommand)conn.CreateCommand())
        {
            // SUM(int) returns int in SQL Server, which would fail GetInt64().
            // Explicit CAST to bigint keeps the C# reader happy and avoids
            // overflow on huge division SOH totals.
            cmd.CommandText = @"
                SELECT eo.DivCode,
                       MAX(d.Division)                                 AS Division,
                       SUM(CAST(ISNULL(eo.SOH, 0)           AS bigint)) AS DivSOH,
                       SUM(ISNULL(eo.TargetEOM, 0))                    AS DivTargetEOM,
                       SUM(CAST(ISNULL(eo.MerchNeedWeek, 0) AS bigint)) AS DivWeekNeed
                  FROM dbo.LPM_EOM_Output eo
                  INNER JOIN dbo.DataSettings ds
                          ON ds.StoreID = eo.StoreID AND ds.SIMCountry = @country
                  LEFT  JOIN dbo.Division     d  ON d.DivCode = eo.DivCode
                 WHERE eo.Country = @country
                   AND eo.Year1   = @y
                   AND eo.Month1  = @m
                 GROUP BY eo.DivCode;";
            cmd.Parameters.Add(new SqlParameter("@country", req.Country));
            cmd.Parameters.Add(new SqlParameter("@y", runYear));
            cmd.Parameters.Add(new SqlParameter("@m", runMonth));
            cmd.CommandTimeout = 120;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                if (rdr.IsDBNull(0)) continue;
                var div = rdr.GetInt32(0);
                divName[div]      = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                divSoh[div]       = rdr.IsDBNull(2) ? 0L : rdr.GetInt64(2);
                divTargetEom[div] = rdr.IsDBNull(3) ? 0m : rdr.GetDecimal(3);
                divNeed[div]      = rdr.IsDBNull(4) ? 0L : rdr.GetInt64(4);
            }
        }

        // ── 4) Compute per-div fill rate / gap, then the urgency-ordered list
        // FillRate = SOH / TargetEOM (clamped to [0,1]); FillGap = 1 - FillRate.
        // Divisions with no TargetEOM are dropped (can't compute) — boxes
        // landing there get Reason = "NO_DIV" later.
        var divStats = new Dictionary<int, DivStat>();
        foreach (var div in divName.Keys)
        {
            var soh = divSoh.GetValueOrDefault(div, 0L);
            var tgt = divTargetEom.GetValueOrDefault(div, 0m);
            decimal fillRate, fillGap;
            if (tgt <= 0)
            {
                fillRate = 0m;
                fillGap  = 0m;
            }
            else
            {
                fillRate = Math.Min(1m, (decimal)soh / tgt);
                fillGap  = Math.Max(0m, 1m - fillRate);
            }
            divStats[div] = new DivStat(div, divName[div],
                FillRatePct: fillRate * 100m,
                FillGapPct:  fillGap * 100m,
                WeekNeed:    divNeed.GetValueOrDefault(div, 0L));
        }

        // ── 5) Need-driven weekly placement ─────────────────────────────────
        // Per business spec: "Week Target = SUM(MerchNeedWeek across divisions)".
        // No multiplier — every week aims to ship exactly the weekly merch need.
        //
        // Bounded by supply: if total LPM supply < demand × NumWeeks, the
        // weekly target is reduced to TotalSupply / NumWeeks so supply spreads
        // evenly across all weeks instead of dumping into Week 1.
        //
        // Per week, walk divisions in FillGap DESC order and pull boxes until
        // the division's weekly quota is met (or the week fills, or the brand
        // cap blocks). Surplus boxes — those with no week to land in — end up
        // Week=NULL with the most-binding skip reason.
        var totalQty           = boxes.Sum(b => (long)b.Qty);
        var totalWeeklyNeed    = divNeed.Values.Sum();         // SUM(MerchNeedWeek)
        var demandWeekTarget   = totalWeeklyNeed;              // demand-driven goal
        var supplyWeekTarget   = totalQty / Math.Max(1, req.NumWeeks);  // supply-spread floor
        // When demand is 0 (no MerchNeedWeek loaded for the period) fall back
        // to pure supply-spread mode so we still place boxes evenly.
        var weekTargetQty      = totalWeeklyNeed > 0
            ? Math.Min(demandWeekTarget, Math.Max(supplyWeekTarget, 1L))
            : Math.Max(supplyWeekTarget, 1L);
        var weekTargets        = new long[req.NumWeeks];
        for (int i = 0; i < req.NumWeeks; i++) weekTargets[i] = weekTargetQty;

        // Per-division weekly target. When supply is tight, scale each div's
        // share by (weekTargetQty / demandWeekTarget) so per-div quotas sum to
        // weekTargetQty. When supply is rich, divScale=1 → div quota = MerchNeedWeek.
        var divWeeklyTarget = new Dictionary<int, long>();
        if (demandWeekTarget > 0)
        {
            decimal divScale = (decimal)weekTargetQty / demandWeekTarget;
            foreach (var (div, need) in divNeed)
                divWeeklyTarget[div] = (long)Math.Round(need * divScale);
        }
        // (If totalWeeklyNeed == 0, divWeeklyTarget stays empty — div-quota
        // check is skipped in the placement loop and only week target + brand
        // cap constrain placement.)

        // Brand cap per week = BrandCapPct of the week's target qty.
        var brandCapPerWeek = new long[req.NumWeeks];
        for (int i = 0; i < req.NumWeeks; i++)
            brandCapPerWeek[i] = (long)Math.Round(weekTargets[i] * (req.BrandCapPct / 100m));

        // Boxes annotated with division/brand metadata for downstream display.
        // FillRate/FillGap are read from divStats (per-division, constant across run).
        var annotated = boxes.Select(b =>
        {
            string brand = string.IsNullOrWhiteSpace(b.Brand) ? "(no-brand)" : b.Brand!.Trim();
            int? divCode = boxDiv.TryGetValue(b.BoxNo, out var d) ? d.DivCode : (int?)null;
            string? division = divCode.HasValue ? d.Division : null;
            decimal fillRate = divCode.HasValue && divStats.TryGetValue(divCode.Value, out var ds)
                ? ds.FillRatePct : 0m;
            decimal fillGap  = divCode.HasValue && divStats.TryGetValue(divCode.Value, out ds)
                ? ds.FillGapPct  : 0m;
            return new AnnotatedBox(b, divCode, division, brand, fillRate, fillGap);
        }).ToList();

        // Group boxes by division so we can iterate per-division per-week.
        // Within each div, boxes are pre-sorted oldest-first (LPMDt ASC).
        var boxesByDiv = annotated
            .Where(a => a.DivCode.HasValue)
            .GroupBy(a => a.DivCode!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(a => a.Box.LPMDt ?? DateTime.MaxValue)
                      .ThenBy(a => a.Box.BoxNo, StringComparer.Ordinal)
                      .ToList());

        // Divisions sorted by FillGap DESC — most-under-filled first claims
        // each week's capacity before lower-priority divs.
        var divisionsByUrgency = divStats.Values
            .OrderByDescending(d => d.FillGapPct)
            .Select(d => d.DivCode)
            .ToList();

        // Per-week running tallies.
        var weekQty        = new long[req.NumWeeks];
        var brandQtyByWeek = new Dictionary<string, long>[req.NumWeeks];
        var divQtyByWeek   = new Dictionary<int, long>[req.NumWeeks];
        for (int i = 0; i < req.NumWeeks; i++)
        {
            brandQtyByWeek[i] = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            divQtyByWeek[i]   = new Dictionary<int, long>();
        }

        // Track which boxes have been placed and the binding skip reason for
        // those still trying to find a week.
        var placedBoxes        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastSkipReason     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allocs             = new List<LpmSimAdmBoxAlloc>(annotated.Count);

        // Walk weeks, divisions within each week.
        for (int w = 0; w < req.NumWeeks; w++)
        {
            foreach (var divCode in divisionsByUrgency)
            {
                if (weekQty[w] >= weekTargets[w]) break;                 // week's overall target met
                if (!boxesByDiv.TryGetValue(divCode, out var divBoxList)) continue;
                var divTarget = divWeeklyTarget.GetValueOrDefault(divCode, 0L);
                // useDivQuota = false when no MerchNeedWeek data is available
                // (totalWeeklyNeed == 0) — only week target + brand cap constrain.
                bool useDivQuota = divTarget > 0;
                var divPlacedThisWeek = divQtyByWeek[w].GetValueOrDefault(divCode, 0L);
                if (useDivQuota && divPlacedThisWeek >= divTarget) continue;

                // Within a division, the variety bonus prefers brands not
                // yet picked this week (fresh first); otherwise oldest-first.
                IEnumerable<AnnotatedBox> ordered = divBoxList;
                if (req.ApplyVarietyBonus)
                {
                    var bdictForSort = brandQtyByWeek[w];
                    ordered = divBoxList
                        .OrderBy(a => bdictForSort.ContainsKey(a.Brand) ? 1 : 0)
                        .ThenBy(a => a.Box.LPMDt ?? DateTime.MaxValue)
                        .ThenBy(a => a.Box.BoxNo, StringComparer.Ordinal);
                }

                foreach (var a in ordered)
                {
                    if (placedBoxes.Contains(a.Box.BoxNo)) continue;
                    if (useDivQuota && divPlacedThisWeek >= divTarget) break;
                    if (weekQty[w] >= weekTargets[w]) break;             // week filled mid-loop

                    // Week-cap check (with 5 % overshoot tolerance — lets the
                    // last large box land slightly over rather than deferring).
                    if (weekQty[w] + a.Box.Qty > weekTargets[w] + (weekTargets[w] / 20))
                    {
                        lastSkipReason[a.Box.BoxNo] = "WEEK_FULL";
                        continue;
                    }
                    // Div-quota check — only enforced when MerchNeedWeek data exists.
                    // Allows the FIRST box per div per week to overshoot (if no
                    // boxes have landed yet in this div) so divisions with very
                    // small quotas still get at least one box per week.
                    if (useDivQuota && divPlacedThisWeek > 0
                        && divPlacedThisWeek + a.Box.Qty > divTarget + (divTarget / 20))
                    {
                        lastSkipReason[a.Box.BoxNo] = "DIV_QUOTA";
                        continue;
                    }
                    // Brand cap check.
                    var bdict = brandQtyByWeek[w];
                    bdict.TryGetValue(a.Brand, out var bSoFar);
                    if (bSoFar + a.Box.Qty > brandCapPerWeek[w])
                    {
                        lastSkipReason[a.Box.BoxNo] = "BRAND_CAP";
                        continue;
                    }

                    // Place box in this week.
                    allocs.Add(NewAlloc(a, week: w + 1, reason: "ALLOC", brandQtyAtPick: bSoFar));
                    placedBoxes.Add(a.Box.BoxNo);
                    weekQty[w]               += a.Box.Qty;
                    divPlacedThisWeek        += a.Box.Qty;
                    divQtyByWeek[w][divCode]  = divPlacedThisWeek;
                    bdict[a.Brand]            = bSoFar + a.Box.Qty;
                    lastSkipReason.Remove(a.Box.BoxNo);
                }
            }
        }

        // Sweep: any box without a week → defer with last-known skip reason
        // (or OVER_NEED if it was simply more supply than weekly need × NumWeeks).
        foreach (var a in annotated)
        {
            if (placedBoxes.Contains(a.Box.BoxNo)) continue;
            string reason;
            if (!a.DivCode.HasValue)
            {
                reason = "NO_DIV";
            }
            else if (lastSkipReason.TryGetValue(a.Box.BoxNo, out var r))
            {
                reason = r;
            }
            else
            {
                // Box was eligible but never even tried for any week — happens
                // when the supply for its division exceeds weekly-target × NumWeeks.
                reason = "OVER_NEED";
            }
            allocs.Add(NewAlloc(a, week: null, reason: reason, brandQtyAtPick: 0));
        }

        // ── 6) Persist run + box rows ──────────────────────────────────────
        var run = new LpmSimAdmRun
        {
            Country  = req.Country,
            RunDate  = req.RunDate.Date,
            RunYear  = runYear,
            RunMonth = runMonth,
            NumWeeks = req.NumWeeks,
            Status   = "Draft",
            Week1TargetPct    = req.Week1TargetPct,
            BrandCapPct       = req.BrandCapPct,
            ApplyVarietyBonus = req.ApplyVarietyBonus,
            TotalEligibleBoxes = boxes.Count,
            TotalEligibleQty   = totalQty,
            ScheduledBoxes     = allocs.Count(a => a.Week.HasValue),
            ScheduledQty       = allocs.Where(a => a.Week.HasValue).Sum(a => (long)a.BoxQty),
            DeferredBoxes      = allocs.Count(a => !a.Week.HasValue),
            DeferredQty        = allocs.Where(a => !a.Week.HasValue).Sum(a => (long)a.BoxQty),
            CreateTS  = DateTime.Now,
            CreatedBy = currentUser?.Name ?? "",
        };
        db.LpmSimAdmRuns.Add(run);
        await db.SaveChangesAsync(ct);  // get AdmRunNo

        foreach (var alloc in allocs) alloc.AdmRunNo = run.AdmRunNo;
        // Bulk insert via EF AddRange — usually a few hundred to a few thousand boxes,
        // so this is fast enough without dropping to SqlBulkCopy.
        db.LpmSimAdmBoxAllocs.AddRange(allocs);
        await db.SaveChangesAsync(ct);

        sw.Stop();
        return new GenerateResult
        {
            AdmRunNo = run.AdmRunNo,
            TotalEligibleBoxes = run.TotalEligibleBoxes,
            TotalEligibleQty   = run.TotalEligibleQty,
            ScheduledBoxes     = run.ScheduledBoxes,
            ScheduledQty       = run.ScheduledQty,
            DeferredBoxes      = run.DeferredBoxes,
            DeferredQty        = run.DeferredQty,
            WeekTargetQty      = weekTargetQty,
            TotalWeeklyNeed    = totalWeeklyNeed,
            DemandBound        = totalWeeklyNeed > 0 && weekTargetQty == demandWeekTarget,
            ElapsedMs          = sw.ElapsedMilliseconds,
        };
    }

    public async Task ApproveAsync(long admRunNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.LpmSimAdmRuns.FirstOrDefaultAsync(r => r.AdmRunNo == admRunNo, ct)
            ?? throw new InvalidOperationException($"ADM run #{admRunNo} not found.");
        if (run.Status == "Approved")
            throw new InvalidOperationException("ADM run is already approved.");
        run.Status = "Approved";
        run.ApprovedTS = DateTime.Now;
        run.ApprovedBy = currentUser?.Name ?? "";
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(long admRunNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(300);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM dbo.LPMSIM_AdmRun WHERE AdmRunNo = {admRunNo};", ct);
    }

    public async Task<LpmSimAdmRun?> GetRunAsync(string country, DateTime runDate, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LpmSimAdmRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Country == country && r.RunDate == runDate.Date, ct);
    }

    public async Task<List<LpmSimAdmBoxAlloc>> GetBoxesAsync(long admRunNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LpmSimAdmBoxAllocs.AsNoTracking()
            .Where(b => b.AdmRunNo == admRunNo)
            .OrderBy(b => b.Week ?? 999)
            .ThenBy(b => b.DivCode ?? 9999)
            .ThenByDescending(b => b.BoxQty)
            .ToListAsync(ct);
    }

    /// <summary>Per-week summary row for the result page.</summary>
    public sealed record WeekSummary(
        int Week, int Boxes, long Qty, int Divisions, int Brands);

    public async Task<List<WeekSummary>> GetWeekSummaryAsync(long admRunNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.LpmSimAdmBoxAllocs.AsNoTracking()
            .Where(b => b.AdmRunNo == admRunNo)
            .ToListAsync(ct);
        return rows
            .GroupBy(b => b.Week ?? 0)  // 0 = deferred
            .OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key)
            .Select(g => new WeekSummary(
                Week: g.Key,
                Boxes: g.Count(),
                Qty: g.Sum(x => (long)x.BoxQty),
                Divisions: g.Select(x => x.DivCode).Where(d => d.HasValue).Distinct().Count(),
                Brands: g.Select(x => x.LPMBrand).Where(b => !string.IsNullOrWhiteSpace(b)).Distinct().Count()))
            .ToList();
    }

    /// <summary>Per-(Week, Division) summary so the page can show variety per week.</summary>
    public sealed record WeekDivisionSummary(
        int Week, int? DivCode, string? Division,
        int Boxes, long Qty, decimal DivFillRatePct, decimal DivFillGapPct);

    public async Task<List<WeekDivisionSummary>> GetWeekDivisionSummaryAsync(long admRunNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.LpmSimAdmBoxAllocs.AsNoTracking()
            .Where(b => b.AdmRunNo == admRunNo)
            .ToListAsync(ct);
        return rows
            .GroupBy(b => new { Week = b.Week ?? 0, b.DivCode, b.Division })
            .OrderBy(g => g.Key.Week == 0 ? int.MaxValue : g.Key.Week)
            .ThenByDescending(g => g.Sum(x => (long)x.BoxQty))
            .Select(g => new WeekDivisionSummary(
                Week: g.Key.Week,
                DivCode: g.Key.DivCode,
                Division: g.Key.Division,
                Boxes: g.Count(),
                Qty: g.Sum(x => (long)x.BoxQty),
                // FillRate/Gap is constant per division within a run, so MAX is fine.
                DivFillRatePct: g.Max(x => x.DivFillRatePct),
                DivFillGapPct:  g.Max(x => x.DivFillGapPct)))
            .ToList();
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private static LpmSimAdmBoxAlloc NewAlloc(AnnotatedBox a, int? week, string reason, long brandQtyAtPick)
        => new()
        {
            Week        = week,
            BoxNo       = a.Box.BoxNo,
            Warehouse   = a.Box.Warehouse,
            LPMBrand    = a.Box.Brand,
            BoxQty      = a.Box.Qty,
            DaysInDC    = a.Box.LPMDt.HasValue ? Math.Max(0, (DateTime.Today - a.Box.LPMDt.Value).Days) : 0,
            LPMDt       = a.Box.LPMDt,
            DivCode     = a.DivCode,
            Division    = a.Division,
            DivFillRatePct = a.FillRatePct,
            DivFillGapPct  = a.FillGapPct,
            BrandQtyAtPick = brandQtyAtPick,
            Reason      = reason,
            CreateTS    = DateTime.Now,
        };

    private async Task<GenerateResult> PersistEmptyRunAsync(
        LpmDbContext db, GenerateRequest req, System.Diagnostics.Stopwatch sw, CancellationToken ct)
    {
        var run = new LpmSimAdmRun
        {
            Country  = req.Country,
            RunDate  = req.RunDate.Date,
            RunYear  = req.RunDate.Year,
            RunMonth = req.RunDate.Month,
            NumWeeks = req.NumWeeks,
            Status   = "Draft",
            Week1TargetPct    = req.Week1TargetPct,
            BrandCapPct       = req.BrandCapPct,
            ApplyVarietyBonus = req.ApplyVarietyBonus,
            CreateTS  = DateTime.Now,
            CreatedBy = currentUser?.Name ?? "",
        };
        db.LpmSimAdmRuns.Add(run);
        await db.SaveChangesAsync(ct);
        sw.Stop();
        return new GenerateResult { AdmRunNo = run.AdmRunNo, ElapsedMs = sw.ElapsedMilliseconds };
    }

    private record BoxRow(string BoxNo, string? Warehouse, string? Brand, int Qty, DateTime? LPMDt);
    private record DivStat(int DivCode, string Division, decimal FillRatePct, decimal FillGapPct, long WeekNeed);
    private record AnnotatedBox(BoxRow Box, int? DivCode, string? Division, string Brand,
                                decimal FillRatePct, decimal FillGapPct);
}
