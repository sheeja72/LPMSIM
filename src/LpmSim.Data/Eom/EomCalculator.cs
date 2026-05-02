using LpmSim.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data.Eom;

public class EomCalculator(IDbContextFactory<LpmDbContext> dbFactory)
{
    public async Task<EomReadiness> CheckAsync(string country, int year, int month, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var weights = await db.LpmMonthlyWeights.AsNoTracking()
            .Where(w => w.Country == country && w.RunYear == year && w.RunMonth == month)
            .ToListAsync(ct);
        var weightSum = weights.Sum(w => w.WeightPct);
        var weightsOk = weights.Count >= 1 && Math.Abs(weightSum - 1m) < 0.0001m;

        var storeIds = await db.DataSettings.AsNoTracking()
            .Where(s => s.ActiveStore == "Y" && s.Country == country)
            .Select(s => s.StoreID).Distinct().ToListAsync(ct);
        var divCount = await db.Divisions.AsNoTracking().CountAsync(ct);

        var planned = await db.LpmPlanneds.AsNoTracking()
            .Where(p => p.Country == country && p.Year1 == year && p.Month1 == month)
            .ToListAsync(ct);
        var plannedOk = planned.Count == divCount;

        int expectedSalesRows = storeIds.Count * divCount * Math.Max(1, weights.Count);
        int salesRows = 0;
        if (weights.Count > 0)
        {
            var periodKeys = weights.Select(w => new { w.PeriodYear, w.PeriodMonth }).ToList();
            var periodYears = periodKeys.Select(k => k.PeriodYear).ToList();
            var periodMonths = periodKeys.Select(k => k.PeriodMonth).ToList();
            salesRows = await db.LpmSalesTurns.AsNoTracking()
                .Where(s => storeIds.Contains(s.StoreID)
                         && periodYears.Contains(s.Year1)
                         && periodMonths.Contains(s.Month1))
                .CountAsync(ct);
        }
        var salesOk = weights.Count > 0 && salesRows > 0;

        var whStockCount = await db.LpmWHStocks.AsNoTracking()
            .Where(w => w.Country == country && w.Year1 == year && w.Month1 == month)
            .CountAsync(ct);
        var whOk = whStockCount == divCount;

        var activeGrades = await db.LpmStoreGrades.AsNoTracking()
            .Where(g => g.IsActive && g.Country == country).ToListAsync(ct);
        var gradeSum = activeGrades.Sum(g => g.SharePct);
        var gradesOk = activeGrades.Count > 0 && Math.Abs(gradeSum - 1m) < 0.0001m;

        var activeGroups = await db.LpmVolumeGroups.AsNoTracking()
            .Where(g => g.IsActive && g.Country == country).ToListAsync(ct);
        var groupSum = activeGroups.Sum(g => g.SharePct);
        var groupsOk = activeGroups.Count > 0 && Math.Abs(groupSum - 1m) < 0.0001m;

        var activeRules = await db.LpmSKUMaxRules.AsNoTracking()
            .Where(r => r.IsActive && r.Country == country)
            .ToListAsync(ct);
        var rulesOk = activeRules.Count > 0 &&
            activeGroups.All(g => activeRules.Any(r => r.GroupCode == g.GroupCode));

        return new EomReadiness
        {
            Weights = new(weightsOk,
                "Monthly Weights",
                weights.Count == 0
                    ? "No weights configured for this country+run."
                    : $"{weights.Count} periods, total = {(weightSum * 100m):0.##}% (must equal 100%)."),
            Planned = new(plannedOk,
                "Planned Inputs",
                $"{planned.Count} of {divCount} divisions configured."),
            SalesTurns = new(salesOk,
                "Sales & Turns",
                weights.Count == 0
                    ? "Weights must be defined first."
                    : $"{salesRows} rows present for the {weights.Count} weighted periods."),
            WHStock = new(whOk,
                "WH Stock",
                $"{whStockCount} of {divCount} divisions have stock for this period."),
            Grades = new(gradesOk,
                "Store Grades",
                activeGrades.Count == 0
                    ? "No active grades configured."
                    : $"{activeGrades.Count} active grades, share total = {(gradeSum * 100m):0.##}%."),
            VolumeGroups = new(groupsOk,
                "Volume Groups",
                activeGroups.Count == 0
                    ? "No active volume groups configured."
                    : $"{activeGroups.Count} active groups, share total = {(groupSum * 100m):0.##}%."),
            SkuMaxRules = new(rulesOk,
                "SKU Max Rules",
                activeRules.Count == 0
                    ? $"No active rules for {country}."
                    : $"{activeRules.Count} active rules for {country} across {activeRules.Select(r => r.DivCode).Distinct().Count()} division(s)."),
        };
    }

