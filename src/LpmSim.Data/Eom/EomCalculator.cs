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
        // 1.14.55 — Returns the SET of active DivCodes (not just a count) so the
        // plannedOk / whOk checks below can verify "every active division has a
        // matching row" instead of comparing raw counts. Raw counts would have
        // mismatched when LPM_Planned / LPM_WHStock still hold rows for a
        // retired division (e.g. 420) — the row counts wouldn't match the
        // shrunken active-division count.
        async Task<HashSet<int>> LoadActiveDivCodes()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var codes = await db.Divisions.AsNoTracking()
                .Where(d => d.IsActive).Select(d => d.DivCode).ToListAsync(ct);
            return new HashSet<int>(codes);
        }
        async Task<List<LpmPlanned>> LoadPlanned()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.LpmPlanneds.AsNoTracking()
                .Where(p => p.Country == country && p.Year1 == year && p.Month1 == month)
                .ToListAsync(ct);
        }
        // 1.14.55 — Returns the SET of DivCodes that have a WH stock row for the
        // period (was just a Count). Lets the wh check filter to active divisions
        // only without re-querying the active set.
        async Task<HashSet<int>> LoadWhStockDivCodes()
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var codes = await db.LpmWHStocks.AsNoTracking()
                .Where(w => w.Country == country && w.Year1 == year && w.Month1 == month)
                .Select(w => w.DivCode).ToListAsync(ct);
            return new HashSet<int>(codes);
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
        var weightsTask        = LoadWeights();
        var storeIdsTask       = LoadStoreIds();
        var activeDivCodesTask = LoadActiveDivCodes();
        var plannedTask        = LoadPlanned();
        var whStockDivsTask    = LoadWhStockDivCodes();
        var gradesTask         = LoadActiveGrades();
        var groupsTask         = LoadActiveGroups();
        var rulesTask          = LoadActiveRules();
        await Task.WhenAll(weightsTask, storeIdsTask, activeDivCodesTask, plannedTask,
                           whStockDivsTask, gradesTask, groupsTask, rulesTask);

        var weights         = await weightsTask;
        var storeIds        = await storeIdsTask;
        var activeDivCodes  = await activeDivCodesTask;
        var divCount        = activeDivCodes.Count;
        var planned         = await plannedTask;
        var whStockDivs     = await whStockDivsTask;
        var activeGrades    = await gradesTask;
        var activeGroups    = await groupsTask;
        var activeRules     = await rulesTask;

        // 1.14.55 — Filter Volume Groups and SKU Max Rules to active divisions
        // only. Without this, a leftover LPM_VolumeGroup row for an inactive
        // division (e.g. Country/420/E) with IsActive = true would still fail
        // the rules-coverage check below because the rule for that (Div,Group)
        // pair would (correctly) be missing. By dropping inactive-division
        // groups upfront, the check only verifies coverage for what actually
        // participates in the EOM run.
        activeGroups = activeGroups.Where(g => activeDivCodes.Contains(g.DivCode)).ToList();
        activeRules  = activeRules .Where(r => activeDivCodes.Contains(r.DivCode)).ToList();

        // Computed flags (no DB).
        var weightSum = weights.Sum(w => w.WeightPct);
        var weightsOk = weights.Count >= 1 && Math.Abs(weightSum - 1m) < 0.0001m;
        // 1.14.55 — Compare against ACTIVE divisions only. Rows in LPM_Planned /
        // LPM_WHStock for inactive divisions are ignored so the readiness flag
        // still flips green after a division is retired (e.g. DivCode 420).
        var plannedDivs = planned.Select(p => p.DivCode).ToHashSet();
        var plannedOk   = activeDivCodes.IsSubsetOf(plannedDivs);
        var whOk        = activeDivCodes.IsSubsetOf(whStockDivs);
        // Counts for the user-visible "X of Y divisions" message — both are
        // active-only so the text reads consistently.
        var plannedActiveCount = plannedDivs.Count(d => activeDivCodes.Contains(d));
        var whActiveCount      = whStockDivs.Count(d => activeDivCodes.Contains(d));
        var gradeSum  = activeGrades.Sum(g => g.SharePct);
        var gradesOk  = activeGrades.Count > 0 && Math.Abs(gradeSum - 1m) < 0.0001m;
        // 1.14.39 — Volume Groups are now per-(Country, DivCode, GroupCode).
        // The share total check must be PER-DIVISION (each division's
        // active groups must sum to 100%), not country-wide (which would
        // sum to N × 100% across N divisions).
        var groupsByDiv = activeGroups.GroupBy(g => g.DivCode).ToList();
        var groupSum = groupsByDiv.Count == 0
            ? 0m
            : groupsByDiv.Average(g => g.Sum(x => x.SharePct));
        var groupsOk = groupsByDiv.Count > 0
            && groupsByDiv.All(g => Math.Abs(g.Sum(x => x.SharePct) - 1m) < 0.0001m);
        // Rules check tightened: every (DivCode, GroupCode) in active
        // volume groups must have at least one matching SKU Max rule.
        // Previously checked just GroupCode, which let a missing rule
        // for ACCESSORIES/A slip through if e.g. BAGS/A had a rule.
        var rulesOk   = activeRules.Count > 0 &&
            activeGroups.All(g => activeRules.Any(r => r.DivCode == g.DivCode && r.GroupCode == g.GroupCode));

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
                // 1.14.55 — Counts active divisions only (retired ones are
                // excluded from both numerator and denominator).
                $"{plannedActiveCount} of {divCount} divisions configured."),
            SalesTurns = new(salesOk,
                "Sales & Turns",
                weights.Count == 0
                    ? "Weights must be defined first."
                    : $"{salesRows} rows present for the {weights.Count} weighted periods."),
            WHStock = new(whOk,
                "WH Stock",
                $"{whActiveCount} of {divCount} divisions have stock for this period."),
            Grades = new(gradesOk,
                "Store Grades",
                activeGrades.Count == 0
                    ? "No active grades configured."
                    : $"{activeGrades.Count} active grades, share total = {(gradeSum * 100m):0.##}%."),
            VolumeGroups = new(groupsOk,
                "Volume Groups",
                activeGroups.Count == 0
                    ? "No active volume groups configured."
                    : groupsOk
                        ? $"{activeGroups.Count} active groups across {groupsByDiv.Count} division(s), each summing to 100%."
                        : $"{activeGroups.Count} active groups across {groupsByDiv.Count} division(s) — each division's share total must equal 100% (avg = {(groupSum * 100m):0.##}%)."),
            SkuMaxRules = new(rulesOk,
                "SKU Max Rules",
                activeRules.Count == 0
                    ? $"No active rules for {country}."
                    : rulesOk
                        ? $"{activeRules.Count} active rules for {country} across {activeRules.Select(r => r.DivCode).Distinct().Count()} division(s)."
                        // 1.14.41 — Blocked state now lists the SPECIFIC
                        // (DivCode, GroupCode) pairs in active Volume Groups
                        // that have no matching active SKU Max rule. Was just
                        // a generic count, which left the planner guessing
                        // which (Division, GroupCode) to fix.
                        : $"{activeRules.Count} active rules but {activeGroups.Count(g => !activeRules.Any(r => r.DivCode == g.DivCode && r.GroupCode == g.GroupCode))} (Division, Group) pair(s) have no matching rule: " +
                          string.Join(", ", activeGroups
                              .Where(g => !activeRules.Any(r => r.DivCode == g.DivCode && r.GroupCode == g.GroupCode))
                              .Take(8)
                              .Select(g => $"Div{g.DivCode}/{g.GroupCode}")) +
                          (activeGroups.Count(g => !activeRules.Any(r => r.DivCode == g.DivCode && r.GroupCode == g.GroupCode)) > 8 ? ", …" : "")),
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
            // 1.14.55 — Active-only. Inactive divisions never produce new EOM
            // Output rows so we don't need names for them; saved batches that
            // already contain inactive-division rows fall back to the DivCode
            // string via GetSavedAsync's TryGetValue branch.
            return await db.Divisions.AsNoTracking()
                .Where(d => d.IsActive)
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
            IniEom         = e.IniEom         ?? 0m,
            PreStoreCapEom = e.PreStoreCapEom ?? 0m,
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
    /// Sources (1.14.7 — aligned with WH Stock Position and Variance Report
    /// so all three reports reconcile against the planner's SSMS queries):
    /// <list type="bullet">
    ///   <item><strong>HO Stock</strong> — <c>racks.dbo.LPM_LocStock</c> where
    ///         <c>dataname = 'HODATA'</c>, mapped to division via
    ///         <c>upc_subclass × subclassmaster × Division</c>; season from
    ///         <c>usa.dbo.upcbarcodes.Itemtype</c> (<c>'W'</c> → Winter, else
    ///         Summer).</item>
    ///   <item><strong>WH (Purchased)</strong> — <c>whboxitems.Qty</c> where
    ///         <c>ShopEligible &lt;&gt; 'E'</c> (boxes cleared / moved past
    ///         the 'E' in-process state). Strict <c>&lt;&gt;</c> excludes
    ///         NULL ShopEligible — matches the planner's reference SSMS
    ///         query <c>WHERE ShopEligible &lt;&gt; 'E'</c>.</item>
    ///   <item><strong>WH (Non-Purchased)</strong> — <c>whboxitems.Qty</c>
    ///         where <c>ShopEligible = 'E'</c> (still being processed).</item>
    ///   <item><strong>Eligible Stock</strong> — <c>whboxitems.Qty</c> where
    ///         <c>whboxitems.PalletCategory = 'ELIGIBLE' AND
    ///         ShopEligible &lt;&gt; 'E'</c> — purchased subset of the
    ///         ELIGIBLE pallet category.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 1.14.7: PalletCategory and Season both read from <c>whboxitems</c>
    /// directly (was via an <c>INNER JOIN</c> to <c>bfldata.dbo.pallettype</c>
    /// on <c>PalletType</c>, which silently dropped boxes whose
    /// <c>PalletType</c> had no master row). HO Stock continues to derive
    /// season from <c>upcbarcodes.Itemtype</c>.
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
                                              AND d.IsActive = 1  -- 1.14.55: skip retired divisions
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
                -- 1.14.7: rule unification with WH Stock Position + Variance
                -- Report. Season and PalletCategory both come from whboxitems
                -- directly (was via pt.* through an INNER JOIN to pallettype
                -- master, which silently dropped boxes whose PalletType had
                -- no master row). Purchased filter tightened from
                -- IS NULL OR <> 'E' to strict <> 'E' so it matches the
                -- planner's SSMS reference queries.
                --   • Purchased     = ShopEligible <> 'E' (boxes that have
                --                     been moved/cleared/shipped — 'E' marks
                --                     the still-in-process state).
                --   • Non-Purchased = ShopEligible = 'E' (still in-process).
                --   • Eligible      = PalletCategory='ELIGIBLE' AND
                --                     ShopEligible <> 'E' (eligible-category
                --                     subset of Purchased).
                --
                -- Note: this naming is the OPPOSITE of how the existing SIM
                -- allocator filters use ShopEligible <> E (which there means
                -- eligible-to-allocate). The Division Summary uses the
                -- business team labels; the allocator behaviour stays
                -- untouched.
                SELECT id.DivCode,
                       CASE WHEN UPPER(ISNULL(w.Season, '')) = 'W' THEN 'W' ELSE 'S' END AS Season,
                       SUM(CASE WHEN w.ShopEligible <> 'E'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS WHStockPurchased,
                       SUM(CASE WHEN w.ShopEligible = 'E'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS WHStockNonPurchased,
                       SUM(CASE WHEN UPPER(ISNULL(w.PalletCategory, '')) = 'ELIGIBLE'
                                 AND w.ShopEligible <> 'E'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS EligibleStock
                  FROM {whSrc} w
                  INNER JOIN ItemDiv id ON id.itemcode = w.ItemCode
                 GROUP BY id.DivCode,
                          CASE WHEN UPPER(ISNULL(w.Season, '')) = 'W' THEN 'W' ELSE 'S' END
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
                IniEom         = r.IniEom,
                PreStoreCapEom = r.PreStoreCapEom,
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

        // 1.14.55 — Active-only. The (Store × Division) grid only iterates
        // divisions that haven't been retired (e.g. 420). Inactive divisions
        // never produce new EOM Output rows.
        var divisions = await db.Divisions.AsNoTracking()
            .Where(d => d.IsActive)
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

        // 1.14.53 — Per-store EOM ceiling from LPM_StoreCapacity (Planning Config
        // → Stores Capacity EOM page, or the matching Excel upload). Only IsActive
        // rows participate. Missing entries mean "no cap" — the new Tgt EOM logic
        // then just passes Pre-Store-Cap EOM through unchanged.
        // Case-insensitive key (matches the SQL Server default collation).
        var storeCapByStore = (await db.LpmStoreCapacities.AsNoTracking()
            .Where(c => c.Country == country && c.IsActive)
            .ToListAsync(ct))
            .ToDictionary(c => c.StoreID, c => c.EomCapacity, StringComparer.OrdinalIgnoreCase);

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
                                              AND d.IsActive = 1  -- 1.14.55: skip retired divisions
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
                                              AND d.IsActive = 1  -- 1.14.55: skip retired divisions
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
        // 1.14.39 — Volume Groups are now per-(Country, DivCode, GroupCode).
        // Group by DivCode so Step 5 can pick the right bucket profile for
        // each division's stores. Divisions with no rows in the map don't
        // get a Volume Group assigned (Step 5 skips them).
        var volumeGroupsByDiv = (await db.LpmVolumeGroups.AsNoTracking()
            .Where(g => g.IsActive && g.Country == country).OrderBy(g => g.SortOrder)
            .ToListAsync(ct))
            .GroupBy(g => g.DivCode)
            .ToDictionary(grp => grp.Key, grp => grp.ToList());
        // skuRules no longer needed at EOM time — moved to SIM Generate's
        // LPM_SimItemSkuMax build. (LpmSKUMaxRules still queried elsewhere
        // when SIM rebuilds the per-item table.)

        // 1.14.42 — TRUE weighted average per (Store, Division).
        // Formula:  WtAvg = Σ (Qty × WeightPct) / Σ WeightPct
        //
        // Previously we divided by periodCount (count of weights with
        // WeightPct > 0). That gave a "per-period average weighted
        // contribution" — which read as roughly monthly_qty / N for N
        // active periods. Two periods at 40/60 with the same monthly
        // qty showed HALF the actual monthly qty, which was unintuitive.
        //
        // The new divisor is Σ WeightPct. When weights sum to 1.0
        // (enforced by the Monthly Weights readiness check), the divisor
        // is 1.0 and the result reads as "monthly-equivalent sold qty
        // weighted by recency" — the standard weighted-average reading.
        //
        // Downstream Step 3 (TargetSales) and Step 4 (TargetEOM) are
        // share-based — (r.WtAvgSoldQty / totalWt) × Planned — so scaling
        // every WtAvg by the same constant leaves their results identical.
        // Ranks (SoldQtyRank, TurnsRank) are order-based — also unchanged.
        var weightSum = weights.Sum(w => w.WeightPct);

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
            if (weightSum > 0m)
            {
                wtSold /= weightSum;
                wtTurn /= weightSum;
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

        // Step 4 — 1.14.53 rewrite. The single old apportionment is replaced
        // with three sequential stages so the planner can read the calc as a
        // waterfall on the EOM grid:
        //
        //   4a. Ini.EOM[store, div]        = TargetSales × TargetTurn
        //       (a "naive demand" figure: how much stock the store would
        //        carry if you held its sales × its target turn target).
        //
        //   4b. PreStoreCapEom[store, div] = (Ini.EOM[store, div] / Σ Ini.EOM in Div)
        //                                    × PlannedEOM[div]
        //       (apportions the division's PlannedEOM by each store's Ini.EOM
        //        share within the division. Σ within a division reconciles
        //        to PlannedEOM, same shape Step 3 uses for TargetSales.
        //        Cap-agnostic — store cap is NOT considered here.)
        //
        //   4c. TargetEOM[store, div]      = if LPM_StoreCapacity.EomCapacity
        //                                      EXISTS for the store AND
        //                                      Σ PreStoreCapEom across divisions
        //                                      > EomCapacity:
        //                                        PreStoreCapEom[div]
        //                                        × (EomCapacity / Σ PreStoreCapEom)
        //                                    else:
        //                                        PreStoreCapEom[div]   (passthrough)
        //
        // Σ TargetEOM(Division) only reconciles to PlannedEOM(Division) when
        // no store's cap binds in that division. When a cap binds, the division
        // falls short by the capped delta — that's intentional (the cap is a
        // ceiling, accepting less stock is the whole point).
        //
        // Stage 4a — Ini.EOM per row.
        foreach (var r in rows)
            r.IniEom = r.TargetSales * r.TargetTurn;

        // Stage 4b — PreStoreCapEom per division by Ini.EOM share.
        // Divisions with Σ IniEom <= 0 (e.g. nothing planned, or every store
        // unranked so TgtTurn = 0 everywhere) leave PreStoreCapEom = 0 — the
        // division has no apportionment basis.
        foreach (var grp in rows.GroupBy(r => r.DivCode))
        {
            if (!planned.TryGetValue(grp.Key, out var p)) continue;
            var totalIni = grp.Sum(r => r.IniEom);
            if (totalIni <= 0m) continue;
            foreach (var r in grp)
                r.PreStoreCapEom = (r.IniEom / totalIni) * p.PlannedEOM;
        }

        // Stage 4c — TargetEOM per store, applying store cap if available.
        // Group by store (across divisions) so the cap is evaluated against
        // the store's total PreStoreCapEom demand.
        foreach (var grp in rows.GroupBy(r => r.StoreID))
        {
            var totalPre = grp.Sum(r => r.PreStoreCapEom);
            // "Cap available" = a row exists in LPM_StoreCapacity (IsActive)
            // for this store. EomCapacity = 0 is a valid explicit cap meaning
            // "this store gets nothing" — that's why the dictionary lookup is
            // the decisive check, not the value.
            if (storeCapByStore.TryGetValue(grp.Key, out var cap))
            {
                if (totalPre > cap && totalPre > 0m)
                {
                    var ratio = (decimal)cap / totalPre;
                    foreach (var r in grp)
                        r.TargetEOM = r.PreStoreCapEom * ratio;
                }
                else
                {
                    // Cap not binding (totalPre <= cap, or totalPre == 0):
                    // pass through. Cap = 0 with totalPre = 0 also lands here
                    // with TargetEOM = 0 naturally.
                    foreach (var r in grp)
                        r.TargetEOM = r.PreStoreCapEom;
                }
            }
            else
            {
                // No cap configured for this store — passthrough.
                foreach (var r in grp)
                    r.TargetEOM = r.PreStoreCapEom;
            }
        }

        // Step 5: Volume Group by TargetEOM desc, per division.
        // 1.14.39 — Each division now uses its OWN bucket profile from
        // volumeGroupsByDiv. Divisions without configured groups skip
        // bucketing — their rows keep the default VolumeGroup = "" and
        // downstream code treats them as unranked.
        foreach (var grp in rows.GroupBy(r => r.DivCode))
        {
            if (!volumeGroupsByDiv.TryGetValue(grp.Key, out var vgList) || vgList.Count == 0)
                continue;
            var ordered = grp.OrderByDescending(r => r.TargetEOM).ToList();
            AssignBuckets(ordered, vgList.Select(g => (g.GroupCode, g.SharePct)).ToList(),
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
