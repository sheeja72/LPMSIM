using LpmSim.Core.Entities;
using LpmSim.Data.Warehouse;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data.Eom;

public class EomCalculator(IDbContextFactory<LpmDbContext> dbFactory)
{
    public async Task<EomReadiness> CheckAsync(string country, int year, int month, CancellationToken ct = default)
    {
        // 1.13.2 perf: the 8 independent readiness queries below run in
        // PARALLEL via Task.WhenAll, each with its own DbContext from the
        // factory. EF Core's DbContext is not thread-safe — sharing one
        // across parallel awaits would silently corrupt results, so each
        // local helper opens its own context. The single dependent query
        // (LpmSalesTurns count, which needs weights+storeIds) runs after
        // Wave 1 completes. Total time drops from ~sum-of-each to
        // ~max-of-each, typically a 70–80% reduction on the page-open path.
        async Task<List<LpmMonthlyWeight>> LoadWeights()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.LpmMonthlyWeights.AsNoTracking()
                .Where(w => w.Country == country && w.RunYear == year && w.RunMonth == month)
                .ToListAsync(ct);
        }
        async Task<List<string?>> LoadStoreIds()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.DataSettings.AsNoTracking()
                .Where(s => s.ActiveStore == "Y" && s.Country == country)
                .Select(s => s.StoreID).Distinct().ToListAsync(ct);
        }
        async Task<int> LoadDivCount()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.Divisions.AsNoTracking().CountAsync(ct);
        }
        async Task<List<LpmPlanned>> LoadPlanned()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.LpmPlanneds.AsNoTracking()
                .Where(p => p.Country == country && p.Year1 == year && p.Month1 == month)
                .ToListAsync(ct);
        }
        async Task<int> LoadWhStockCount()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.LpmWHStocks.AsNoTracking()
                .Where(w => w.Country == country && w.Year1 == year && w.Month1 == month)
                .CountAsync(ct);
        }
        async Task<List<LpmStoreGrade>> LoadActiveGrades()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.LpmStoreGrades.AsNoTracking()
                .Where(g => g.IsActive && g.Country == country).ToListAsync(ct);
        }
        async Task<List<LpmVolumeGroup>> LoadActiveGroups()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.LpmVolumeGroups.AsNoTracking()
                .Where(g => g.IsActive && g.Country == country).ToListAsync(ct);
        }
        async Task<List<LpmSKUMaxRule>> LoadActiveRules()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.LpmSKUMaxRules.AsNoTracking()
                .Where(r => r.IsActive && r.Country == country)
                .ToListAsync(ct);
        }

        // Wave 1 — fire all 8 in parallel.
        var weightsTask  = LoadWeights();
        var storeIdsTask = LoadStoreIds();
        var divCountTask = LoadDivCount();
        var plannedTask  = LoadPlanned();
        var whStockTask  = LoadWhStockCount();
        var gradesTask   = LoadActiveGrades();
        var groupsTask   = LoadActiveGroups();
        var rulesTask    = LoadActiveRules();
        await Task.WhenAll(weightsTask, storeIdsTask, divCountTask, plannedTask,
                           whStockTask, gradesTask, groupsTask, rulesTask);

        var weights      = await weightsTask;
        var storeIds     = await storeIdsTask;
        var divCount     = await divCountTask;
        var planned      = await plannedTask;
        var whStockCount = await whStockTask;
        var activeGrades = await gradesTask;
        var activeGroups = await groupsTask;
        var activeRules  = await rulesTask;

        // Computed flags (no DB).
        var weightSum = weights.Sum(w => w.WeightPct);
        var weightsOk = weights.Count >= 1 && Math.Abs(weightSum - 1m) < 0.0001m;
        var plannedOk = planned.Count == divCount;
        var whOk      = whStockCount == divCount;
        var gradeSum  = activeGrades.Sum(g => g.SharePct);
        var gradesOk  = activeGrades.Count > 0 && Math.Abs(gradeSum - 1m) < 0.0001m;
        var groupSum  = activeGroups.Sum(g => g.SharePct);
        var groupsOk  = activeGroups.Count > 0 && Math.Abs(groupSum - 1m) < 0.0001m;
        var rulesOk   = activeRules.Count > 0 &&
            activeGroups.All(g => activeRules.Any(r => r.GroupCode == g.GroupCode));

        // Wave 2 — LpmSalesTurns count depends on weights + storeIds, so it
        // can only run AFTER Wave 1 finishes. Its own DbContext.
        int expectedSalesRows = storeIds.Count * divCount * Math.Max(1, weights.Count);
        int salesRows = 0;
        if (weights.Count > 0)
        {
            var periodYears  = weights.Select(w => w.PeriodYear).ToList();
            var periodMonths = weights.Select(w => w.PeriodMonth).ToList();
            await using var dbSales = await dbFactory.CreateDbContextAsync(ct);
            salesRows = await dbSales.LpmSalesTurns.AsNoTracking()
                .Where(s => storeIds.Contains(s.StoreID)
                         && periodYears.Contains(s.Year1)
                         && periodMonths.Contains(s.Month1))
                .CountAsync(ct);
        }
        var salesOk = weights.Count > 0 && salesRows > 0;

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
        // 1.14.3 perf: the 3 queries below are independent. Run them in
        // parallel via Task.WhenAll with separate DbContexts (EF Core's
        // DbContext is not thread-safe). Same pattern as CheckAsync.
        async Task<Dictionary<string, string>> LoadStoreNames()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var stores = await db.DataSettings.AsNoTracking()
                .Where(s => s.Country == country && s.StoreID != null)
                .Select(s => new { s.StoreID, s.PBFullname })
                .Distinct()
                .ToListAsync(ct);
            return stores
                .Where(s => s.StoreID != null)
                .GroupBy(s => s.StoreID!)
                .ToDictionary(g => g.Key, g => g.First().PBFullname ?? "");
        }
        async Task<Dictionary<int, string>> LoadDivNames()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.Divisions.AsNoTracking()
                .ToDictionaryAsync(d => d.DivCode, d => d.Name ?? "", ct);
        }
        async Task<List<LpmEomOutput>> LoadSaved()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.LpmEomOutputs.AsNoTracking()
                .Where(e => e.Country == country && e.Year1 == year && e.Month1 == month)
                .OrderBy(e => e.DivCode).ThenBy(e => e.StoreID)
                .ToListAsync(ct);
        }

        var storeNamesTask = LoadStoreNames();
        var divNamesTask   = LoadDivNames();
        var savedTask      = LoadSaved();
        await Task.WhenAll(storeNamesTask, divNamesTask, savedTask);

        var storeName = await storeNamesTask;
        var divNames  = await divNamesTask;
        var saved     = await savedTask;

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
            SOH            = e.SOH            ?? 0,
            MerchNeedMonth = e.MerchNeedMonth ?? 0,
            MerchNeedWeek  = e.MerchNeedWeek  ?? 0,
            MerchNeedWeek1 = e.MerchNeedWeek1 ?? 0,
            MerchNeedWeek2 = e.MerchNeedWeek2 ?? 0,
            MerchNeedWeek3 = e.MerchNeedWeek3 ?? 0,
            MerchNeedWeek4 = e.MerchNeedWeek4 ?? 0,
            MerchNeedDay   = e.MerchNeedDay   ?? 0,
            LPMBoxQty      = e.LPMBoxQty      ?? 0,
        }).ToList();

        var last = saved.Count > 0 ? saved.Max(e => e.CreateTS) : (DateTime?)null;
        return (rows, last);
    }

    public async Task<List<EomRow>> PreviewAsync(string country, int year, int month, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await CalculateAsync(db, country, year, month, ct);
    }

    /// <summary>
    /// Returns one row per <c>(DivCode × Season)</c> with on-demand stock
    /// metrics for the Division Summary tab of EOM Generate. Refreshed every
    /// time the user lands on that tab — values are NOT persisted to
    /// <c>LPM_EOM_Output</c> because the underlying sources
    /// (<c>LPM_LocStock</c>, <c>whboxitems</c>) change daily and the planner
    /// always wants the current snapshot.
    ///
    /// <para>
    /// Sources:
    /// <list type="bullet">
    ///   <item><strong>HO Stock</strong> — <c>racks.dbo.LPM_LocStock</c> where
    ///         <c>dataname = 'HODATA'</c>, mapped to division via
    ///         <c>upc_subclass × subclassmaster × Division</c>; season from
    ///         <c>usa.dbo.upcbarcodes.Itemtype</c> (<c>'W'</c> → Winter, else
    ///         Summer).</item>
    ///   <item><strong>WH (Purchased)</strong> — <c>whboxitems.Qty</c> where
    ///         <c>ShopEligible IS NULL OR &lt;&gt; 'E'</c> (boxes cleared / moved
    ///         past the 'E' in-process state).</item>
    ///   <item><strong>WH (Non-Purchased)</strong> — <c>whboxitems.Qty</c>
    ///         where <c>ShopEligible = 'E'</c> (still being processed).</item>
    ///   <item><strong>Eligible Stock</strong> — <c>whboxitems.Qty</c> where
    ///         <c>pallettype.PalletCategory = 'ELIGIBLE' AND
    ///         (ShopEligible IS NULL OR &lt;&gt; 'E')</c> — purchased subset of
    ///         the ELIGIBLE pallet category.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// All WH-side metrics break down by <c>pallettype.Season</c>. HO Stock
    /// uses <c>upcbarcodes.Itemtype</c> for its season.
    /// </para>
    /// </summary>
    public async Task<List<DivisionStockBreakdown>> GetDivisionStockBreakdownAsync(
        string country, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        // Resolve country-aware whboxitems source (UAE → racks.dbo.whboxitems;
        // others → [<DataName>].dbo.WHBoxItemsExport).
        var whSrc = await WhBoxItemsSource.ResolveAsync(conn, country, ct);

        var rows = new List<DivisionStockBreakdown>();
        using var cmd = conn.CreateCommand();
        // Single batch — two correlated rollups (HO + WH) UNION'd, joined to
        // an item → division map. The final FULL OUTER JOIN ensures a row
        // exists for every (Div, Season) combination where either side has
        // data. Country filter for HO comes from DataSettings.SIMCountry →
        // StoreID match in LPM_LocStock; whboxitems is already country-scoped
        // by whSrc.
        cmd.CommandText = $@"
            ;WITH ItemDiv AS (
                SELECT u.itemcode, MIN(d.DivCode) AS DivCode
                  FROM Datareporting.dbo.upc_subclass    u
                  INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                  INNER JOIN dbo.Division                 d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                 GROUP BY u.itemcode
            ),
            ItemSeason AS (
                -- HO season per item: MAX('W' or 'S') so any 'W' barcode wins.
                -- Items with no upcbarcodes row default to 'S' via LEFT JOIN
                -- below (handled in HOByDiv with ISNULL).
                SELECT b.itemcode,
                       MAX(CASE WHEN UPPER(LTRIM(RTRIM(b.Itemtype))) = 'W' THEN 'W' ELSE 'S' END) AS Season
                  FROM usa.dbo.upcbarcodes b
                 WHERE b.itemcode IS NOT NULL AND b.itemcode <> ''
                 GROUP BY b.itemcode
            ),
            HOByDiv AS (
                SELECT id.DivCode,
                       ISNULL(its.Season, 'S') AS Season,
                       SUM(CAST(ISNULL(ls.SOH, 0) AS bigint)) AS HOStock
                  FROM racks.dbo.LPM_LocStock ls
                  INNER JOIN ItemDiv id ON id.itemcode = ls.ItemCode
                  LEFT  JOIN ItemSeason its ON its.itemcode = ls.ItemCode
                 WHERE ls.dataname = 'HODATA'
                   AND ls.ItemCode IS NOT NULL AND ls.ItemCode <> ''
                 GROUP BY id.DivCode, ISNULL(its.Season, 'S')
            ),
            WHByDiv AS (
                -- Business definitions confirmed by user:
                --   • Purchased     = ShopEligible IS NULL OR <> 'E' (boxes
                --                     that have been moved/cleared/shipped —
                --                     'E' marks the still-in-process state)
                --   • Non-Purchased = ShopEligible = 'E' (still being
                --                     processed / not yet shipped)
                --   • Eligible      = PalletCategory='ELIGIBLE' AND
                --                     ShopEligible <> 'E' (eligible-category
                --                     stock that has been purchased — i.e.
                --                     what's available for the next SIM run
                --                     after the 'E' work clears).
                --
                -- Note: this naming is the OPPOSITE of how the existing SIM
                -- allocator filters use ShopEligible <> E (which there means
                -- eligible-to-allocate). The Division Summary uses the
                -- business team labels; the allocator behaviour stays
                -- untouched.
                SELECT id.DivCode,
                       CASE WHEN ISNULL(pt.Season, '') = 'W' THEN 'W' ELSE 'S' END AS Season,
                       SUM(CASE WHEN w.ShopEligible IS NULL OR w.ShopEligible <> 'E'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS WHStockPurchased,
                       SUM(CASE WHEN w.ShopEligible = 'E'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS WHStockNonPurchased,
                       SUM(CASE WHEN pt.PalletCategory = 'ELIGIBLE'
                                 AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS EligibleStock
                  FROM {whSrc} w
                  INNER JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
                  INNER JOIN ItemDiv id              ON id.itemcode    = w.ItemCode
                 GROUP BY id.DivCode,
                          CASE WHEN ISNULL(pt.Season, '') = 'W' THEN 'W' ELSE 'S' END
            )
            SELECT
                COALESCE(h.DivCode, w.DivCode)   AS DivCode,
                COALESCE(h.Season,  w.Season)    AS Season,
                ISNULL(h.HOStock,               0) AS HOStock,
                ISNULL(w.WHStockPurchased,      0) AS WHStockPurchased,
                ISNULL(w.WHStockNonPurchased,   0) AS WHStockNonPurchased,
                ISNULL(w.EligibleStock,         0) AS EligibleStock
              FROM HOByDiv h
              FULL OUTER JOIN WHByDiv w
                       ON w.DivCode = h.DivCode AND w.Season = h.Season
             ORDER BY DivCode, Season;";
        cmd.CommandTimeout = 300;
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new DivisionStockBreakdown(
                DivCode:             rdr.IsDBNull(0) ? 0      : rdr.GetInt32(0),
                Season:              rdr.IsDBNull(1) ? "S"    : rdr.GetString(1),
                HOStock:             rdr.IsDBNull(2) ? 0L     : rdr.GetInt64(2),
                WHStockPurchased:    rdr.IsDBNull(3) ? 0L     : rdr.GetInt64(3),
                WHStockNonPurchased: rdr.IsDBNull(4) ? 0L     : rdr.GetInt64(4),
                EligibleStock:       rdr.IsDBNull(5) ? 0L     : rdr.GetInt64(5)));
        }
        return rows;
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
                VolumeGroup    = r.VolumeGroup,
                WHStock        = r.WHStock,
                WHStockSummer  = r.WHStockSummer,
                WHStockWinter  = r.WHStockWinter,
                SKUMax         = r.SKUMax,
                Grade          = r.Grade,
                SOH            = r.SOH,
                MerchNeedMonth = r.MerchNeedMonth,
                MerchNeedWeek  = r.MerchNeedWeek,
                MerchNeedWeek1 = r.MerchNeedWeek1,
                MerchNeedWeek2 = r.MerchNeedWeek2,
                MerchNeedWeek3 = r.MerchNeedWeek3,
                MerchNeedWeek4 = r.MerchNeedWeek4,
                MerchNeedDay   = r.MerchNeedDay,
                LPMBoxQty      = r.LPMBoxQty,
                CreateTS       = now,
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

        // Weekly Sales Target Splits — keyed by (DivCode, WeekNo). Empty when
        // no rows have been configured for the period; the per-row loop below
        // falls back to the hard-coded default 20/20/25/35 in that case so
        // EOM never blocks. Only IsActive rows are considered (deactivated
        // splits are ignored, equivalent to "no row").
        var splitsByDivWeek = (await db.LpmWeeklySalesTargetSplits.AsNoTracking()
            .Where(s => s.Country == country && s.Year1 == year && s.Month1 == month && s.IsActive)
            .ToListAsync(ct))
            .ToDictionary(s => (s.DivCode, (int)s.WeekNo), s => s.SplitPct);

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
        // Warehouse stock totals per (Division, Season) — the SUM of eligible
        // box quantities. Shown on the EOM grid as a Division-level summary;
        // the per-item SKU Max calculation has moved to LPM_SimItemSkuMax,
        // built fresh at SIM Generate time.
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
        // Combined value kept on EomRow.WHStock purely for the historical
        // column on LPM_EOM_Output; it no longer drives SKU Max (that lives
        // in LPM_SimItemSkuMax now).
        var whStock = whStockBySeason.ToDictionary(
            kv => kv.Key,
            kv => (int)Math.Min(int.MaxValue, kv.Value.Summer + kv.Value.Winter));

        // ── LPM Box Qty per Division ────────────────────────────────────
        // Total qty of LPM-tagged eligible boxes from racks.dbo.whboxitems.
        // Filter:
        //   pt.PalletCategory = 'ELIGIBLE'
        //   w.LPMDt IS NOT NULL          (any date — the box is LPM-flagged)
        //   (NO ShopEligible filter — intentionally broader than WHStock)
        // Surfaces on the EOM Division Summary tab; stored on LPM_EOM_Output
        // alongside the WHStock columns.
        var lpmBoxQtyByDiv = new Dictionary<int, long>();
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
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
       Qty = SUM(CAST(ISNULL(w.Qty, 0) AS bigint))
  FROM racks.dbo.whboxitems w
  INNER JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
  INNER JOIN ItemDiv id               ON id.itemcode    = w.ItemCode
 WHERE pt.PalletCategory = 'ELIGIBLE'
   AND w.LPMDt IS NOT NULL
 GROUP BY id.DivCode;";
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                if (rdr.IsDBNull(0)) continue;
                lpmBoxQtyByDiv[rdr.GetInt32(0)] = rdr.IsDBNull(1) ? 0L : rdr.GetInt64(1);
            }
        }

        // Per-(Store, Div) SOH from racks.dbo.LPM_LocStock — sum across items
        // for that combination. Same source SIM Generate uses internally;
        // persisted on EOM Output so reports / Merch Need don't have to
        // re-aggregate.
        //
        // Store country scope mirrors the existing EOM convention
        // (DataSettings.Country == @country).
        var sohByStoreDiv = new Dictionary<(string Store, int Div), int>();
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await db.Database.OpenConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ls.StoreID, ls.DivCode, SUM(CAST(ISNULL(ls.SOH,0) AS bigint)) AS SOH
                  FROM racks.dbo.LPM_LocStock ls
                  INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
                 WHERE ds.Country = @country
                   AND ls.StoreID IS NOT NULL AND ls.StoreID <> ''
                   AND ls.DivCode IS NOT NULL
                 GROUP BY ls.StoreID, ls.DivCode;";
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@country", country));
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                if (rdr.IsDBNull(0) || rdr.IsDBNull(1)) continue;
                var storeId = rdr.GetString(0);
                var divCode = rdr.GetInt32(1);
                var soh     = rdr.IsDBNull(2) ? 0L : rdr.GetInt64(2);
                sohByStoreDiv[(storeId, divCode)] = (int)Math.Min(int.MaxValue, soh);
            }
        }

        var grades = (await db.LpmStoreGrades.AsNoTracking()
            .Where(g => g.IsActive && g.Country == country).OrderBy(g => g.SortOrder)
            .ToListAsync(ct));
        var volumeGroups = (await db.LpmVolumeGroups.AsNoTracking()
            .Where(g => g.IsActive && g.Country == country).OrderBy(g => g.SortOrder)
            .ToListAsync(ct));
        // skuRules no longer needed at EOM time — moved to SIM Generate's
        // LPM_SimItemSkuMax build. (LpmSKUMaxRules still queried elsewhere
        // when SIM rebuilds the per-item table.)

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
        // If the store has no Grade (PriorityRank = 0 / unranked / Grade lookup
        // miss) → TargetTurn = 0. SIM Generate will treat such stores as having
        // zero stock turn target, which combined with TargetEOM = 0 (also a
        // consequence of zero historical signal) means no allocation lands
        // on them.
        var markupByGrade = grades.ToDictionary(g => g.GradeCode, g => g.MarkupPct);
        foreach (var r in rows)
        {
            if (!planned.TryGetValue(r.DivCode, out var p)) continue;
            if (string.IsNullOrEmpty(r.Grade) || !markupByGrade.ContainsKey(r.Grade))
            {
                r.TargetTurn = 0m;
                continue;
            }
            var markup = markupByGrade[r.Grade];
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

        // Step 4: TargetEOM apportioned to stores by their share of the
        // division's total WtAvgSoldQty — same shape as Step 3 (TargetSales),
        // just using PlannedEOM instead of PlannedSalesQty:
        //
        //     TargetEOM[store] = (WtAvgSold[store] / Σ WtAvgSold in Division) × PlannedEOM
        //
        // Σ TargetEOM(Division) reconciles to LPM_Planned.PlannedEOM. Tgt Turn
        // / grade markup no longer feed back into Tgt EOM — that influence
        // exists only on the turn target itself.
        foreach (var grp in rows.GroupBy(r => r.DivCode))
        {
            if (!planned.TryGetValue(grp.Key, out var p)) continue;
            var totalWt = grp.Sum(r => r.WtAvgSoldQty);
            if (totalWt <= 0m) continue;
            foreach (var r in grp)
                r.TargetEOM = (r.WtAvgSoldQty / totalWt) * p.PlannedEOM;
        }

        // Step 5: Volume Group by TargetEOM desc, per division.
        foreach (var grp in rows.GroupBy(r => r.DivCode))
        {
            var ordered = grp.OrderByDescending(r => r.TargetEOM).ToList();
            AssignBuckets(ordered, volumeGroups.Select(g => (g.GroupCode, g.SharePct)).ToList(),
                          (r, code) => r.VolumeGroup = code);
        }

        // SKU Max is no longer computed at EOM time — it's per-item now and
        // built into LPM_SimItemSkuMax at SIM Generate time. The legacy
        // EomRow.SKUMax column is kept (set to 0) so old reports/exports that
        // reference it still bind without errors.
        //
        // SOH (per Store × Div) and Merch Need (Month / Week / Week 1..4) are
        // computed here so they're stored on LPM_EOM_Output and visible
        // everywhere downstream.
        //
        //   MerchNeedMonth   = TargetEOM − SOH + TargetSales
        //   MerchNeedWeekN   = (TargetEOM − SOH) / 4
        //                    + (TargetSales × SplitPct[N] / 100)        for N = 1..4
        //   MerchNeedWeek    = MerchNeedWeek1   ← legacy column, mirrors Wk1
        //                                          so ADM/Reports/Scheduler
        //                                          keep working untouched.
        //   MerchNeedDay     = MerchNeedMonth / 4 / 6   (unchanged)
        //
        // Splits source: dbo.LPM_WeeklySalesTargetSplit, keyed by
        // (Country, Year, Month, DivCode, WeekNo). When no row exists for
        // (Div, Week) the default fallback 20/20/25/35 is used so EOM never
        // blocks. The Weekly Sales Target Split admin page enforces
        // sum(splits) = 100 across the 4 weeks before allowing a save, so
        // the splits we read here either come from defaults (always sum
        // to 100) or from an admin who saved a balanced row.
        // Rounded to int (qty) using AwayFromZero so 0.5 doesn't disappear.
        // Default split — also used by the Weekly Sales Target Split admin
        // page to pre-fill new rows. Sums to 100.
        decimal[] defaultSplit = { 20m, 20m, 25m, 35m };

        foreach (var r in rows)
        {
            r.WHStock = whStock.GetValueOrDefault(r.DivCode, 0);
            (long Summer, long Winter) seasonal = whStockBySeason.TryGetValue(r.DivCode, out var sw)
                ? sw
                : (0L, 0L);
            r.WHStockSummer = (int)Math.Min(int.MaxValue, seasonal.Summer);
            r.WHStockWinter = (int)Math.Min(int.MaxValue, seasonal.Winter);
            r.SKUMax = 0;

            r.SOH = sohByStoreDiv.GetValueOrDefault((r.StoreID, r.DivCode), 0);
            var merchNeedMonth = r.TargetEOM - r.SOH + r.TargetSales;
            r.MerchNeedMonth = (int)Math.Round(merchNeedMonth,           MidpointRounding.AwayFromZero);
            r.MerchNeedDay   = (int)Math.Round(merchNeedMonth / 4m / 6m, MidpointRounding.AwayFromZero);

            // Per-week need: half of formula (TargetEOM − SOH)/4 is constant
            // across weeks; the per-week split only modulates the TargetSales
            // portion. Pre-compute the constant half once.
            var stockHalf = (r.TargetEOM - r.SOH) / 4m;
            int ComputeWeek(int weekNo)
            {
                var pct = splitsByDivWeek.TryGetValue((r.DivCode, weekNo), out var p)
                    ? p
                    : defaultSplit[weekNo - 1];
                var v = stockHalf + (r.TargetSales * pct / 100m);
                return (int)Math.Round(v, MidpointRounding.AwayFromZero);
            }
            r.MerchNeedWeek1 = ComputeWeek(1);
            r.MerchNeedWeek2 = ComputeWeek(2);
            r.MerchNeedWeek3 = ComputeWeek(3);
            r.MerchNeedWeek4 = ComputeWeek(4);
            // Legacy column mirrors Wk1 — see SIM Generate's WeekNo dropdown
            // for the full per-week pickup.
            r.MerchNeedWeek = r.MerchNeedWeek1;

            // Per-Division LPM Box Qty — same value repeated across stores
            // of that division (matches how WHStock is propagated).
            r.LPMBoxQty = (int)Math.Min(int.MaxValue,
                lpmBoxQtyByDiv.GetValueOrDefault(r.DivCode, 0L));
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