    /// <summary>Reads previously generated/saved rows from LPM_EOM_Output for a given (Country, Year, Month).</summary>
    public async Task<(List<EomRow> rows, DateTime? lastGeneratedTS)> GetSavedAsync(string country, int year, int month, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var stores = await db.DataSettings.AsNoTracking()
            .Where(s => s.Country == country && s.StoreID != null)
            .Select(s => new { s.StoreID, s.PBFullname })
            .Distinct()
            .ToListAsync(ct);
        var storeName = stores
            .Where(s => s.StoreID != null)
            .GroupBy(s => s.StoreID!)
            .ToDictionary(g => g.Key, g => g.First().PBFullname ?? "");

        var divNames = await db.Divisions.AsNoTracking()
            .ToDictionaryAsync(d => d.DivCode, d => d.Name ?? "", ct);

        var saved = await db.LpmEomOutputs.AsNoTracking()
            .Where(e => e.Country == country && e.Year1 == year && e.Month1 == month)
            .OrderBy(e => e.DivCode).ThenBy(e => e.StoreID)
            .ToListAsync(ct);

        var rows = saved.Select(e => new EomRow
        {
            Country      = e.Country ?? country,
            StoreID      = e.StoreID,
            StoreName    = storeName.TryGetValue(e.StoreID, out var n) ? n : "",
            DivCode      = e.DivCode,
            DivisionName = divNames.TryGetValue(e.DivCode, out var d) ? d : e.DivCode.ToString(),
            Year         = e.Year1,
            Month        = e.Month1,
            WtAvgSoldQty = e.WtAvgSoldQty ?? 0m,
            WtAvgTurn    = e.WtAvgTurn ?? 0m,
            SoldQtyRank  = e.SoldQtyRank ?? 0,
            TurnsRank    = e.TurnsRank ?? 0,
            PriorityRank = e.PriorityRank ?? 0m,
            TargetTurn   = e.TargetTurn ?? 0m,
            TargetSales  = e.TargetSales ?? 0m,
            TargetEOM    = e.TargetEOM ?? 0m,
            VolumeGroup  = e.VolumeGroup ?? "",
            WHStock      = e.WHStock ?? 0,
            WHStockSummer= e.WHStockSummer ?? 0,
            WHStockWinter= e.WHStockWinter ?? 0,
            SKUMax       = e.SKUMax ?? 0,
            Grade        = e.Grade ?? "",
        }).ToList();

        var last = saved.Count > 0 ? saved.Max(e => e.CreateTS) : (DateTime?)null;
        return (rows, last);
    }

    public async Task<List<EomRow>> PreviewAsync(string country, int year, int month, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await CalculateAsync(db, country, year, month, ct);
    }

    public async Task<int> GenerateAsync(string country, int year, int month, string user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await CalculateAsync(db, country, year, month, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var existing = await db.LpmEomOutputs
            .Where(e => e.Country == country && e.Year1 == year && e.Month1 == month)
            .ToListAsync(ct);
        if (existing.Count > 0) db.LpmEomOutputs.RemoveRange(existing);

        var now = DateTime.Now;
        foreach (var r in rows)
        {
            db.LpmEomOutputs.Add(new LpmEomOutput
            {
                StoreID       = r.StoreID,
                DivCode       = r.DivCode,
                Year1         = r.Year,
                Month1        = r.Month,
                Country       = r.Country,
                WtAvgSoldQty  = r.WtAvgSoldQty,
                WtAvgTurn     = r.WtAvgTurn,
                SoldQtyRank   = r.SoldQtyRank,
                TurnsRank     = r.TurnsRank,
                PriorityRank  = r.PriorityRank,
                TargetTurn    = r.TargetTurn,
                TargetSales   = r.TargetSales,
                TargetEOM     = r.TargetEOM,
                VolumeGroup   = r.VolumeGroup,
                WHStock       = r.WHStock,
                WHStockSummer = r.WHStockSummer,
                WHStockWinter = r.WHStockWinter,
                SKUMax        = r.SKUMax,
                Grade         = r.Grade,
                CreateTS      = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Count;
    }

    private static async Task<List<EomRow>> CalculateAsync(
        LpmDbContext db, string country, int year, int month, CancellationToken ct)
    {
        // Load inputs in parallel-ish (same context, sequential but short).
        var weights = await db.LpmMonthlyWeights.AsNoTracking()
            .Where(w => w.Country == country && w.RunYear == year && w.RunMonth == month)
            .OrderBy(w => w.PeriodSeq).ToListAsync(ct);

        var stores = await db.DataSettings.AsNoTracking()
            .Where(s => s.ActiveStore == "Y" && s.Country == country)
            .Select(s => new { s.StoreID, s.PBFullname })
            .Distinct()
            .ToListAsync(ct);

        var divisions = await db.Divisions.AsNoTracking()
            .OrderBy(d => d.DivCode)
            .ToListAsync(ct);

        var storeIds = stores.Select(s => s.StoreID).ToList();
        var periodYears  = weights.Select(w => w.PeriodYear).Distinct().ToList();
        var periodMonths = weights.Select(w => w.PeriodMonth).Distinct().ToList();

        var salesTurns = (await db.LpmSalesTurns.AsNoTracking()
            .Where(s => storeIds.Contains(s.StoreID)
                     && periodYears.Contains(s.Year1)
                     && periodMonths.Contains(s.Month1))
            .ToListAsync(ct))
            .GroupBy(s => (s.StoreID, s.DivCode, s.Year1, s.Month1))
            .ToDictionary(g => g.Key, g => g.First());

        var planned = await db.LpmPlanneds.AsNoTracking()
            .Where(p => p.Country == country && p.Year1 == year && p.Month1 == month)
            .ToDictionaryAsync(p => p.DivCode, ct);

        // WH stock per Division comes LIVE from racks.dbo.whboxitems (the same
        // table SIM Generate consumes), split by pallettype.Season:
        //
        //   Filters (mirror SIM eligibility):
        //     pt.PalletCategory = 'ELIGIBLE'
        //     ShopEligible <> 'E'
        //     LPMDt IS NULL  OR  (Year/Month <= run period)         ← LPM + Non-LPM combined
        //   Item → Division: upc_subclass × subclassmaster × Division (MIN(DivCode) per item).
        //
        // Returns one bucket per (DivCode, Season). The legacy LPM_WHStock
        // table is no longer consulted (still queryable from SSMS, but the
        // EOM engine ignores it as of this change).
        var whStockBySeason = new Dictionary<int, (long Summer, long Winter)>();
        {
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
WITH ItemDiv AS (
    SELECT u.itemcode, MIN(d.DivCode) AS DivCode
      FROM Datareporting.dbo.upc_subclass    u
      INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
      INNER JOIN dbo.Division                 d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
     GROUP BY u.itemcode
)
SELECT id.DivCode,
       Season = CASE WHEN ISNULL(pt.Season, '') = 'W' THEN 'W' ELSE 'S' END,
       Qty    = SUM(CAST(ISNULL(w.Qty,0) AS bigint))
  FROM racks.dbo.whboxitems w
  INNER JOIN bfldata.dbo.pallettype  pt ON pt.PalletType = w.PalletType
  INNER JOIN ItemDiv id                 ON id.itemcode = w.ItemCode
 WHERE pt.PalletCategory = 'ELIGIBLE'
   AND ISNULL(w.ShopEligible, '') <> 'E'
   AND ( w.LPMDt IS NULL
      OR (YEAR(w.LPMDt) < @y OR (YEAR(w.LPMDt) = @y AND MONTH(w.LPMDt) <= @m)) )
 GROUP BY id.DivCode, CASE WHEN ISNULL(pt.Season, '') = 'W' THEN 'W' ELSE 'S' END;";
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@y", year));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@m", month));
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                if (rdr.IsDBNull(0)) continue;
                var divCode = rdr.GetInt32(0);
                var season  = rdr.IsDBNull(1) ? "S" : rdr.GetString(1);
                var qty     = rdr.IsDBNull(2) ? 0L : rdr.GetInt64(2);
                whStockBySeason.TryGetValue(divCode, out var cur);
                whStockBySeason[divCode] = season == "W"
                    ? (cur.Summer, cur.Winter + qty)
                    : (cur.Summer + qty, cur.Winter);
            }
        }
        // Backwards-compatible single value used by the SKU Max range lookup.
        var whStock = whStockBySeason.ToDictionary(
            kv => kv.Key,
            kv => (int)Math.Min(int.MaxValue, kv.Value.Summer + kv.Value.Winter));

        var grades = (await db.LpmStoreGrades.AsNoTracking()
            .Where(g => g.IsActive && g.Country == country).OrderBy(g => g.SortOrder)
            .ToListAsync(ct));
        var volumeGroups = (await db.LpmVolumeGroups.AsNoTracking()
            .Where(g => g.IsActive && g.Country == country).OrderBy(g => g.SortOrder)
            .ToListAsync(ct));
        var skuRules = await db.LpmSKUMaxRules.AsNoTracking()
            .Where(r => r.IsActive && r.Country == country)
            .ToListAsync(ct);

        var weeksInMonth = Math.Max(1, DateTime.DaysInMonth(year, month) / 7);

        // Number of periods with a positive weight — the divisor for the
        // weighted MONTHLY AVERAGE. We divide the weighted sum by this count
        // (NOT by SUM(WeightPct)) so the column reads as a per-period
        // average rather than a normalised weighted total.
        var periodCount = weights.Count(w => w.WeightPct > 0m);

        // Build (Store × Division) grid with Step 1 weighted averages.
        var rows = new List<EomRow>(stores.Count * divisions.Count);
        foreach (var s in stores)
        foreach (var d in divisions)
        {
            decimal wtSold = 0m, wtTurn = 0m;
            foreach (var w in weights)
            {
                if (salesTurns.TryGetValue((s.StoreID, d.DivCode, w.PeriodYear, w.PeriodMonth), out var st))
                {
                    wtSold += (st.SoldQty ?? 0m) * w.WeightPct;
                    wtTurn += (st.TurnsQty ?? 0m) * w.WeightPct;
                }
            }
            if (periodCount > 0)
            {
                wtSold /= periodCount;
                wtTurn /= periodCount;
            }
            rows.Add(new EomRow
            {
                Country      = country,
                StoreID      = s.StoreID,
                StoreName    = s.PBFullname ?? "",
                DivCode      = d.DivCode,
                DivisionName = d.Name ?? "",
                Year         = year,
                Month        = month,
                WtAvgSoldQty = wtSold,
                WtAvgTurn    = wtTurn,
            });
        }

        // Step 1: ranks within each division.
        // Sold Qty Rank: higher = rank 1 (descending, dense-with-ties).
        // Turns Rank:    lower  = rank 1 (ascending,  dense-with-ties).
        foreach (var grp in rows.GroupBy(r => r.DivCode))
        {
            AssignRanks(grp.ToList(), r => r.WtAvgSoldQty, (r, rank) => r.SoldQtyRank = rank, descending: true);
            AssignRanks(grp.ToList(), r => r.WtAvgTurn,    (r, rank) => r.TurnsRank   = rank, descending: false);
        }

        // PriorityRank — average of the two ranks, but only counting ranks
        // that actually apply. SoldQtyRank applies only when WtAvgSold > 0;
        // TurnsRank applies only when WtAvgTurn > 0. AssignRanks sets the
        // rank to 0 when its source value is non-positive (= "no rank").
        //
        //   both > 0  →  PriorityRank = (SoldQtyRank + TurnsRank) / 2
        //   only Sold→  PriorityRank = SoldQtyRank
        //   only Turn→  PriorityRank = TurnsRank
        //   neither   →  PriorityRank = 0  (no historical signal — TargetEOM
        //                                   for these rows is also typically 0,
        //                                   so SIM Generate won't allocate to
        //                                   them anyway, but downstream code
        //                                   can treat 0 as "unranked").
        foreach (var r in rows)
        {
            var hasSold  = r.SoldQtyRank > 0;
            var hasTurns = r.TurnsRank   > 0;
            r.PriorityRank = (hasSold, hasTurns) switch
            {
                (true,  true ) => (r.SoldQtyRank + r.TurnsRank) / 2m,
                (true,  false) => r.SoldQtyRank,
                (false, true ) => r.TurnsRank,
                _              => 0m,
            };
        }

        // Step 2: assign grade by PriorityRank asc (tiebreak WtAvgSoldQty desc)
        // within each division. PriorityRank == 0 means the store had no
        // historical signal (both Sold and Turn were 0), so it is excluded
        // from grade bucketing — those rows keep Grade = "" so downstream
        // calculations treat them as unranked / no markup.
        foreach (var grp in rows.GroupBy(r => r.DivCode))
        {
            var ranked = grp.Where(r => r.PriorityRank > 0m)
                            .OrderBy(r => r.PriorityRank)
                            .ThenByDescending(r => r.WtAvgSoldQty)
                            .ToList();
            AssignBuckets(ranked, grades.Select(g => (g.GradeCode, g.SharePct)).ToList(),
                          (r, code) => r.Grade = code);
            // Defensive: any unranked row stays with the default empty Grade.
            // (No need to reset — EomRow.Grade defaults to "" and we never
            // touched it for unranked rows above.)
        }

        // TargetTurn per grade markup.
        var markupByGrade = grades.ToDictionary(g => g.GradeCode, g => g.MarkupPct);
        foreach (var r in rows)
        {
            if (!planned.TryGetValue(r.DivCode, out var p)) continue;
            var markup = markupByGrade.GetValueOrDefault(r.Grade, 0m);
            r.TargetTurn = p.PlannedTurn * (1m - markup);
        }

        // Step 3: TargetSales distributed by store's share of division's total WtAvgSoldQty.
        foreach (var grp in rows.GroupBy(r => r.DivCode))
        {
            if (!planned.TryGetValue(grp.Key, out var p)) continue;
            var totalWt = grp.Sum(r => r.WtAvgSoldQty);
            if (totalWt <= 0m) continue;
            foreach (var r in grp)
                r.TargetSales = (r.WtAvgSoldQty / totalWt) * p.PlannedSalesQty;
        }

        // Step 4: Initial EOM per store, then distribute total Planned EOM by share.
        foreach (var grp in rows.GroupBy(r => r.DivCode))
        {
            if (!planned.TryGetValue(grp.Key, out var p)) continue;
            var list = grp.ToList();
            var initials = list.Select(r => (r.TargetTurn * r.TargetSales) / weeksInMonth).ToList();
            var totalInitial = initials.Sum();
            if (totalInitial <= 0m) continue;
            for (int i = 0; i < list.Count; i++)
                list[i].TargetEOM = (initials[i] / totalInitial) * p.PlannedEOM;
        }

        // Step 5: Volume Group by TargetEOM desc, per division.
        foreach (var grp in rows.GroupBy(r => r.DivCode))
        {
            var ordered = grp.OrderByDescending(r => r.TargetEOM).ToList();
            AssignBuckets(ordered, volumeGroups.Select(g => (g.GroupCode, g.SharePct)).ToList(),
                          (r, code) => r.VolumeGroup = code);
        }

        // Step 6: SKU Max = lookup by (Country, DivCode, VolumeGroup, WHStock range).
        var ruleIdx = skuRules
            .GroupBy(r => (r.DivCode, r.GroupCode))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.WHStockFrom).ToList());
        // Per-(Country, Store, Div) deactivations from LPM_StoreDivAccess.
        // A row here with IsActive = 0 forces SKUMax to 0 → SIM cannot allocate
        // the division to that store. Default (no row) = active, full SKUMax.
        var deactivated = await db.LpmStoreDivAccesses.AsNoTracking()
            .Where(a => a.Country == country && !a.IsActive)
            .Select(a => new { a.StoreID, a.DivCode })
            .ToListAsync(ct);
        var deactivatedSet = new HashSet<(string Store, int Div)>(
            deactivated.Select(a => (a.StoreID, a.DivCode)));

        foreach (var r in rows)
        {
            r.WHStock = whStock.GetValueOrDefault(r.DivCode, 0);
            (long Summer, long Winter) seasonal = whStockBySeason.TryGetValue(r.DivCode, out var sw)
                ? sw
                : (0L, 0L);
            r.WHStockSummer = (int)Math.Min(int.MaxValue, seasonal.Summer);
            r.WHStockWinter = (int)Math.Min(int.MaxValue, seasonal.Winter);
            if (ruleIdx.TryGetValue((r.DivCode, r.VolumeGroup), out var rules))
            {
                var match = rules.FirstOrDefault(x => r.WHStock >= x.WHStockFrom && r.WHStock <= x.WHStockTo);
                r.SKUMax = match?.SKUMax ?? 0;
            }
            // Apply the deactivation override last so it wins regardless of
            // any SKU Max rule match. SIM Generate's per-store skuBalance
            // (SKUMax − SOH − cumItem) becomes ≤ 0 → store is skipped on
            // every allocation cycle for this division.
            if (deactivatedSet.Contains((r.StoreID, r.DivCode)))
                r.SKUMax = 0;
        }

        return rows;
    }

    /// <summary>
    /// Standard competition ranking ("1224"): ties share a rank, the next
    /// distinct value's rank equals its position (so it skips by tie count).
    /// Stores whose key value is &lt;= 0 are NOT ranked — they get rank = 0
    /// (sentinel "no rank") and are pushed to the bottom of priority order
    /// when the two ranks are averaged into PriorityRank.
    /// </summary>
    private static void AssignRanks<T>(List<T> items, Func<T, decimal> key, Action<T, int> set, bool descending)
    {
        // Split: only positive-key items get ranked. Non-positive get rank 0.
        var rankable = items.Where(x => key(x) > 0m).ToList();
        var unranked = items.Where(x => key(x) <= 0m);

        foreach (var u in unranked) set(u, 0);

        var sorted = descending
            ? rankable.OrderByDescending(key).ToList()
            : rankable.OrderBy(key).ToList();
        int rank = 1;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (i > 0 && key(sorted[i]) != key(sorted[i - 1]))
                rank = i + 1;
            set(sorted[i], rank);
        }
    }

    /// <summary>
    /// Distributes an ordered list across buckets by share %. The last bucket absorbs rounding slack.
    /// </summary>
    private static void AssignBuckets<T>(
        List<T> ordered,
        List<(string Code, decimal Share)> buckets,
        Action<T, string> set)
    {
        if (ordered.Count == 0 || buckets.Count == 0) return;
        int total = ordered.Count;
        int pos = 0;
        for (int b = 0; b < buckets.Count; b++)
        {
            int count = b == buckets.Count - 1
                ? ordered.Count - pos
                : (int)Math.Round(total * buckets[b].Share, MidpointRounding.AwayFromZero);
            for (int i = pos; i < Math.Min(pos + count, ordered.Count); i++)
                set(ordered[i], buckets[b].Code);
            pos += count;
        }
        // Anything left (shouldn't happen due to last-bucket absorbs) → last bucket.
        var lastCode = buckets[^1].Code;
        for (int i = pos; i < ordered.Count; i++)
            set(ordered[i], lastCode);
    }
}
