using LpmSim.Core;
using LpmSim.Core.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data.LpmSim;

/// <summary>
/// Two-phase LPM SIM allocator (per the LPM SIM Generate spec):
///   Phase 1  — LPM Boxes (LPMDt current/elapsed, shopeligible &lt;&gt; 'E')
///       1a normal      : priority asc, Wt-Avg-Sold desc; capped by SKUMax−SOH
///                        and TargetEOM−DivSOH (cumulative across boxes).
///       1b round-robin : 1 unit per store per pass (priority order).
///                        ELIGIBILITY: only runs when post-1a box usability%
///                        ≥ OverrideUsabilityPct ("Round Robin Box Usability %").
///                        CAPS: honours BOTH SKUMax and EOM Div balance,
///                        always cumulative against prior normal + RR allocs.
///   Phase 2  — Non-LPM Boxes (LPMDt NULL). Sorted by total Qty desc, then by
///              item-overlap with Phase-1 allocations desc.
///       2a normal      : same caps as Phase 1.
///       2b round-robin : same as 1b — usability gate + both caps.
///
///   EOM Balance (TargetEOM − DivSOH) and SKU Max (SKUMax − SOH) are HARD
///   ceilings on every step. No allocation crosses either — leftover qty in
///   a box stays unallocated when every candidate is capped.
///
/// Working tables populated by every run:
///   LPMSIM_Output             — final allocation lines, tagged Phase + IsRoundRobin
///   LPMSIM_AllocTrace         — every (Box × Item × Store) decision considered
///   LPMSIM_StoreItemBalance   — per-(Store,Item) totals snapshot at run-end
///   LPMSIM_StoreDivBalance    — per-(Store,Div)  totals snapshot at run-end
/// </summary>
public class LpmSimGenerator(IDbContextFactory<LpmDbContext> dbFactory, ICurrentUser currentUser)
{
    public async Task<LpmSimReadiness> CheckAsync(
        string country, int year, int month, DateTime runDate,
        LpmSimSourceFlags sources = LpmSimSourceFlags.LpmBoxes,
        LpmSimSeasonFlags seasons = LpmSimSeasonFlags.Summer,
        IReadOnlyList<string>? warehouses = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var eomAgg = await db.LpmEomOutputs
            .Where(e => e.Country == country && e.Year1 == year && e.Month1 == month)
            .GroupBy(_ => 1)
            .Select(g => new { Cnt = g.Count(), Total = g.Sum(e => (decimal?)e.TargetEOM) ?? 0m })
            .FirstOrDefaultAsync(ct);
        var eomCount = eomAgg?.Cnt ?? 0;
        var totalEom = (long)Math.Round(eomAgg?.Total ?? 0m);
        var eomReady = eomCount > 0;

        // Per-segment box counts: LPM/Non-LPM × Summer/Winter. ShopEligible <> 'E'
        // applies to BOTH (excludes already-purchased boxes).
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        BoxSegmentCounts? segments = null;
        try
        {
            using var cmd = conn.CreateCommand();
            // Build a parameterised IN-list for warehouses (empty = no filter, all warehouses).
            var (whClause, whParams) = BuildWarehouseClause(warehouses);
            cmd.CommandText = $@"
                SELECT
                    -- LPM boxes (current/elapsed)
                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND (YEAR(w.LPMDt) < @y OR (YEAR(w.LPMDt) = @y AND MONTH(w.LPMDt) <= @m))
                              AND ISNULL(pt.Season, '') <> 'W' THEN 1 ELSE 0 END) AS LpmSummerLines,
                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND (YEAR(w.LPMDt) < @y OR (YEAR(w.LPMDt) = @y AND MONTH(w.LPMDt) <= @m))
                              AND ISNULL(pt.Season, '') <> 'W' THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LpmSummerQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NOT NULL AND (YEAR(w.LPMDt) < @y OR (YEAR(w.LPMDt) = @y AND MONTH(w.LPMDt) <= @m))
                              AND ISNULL(pt.Season, '') <> 'W' THEN w.BoxNo END) AS LpmSummerBoxes,

                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') <> 'W' THEN 1 ELSE 0 END) AS NonLpmSummerLines,
                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') <> 'W' THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS NonLpmSummerQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') <> 'W' THEN w.BoxNo END) AS NonLpmSummerBoxes,

                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND (YEAR(w.LPMDt) < @y OR (YEAR(w.LPMDt) = @y AND MONTH(w.LPMDt) <= @m))
                              AND ISNULL(pt.Season, '') = 'W' THEN 1 ELSE 0 END) AS LpmWinterLines,
                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND (YEAR(w.LPMDt) < @y OR (YEAR(w.LPMDt) = @y AND MONTH(w.LPMDt) <= @m))
                              AND ISNULL(pt.Season, '') = 'W' THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LpmWinterQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NOT NULL AND (YEAR(w.LPMDt) < @y OR (YEAR(w.LPMDt) = @y AND MONTH(w.LPMDt) <= @m))
                              AND ISNULL(pt.Season, '') = 'W' THEN w.BoxNo END) AS LpmWinterBoxes,

                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') = 'W' THEN 1 ELSE 0 END) AS NonLpmWinterLines,
                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') = 'W' THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS NonLpmWinterQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') = 'W' THEN w.BoxNo END) AS NonLpmWinterBoxes
                  FROM racks.dbo.whboxitems w
                  INNER JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
                 WHERE pt.PalletCategory = 'ELIGIBLE'
                   AND ISNULL(w.ShopEligible, '') <> 'E'
                   {whClause};";
            cmd.Parameters.Add(new SqlParameter("@y", year));
            cmd.Parameters.Add(new SqlParameter("@m", month));
            foreach (var p in whParams) cmd.Parameters.Add(p);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                int  lsB = rdr.IsDBNull(2)  ? 0 : rdr.GetInt32(2);
                long lsQ = rdr.IsDBNull(1)  ? 0 : rdr.GetInt64(1);
                int  nsB = rdr.IsDBNull(5)  ? 0 : rdr.GetInt32(5);
                long nsQ = rdr.IsDBNull(4)  ? 0 : rdr.GetInt64(4);
                int  lwB = rdr.IsDBNull(8)  ? 0 : rdr.GetInt32(8);
                long lwQ = rdr.IsDBNull(7)  ? 0 : rdr.GetInt64(7);
                int  nwB = rdr.IsDBNull(11) ? 0 : rdr.GetInt32(11);
                long nwQ = rdr.IsDBNull(10) ? 0 : rdr.GetInt64(10);
                segments = new BoxSegmentCounts(
                    LpmSummerBoxes: lsB,    LpmSummerQty: lsQ,
                    NonLpmSummerBoxes: nsB, NonLpmSummerQty: nsQ,
                    LpmWinterBoxes: lwB,    LpmWinterQty: lwQ,
                    NonLpmWinterBoxes: nwB, NonLpmWinterQty: nwQ);
            }
        }
        catch { /* leave null */ }

        // Sum the requested-segment counts for the readiness check / metric.
        int  selBoxes = 0;
        long selQty   = 0;
        if (segments is not null)
        {
            if (sources.HasFlag(LpmSimSourceFlags.LpmBoxes))
            {
                if (seasons.HasFlag(LpmSimSeasonFlags.Summer)) { selBoxes += segments.LpmSummerBoxes; selQty += segments.LpmSummerQty; }
                if (seasons.HasFlag(LpmSimSeasonFlags.Winter)) { selBoxes += segments.LpmWinterBoxes; selQty += segments.LpmWinterQty; }
            }
            if (sources.HasFlag(LpmSimSourceFlags.NonLpmBoxes))
            {
                if (seasons.HasFlag(LpmSimSeasonFlags.Summer)) { selBoxes += segments.NonLpmSummerBoxes; selQty += segments.NonLpmSummerQty; }
                if (seasons.HasFlag(LpmSimSeasonFlags.Winter)) { selBoxes += segments.NonLpmWinterBoxes; selQty += segments.NonLpmWinterQty; }
            }
        }

        // LocStock count + total SOH for the country. Uses the denormalized
        // Country column on LPM_LocStock — no DataSettings join, single index seek.
        int locRows = 0;
        long totalSoh = 0;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*) AS Rows, SUM(CAST(ISNULL(ls.SOH,0) AS bigint)) AS TotalSoh
                  FROM racks.dbo.LPM_LocStock ls
                  INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
                 WHERE ds.SIMCountry = @country
                   AND ls.StoreID IS NOT NULL AND ls.StoreID <> '';";
            cmd.Parameters.Add(new SqlParameter("@country", country));
            cmd.CommandTimeout = 120;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                locRows  = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                totalSoh = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1);
            }
        }
        catch { /* leave zero */ }

        var existing = await db.LpmSimBatches.AsNoTracking()
            .Where(b => b.Country == country && b.RunDate == runDate.Date)
            .OrderByDescending(b => b.LPMBatchNo)
            .FirstOrDefaultAsync(ct);

        var srcLabel  = SourceLabel(sources);
        var seasLabel = SeasonLabel(seasons);
        var balanceToFill = totalEom - totalSoh;
        return new LpmSimReadiness(
            EomReady: eomReady,
            EomDetail: eomReady
                ? $"{eomCount:N0} rows · Total EOM {totalEom:N0} · Balance to Fill {balanceToFill:N0}"
                : $"No LPM_EOM_Output rows for {country} {year}/{month:D2}. Generate EOM first.",
            BoxesReady: selBoxes > 0,
            BoxesDetail: selBoxes > 0
                ? $"{selBoxes:N0} boxes · {selQty:N0} qty · {srcLabel} · {seasLabel}"
                : $"No eligible boxes for the current selection ({srcLabel} · {seasLabel}).",
            LocStockReady: locRows > 0,
            LocStockDetail: locRows > 0
                ? $"Total SOH {totalSoh:N0} · {locRows:N0} rows for {country}"
                : "LPM_LocStock is empty for this country — every store will treat SOH as 0.",
            ExistingDraft:    existing is { Status: "Draft"    },
            ExistingApproved: existing is { Status: "Approved" },
            CurrentBatchNo:    existing?.LPMBatchNo,
            CurrentStatus:     existing?.Status,
            CurrentApprovedTS: existing?.ApprovedTS,
            CurrentApprovedBy: existing?.ApprovedBy)
        {
            EomRows            = eomCount,
            TotalEom           = totalEom,
            TotalBalanceToFill = balanceToFill,
            EligibleBoxes      = selBoxes,
            EligibleLines      = 0,
            EligibleQty        = selQty,
            LocStockRows       = locRows,
            TotalSoh           = totalSoh,
            BoxSegments        = segments,
            CurrentBatchSources              = existing?.Sources,
            CurrentBatchSeasons              = existing?.Seasons,
            CurrentBatchOverrideUsabilityPct = existing?.OverrideUsabilityPct,
            CurrentBatchWarehouses           = existing?.Warehouses,
            CurrentBatchFillStrategy         = existing?.FillStrategy,
        };
    }

    private static string SourceLabel(LpmSimSourceFlags f)
    {
        if (f == LpmSimSourceFlags.None) return "no sources";
        var parts = new List<string>(2);
        if (f.HasFlag(LpmSimSourceFlags.LpmBoxes))    parts.Add("LPM");
        if (f.HasFlag(LpmSimSourceFlags.NonLpmBoxes)) parts.Add("Non-LPM");
        return string.Join(" + ", parts);
    }

    private static string SeasonLabel(LpmSimSeasonFlags f)
    {
        if (f == LpmSimSeasonFlags.None || f == LpmSimSeasonFlags.Both) return "All seasons";
        var parts = new List<string>(2);
        if (f.HasFlag(LpmSimSeasonFlags.Summer)) parts.Add("Summer");
        if (f.HasFlag(LpmSimSeasonFlags.Winter)) parts.Add("Winter");
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Builds a SQL fragment + parameters for "AND w.Warehouse IN (@wh0, @wh1, ...)".
    /// Returns ("", []) when warehouses is null/empty (= no filter, all warehouses).
    /// </summary>
    private static (string clause, List<SqlParameter> parameters) BuildWarehouseClause(IReadOnlyList<string>? warehouses)
    {
        if (warehouses is null || warehouses.Count == 0)
            return ("", new List<SqlParameter>());

        var distinct = warehouses
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return ("", new List<SqlParameter>());

        var paramNames = new List<string>(distinct.Count);
        var parms      = new List<SqlParameter>(distinct.Count);
        for (int i = 0; i < distinct.Count; i++)
        {
            var name = $"@wh{i}";
            paramNames.Add(name);
            parms.Add(new SqlParameter(name, distinct[i]));
        }
        return ($"AND w.Warehouse IN ({string.Join(", ", paramNames)})", parms);
    }

    /// <summary>Comma-separated label for the snapshot column on LPMSIM_Batch.</summary>
    private static string? WarehousesLabel(IReadOnlyList<string>? warehouses)
    {
        if (warehouses is null || warehouses.Count == 0) return null;
        var distinct = warehouses
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count == 0 ? null : string.Join(",", distinct);
    }

    /// <summary>
    /// Runs the two-phase allocation. If a Draft batch already exists for
    /// (Country, RunDate) it's replaced. If an Approved batch exists, the call
    /// fails — caller must Delete it first.
    /// </summary>
    public async Task<LpmSimGenerateResult> GenerateAsync(LpmSimGenerateRequest req, CancellationToken ct = default)
    {
        if (req.Sources == LpmSimSourceFlags.None)
            throw new InvalidOperationException("Pick at least one Box Source (LPM and/or Non-LPM).");
        if (req.Seasons == LpmSimSeasonFlags.None)
            throw new InvalidOperationException("Pick at least one Season (Summer and/or Winter).");

        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        long msReadBoxes = 0, msReadItemDiv = 0, msReadSoh = 0, msReadEom = 0;
        long msAllocate = 0, msPersistOutput = 0, msPersistTrace = 0, msPersistBalances = 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // AsNoTracking — we never modify this entity; we delete the row directly
        // via raw SQL below. Without AsNoTracking, EF's change tracker holds a
        // phantom reference to the now-deleted row and the next SaveChangesAsync
        // can throw DbUpdateException with confusing "an entity with the same
        // key" errors.
        var existing = await db.LpmSimBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Country == req.Country && b.RunDate == req.RunDate.Date, ct);
        if (existing is { Status: "Approved" })
            throw new InvalidOperationException(
                $"An Approved batch (#{existing.LPMBatchNo}) already exists for {req.Country} on {req.RunDate:yyyy-MM-dd}. Delete it first to rerun.");

        // Replace any existing Draft for this (Country, RunDate). Use chunked
        // deletes (same reasoning as DeleteAsync) so the FK CASCADE on huge
        // child tables doesn't timeout.
        if (existing is not null)
        {
            db.Database.SetCommandTimeout(600);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_AllocTrace",       existing.LPMBatchNo, ct);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_Output",           existing.LPMBatchNo, ct);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_StoreItemBalance", existing.LPMBatchNo, ct);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_StoreDivBalance",  existing.LPMBatchNo, ct);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_BoxBalance",       existing.LPMBatchNo, ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"DELETE FROM dbo.LPMSIM_Batch WHERE LPMBatchNo = {existing.LPMBatchNo};", ct);
            // Defensive: clear any tracker state that snuck in during the deletes,
            // so the next SaveChangesAsync only sees our new batch entity.
            db.ChangeTracker.Clear();
        }

        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        // ---------------- Inputs ----------------
        var seasonClause = req.Seasons switch
        {
            LpmSimSeasonFlags.Summer                          => "AND ISNULL(pt.Season, '') <> 'W'",
            LpmSimSeasonFlags.Winter                          => "AND ISNULL(pt.Season, '') = 'W'",
            LpmSimSeasonFlags.Both                            => "",
            _                                                 => "",
        };

        // 1) Eligible Box × Item lines, tagged with IsLpm.
        var swStep = System.Diagnostics.Stopwatch.StartNew();
        var lpmBoxes    = new List<BoxItem>(8192);
        var nonLpmBoxes = new List<BoxItem>(8192);

        if (req.Sources.HasFlag(LpmSimSourceFlags.LpmBoxes))
        {
            await ReadBoxesAsync(conn, true, req.RunYear, req.RunMonth, seasonClause, req.Warehouses, lpmBoxes, ct);
        }
        if (req.Sources.HasFlag(LpmSimSourceFlags.NonLpmBoxes))
        {
            await ReadBoxesAsync(conn, false, req.RunYear, req.RunMonth, seasonClause, req.Warehouses, nonLpmBoxes, ct);
        }
        msReadBoxes = swStep.ElapsedMilliseconds; swStep.Restart();

        // 2 + 3) Read SOH and Item→Div in ONE pass from LPM_LocStock.
        //
        // Critical: itemDiv MUST come from the same source as the report uses,
        // otherwise the engine and the report disagree on which division an
        // item belongs to → SIM Qty appears to exceed EOM Balance in the report
        // (because the report and engine bucket SOH differently per division).
        //
        // The user's daily ETL populates LPM_LocStock.DivCode as the
        // denormalized lookup. Reports query LocStock.DivCode directly. Engine
        // now does the same so both agree row-for-row.
        //
        // For items that exist in BOXES but aren't yet stocked anywhere in the
        // country (= not in LocStock), we fall back to upc_subclass below.
        var sohMap  = new Dictionary<(string Store, string Item), int>(StoreItemComparer.Instance);
        var itemDiv = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            // Country resolved via DataSettings JOIN. LPM_LocStock.Country is
            // occasionally populated as '' (empty string) by the daily ETL for
            // some stores; relying on it directly excludes those stores. Using
            // DataSettings.Country (the authoritative source) is bulletproof.
            cmd.CommandText = @"
                SELECT ls.StoreID, ls.Itemcode, ls.DivCode, ISNULL(ls.SOH, 0) AS SOH
                  FROM racks.dbo.LPM_LocStock ls
                  INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
                 WHERE ds.SIMCountry = @country
                   AND ls.StoreID  IS NOT NULL AND ls.StoreID  <> ''
                   AND ls.Itemcode IS NOT NULL AND ls.Itemcode <> '';";
            cmd.Parameters.Add(new SqlParameter("@country", req.Country));
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var s = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                var i = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(i)) continue;
                var d   = rdr.IsDBNull(2) ? (int?)null : rdr.GetInt32(2);
                var soh = rdr.IsDBNull(3) ? 0          : rdr.GetInt32(3);
                sohMap.TryGetValue((s, i), out var prior);
                sohMap[(s, i)] = prior + soh;
                if (d.HasValue && !itemDiv.ContainsKey(i)) itemDiv[i] = d.Value;
            }
        }
        msReadSoh = swStep.ElapsedMilliseconds; swStep.Restart();

        // Fallback: items present in boxes but NOT yet in LocStock for this
        // country (brand-new items being received but not yet stocked anywhere).
        // For those — and only those — look up DivCode via upc_subclass.
        // Items present in LocStock keep their LocStock.DivCode so the engine
        // and the report agree on the per-(Store, Div) SOH bucketing.
        var missingDivItems = lpmBoxes.Select(b => b.ItemCode)
            .Concat(nonLpmBoxes.Select(b => b.ItemCode))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(ic => !itemDiv.ContainsKey(ic))
            .ToList();
        if (missingDivItems.Count > 0)
        {
            using (var ddl = conn.CreateCommand())
            {
                ddl.CommandText = @"
                    IF OBJECT_ID('tempdb..#sim_box_items') IS NOT NULL DROP TABLE #sim_box_items;
                    CREATE TABLE #sim_box_items (Itemcode varchar(30) NOT NULL);";
                await ddl.ExecuteNonQueryAsync(ct);
            }
            using (var dt = new System.Data.DataTable())
            {
                dt.Columns.Add("Itemcode", typeof(string));
                foreach (var ic in missingDivItems) dt.Rows.Add(ic);
                using var bulk = new SqlBulkCopy((SqlConnection)conn)
                {
                    DestinationTableName = "#sim_box_items",
                    BatchSize = 5000,
                    BulkCopyTimeout = 60,
                };
                bulk.ColumnMappings.Add("Itemcode", "Itemcode");
                await bulk.WriteToServerAsync(dt, ct);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT t.Itemcode, MIN(d.DivCode) AS DivCode
                      FROM #sim_box_items t
                      INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = t.Itemcode
                      INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID  = u.MH4ID
                      INNER JOIN LPMSIM.dbo.Division       d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                     GROUP BY t.Itemcode;";
                cmd.CommandTimeout = 120;
                using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    if (rdr.IsDBNull(0)) continue;
                    itemDiv[rdr.GetString(0)] = rdr.GetInt32(1);
                }
            }
        }
        msReadItemDiv = swStep.ElapsedMilliseconds; swStep.Restart();

        // 4) EOM rows for this Country/Year/Month, indexed by DivCode.
        //    Order within a div: Priority Rank asc, Wt-Avg-Sold-Qty desc.
        //
        // DEFENSIVE COUNTRY GUARD — INNER JOIN DataSettings so a store can only
        // become an allocation candidate when its DataSettings.Country matches
        // the run's Country. Even if LPM_EOM_Output has stale rows tagged
        // Country='UAE' for a store DataSettings now lists under a different
        // country, this join drops it — no allocation can leak to a non-UAE
        // store under any circumstance.
        var eomByDiv = new Dictionary<int, List<EomStore>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT eo.StoreID, eo.DivCode, ISNULL(eo.SKUMax, 0) AS SKUMax,
                       ISNULL(eo.TargetEOM, 0) AS TargetEOM,
                       ISNULL(eo.PriorityRank, 0) AS PriorityRank,
                       ISNULL(eo.WtAvgSoldQty, 0) AS WtAvgSoldQty,
                       ISNULL(eo.VolumeGroup, '') AS VolumeGroup
                  FROM dbo.LPM_EOM_Output eo
                  INNER JOIN dbo.DataSettings ds
                          ON ds.StoreID = eo.StoreID
                         AND ds.SIMCountry = @country
                 WHERE eo.Country = @country
                   AND eo.Year1   = @y
                   AND eo.Month1  = @m;";
            cmd.Parameters.Add(new SqlParameter("@country", req.Country));
            cmd.Parameters.Add(new SqlParameter("@y", req.RunYear));
            cmd.Parameters.Add(new SqlParameter("@m", req.RunMonth));
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var s = new EomStore(
                    StoreID:      rdr.GetString(0),
                    DivCode:      rdr.GetInt32(1),
                    SKUMax:       rdr.GetInt32(2),
                    TargetEOM:    rdr.GetDecimal(3),
                    PriorityRank: rdr.GetDecimal(4),
                    WtAvgSold:    rdr.GetDecimal(5),
                    VolumeGroup:  rdr.GetString(6));
                if (!eomByDiv.TryGetValue(s.DivCode, out var list))
                    eomByDiv[s.DivCode] = list = new();
                list.Add(s);
            }
        }
        foreach (var (div, list) in eomByDiv.ToList())
        {
            // Spec ordering: Priority Rank ASC, then Wt-Avg-Sold-Qty DESC.
            eomByDiv[div] = list
                .OrderBy(s => s.PriorityRank)
                .ThenByDescending(s => s.WtAvgSold)
                .ToList();
        }

        msReadEom = swStep.ElapsedMilliseconds; swStep.Restart();

        // ---- Per-(Store, Div) pre-existing SOH for the Div cap. Case-insensitive
        //      on StoreID for the same reason as sohMap.
        var sohByStoreDiv = new Dictionary<(string Store, int Div), int>(StoreDivComparer.Instance);
        foreach (var ((store, item), soh) in sohMap)
        {
            if (!itemDiv.TryGetValue(item, out var dc)) continue;
            var k = (store, dc);
            sohByStoreDiv.TryGetValue(k, out var cur);
            sohByStoreDiv[k] = cur + soh;
        }

        // ---------------- Allocation state ----------------
        var batch = new LpmSimBatch
        {
            Country  = req.Country,
            RunYear  = req.RunYear,
            RunMonth = req.RunMonth,
            RunDate  = req.RunDate.Date,
            Status   = "Draft",
            CreateTS = DateTime.Now,
            CreatedBy = req.User,
            // Snapshot the filters that produced this batch — so the Result preview
            // never shows ambiguous results when the user later changes the checkboxes.
            Sources              = SourceLabel(req.Sources),
            Seasons              = SeasonLabel(req.Seasons),
            OverrideUsabilityPct = req.OverrideUsabilityPct,
            Warehouses           = WarehousesLabel(req.Warehouses),
            FillStrategy         = req.FillStrategy.ToString(),
        };
        db.LpmSimBatches.Add(batch);
        await db.SaveChangesAsync(ct);   // get LPMBatchNo

        // Running totals — both tracked together. SkuMax / TargetEOM caps
        // gate normal allocations; round-robin allocations are not gated but
        // ARE counted toward the running totals (so subsequent normal alloc
        // sees the higher base and won't pile on more, per spec).
        // ALL dictionaries use case-insensitive comparers on StoreID/ItemCode
        // so they match SQL Server's default case-insensitive collation.
        var allocStoreItem    = new Dictionary<(string Store, string Item), int>(StoreItemComparer.Instance);
        var allocStoreDiv     = new Dictionary<(string Store, int Div),    int>(StoreDivComparer.Instance);
        var p1NormalSI = new Dictionary<(string Store, string Item), int>(StoreItemComparer.Instance);
        var p1RrSI     = new Dictionary<(string Store, string Item), int>(StoreItemComparer.Instance);
        var p2NormalSI = new Dictionary<(string Store, string Item), int>(StoreItemComparer.Instance);
        var p2RrSI     = new Dictionary<(string Store, string Item), int>(StoreItemComparer.Instance);
        var p1NormalSD = new Dictionary<(string Store, int Div),    int>(StoreDivComparer.Instance);
        var p1RrSD     = new Dictionary<(string Store, int Div),    int>(StoreDivComparer.Instance);
        var p2NormalSD = new Dictionary<(string Store, int Div),    int>(StoreDivComparer.Instance);
        var p2RrSD     = new Dictionary<(string Store, int Div),    int>(StoreDivComparer.Instance);

        var output = new List<LpmSimOutput>(lpmBoxes.Count + nonLpmBoxes.Count);
        var trace  = new List<LpmSimAllocTrace>((lpmBoxes.Count + nonLpmBoxes.Count) * 4);
        long totalQty = 0;
        int boxesSkipped = 0;
        int itemsWithoutDiv = 0;
        int p1NormalLines = 0, p1RrLines = 0, p2NormalLines = 0, p2RrLines = 0;
        long p1NormalQty = 0, p1RrQty = 0, p2NormalQty = 0, p2RrQty = 0;

        var distinctBoxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // O(1) running tally of qty allocated per (Box, Phase). Box names are
        // also case-insensitive for the same reason.
        var boxAllocByPhase = new Dictionary<(string Box, string Phase), long>(BoxPhaseComparer.Instance);
        // Items already placed in Phase 1 — used to prefer overlapping Non-LPM boxes.
        var phase1Items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ============================================================
        // PHASE 1 — LPM Boxes  (2 sub-steps per box)
        //   1a normal              — SKU + EOM caps both apply
        //   1b RR within EOM cap   — overrides SKU cap, but stops adding to a
        //                            (Store, Div) once DivBalance hits 0
        // EOM Balance is hard. Leftover qty after 1b stays unallocated —
        // the box's remaining units do NOT cross the EOM ceiling under any
        // circumstance.
        // ============================================================
        var p1Groups = lpmBoxes.GroupBy(b => b.BoxNo).ToList();
        foreach (var grp in p1Groups)
        {
            distinctBoxes.Add(grp.Key);
            var grpLines  = grp.ToList();
            var lineRemain = grpLines.Select(l => l.Qty).ToArray();
            var boxQty     = grpLines.Sum(l => (long)l.Qty);

            // 1a normal allocation
            for (int idx = 0; idx < grpLines.Count; idx++)
            {
                var line = grpLines[idx];
                lineRemain[idx] = AllocateLineNormal(
                    isPhase1: true, line, batch, req,
                    eomByDiv, itemDiv, sohMap, sohByStoreDiv,
                    allocStoreItem, allocStoreDiv,
                    p1NormalSI, p1NormalSD,
                    boxAllocByPhase,
                    output, trace,
                    ref p1NormalLines, ref p1NormalQty,
                    ref itemsWithoutDiv, ref boxesSkipped);
                totalQty += line.Qty - lineRemain[idx];
                phase1Items.Add(line.ItemCode);
            }

            // 1b RR — only fires for boxes whose Phase-1a usability% has reached
            // the user-defined "Round Robin Box Usability %" threshold. Boxes
            // below the threshold skip RR entirely (their leftover qty stays
            // unallocated). When RR runs it honours BOTH caps:
            //   • SKU Max  = SKUMax − SOH − cumItem  (cumulative across phases)
            //   • EOM Div  = TargetEOM − DivSOH − cumDiv (cumulative across phases)
            var p1NormalQtyForBox = lineRemain.Select((rem, i) => (long)(grpLines[i].Qty - rem)).Sum();
            var p1UsabilityPct    = boxQty > 0 ? (decimal)p1NormalQtyForBox * 100m / boxQty : 0m;
            if (p1UsabilityPct >= req.OverrideUsabilityPct)
            {
                for (int idx = 0; idx < grpLines.Count; idx++)
                {
                    if (lineRemain[idx] <= 0) continue;
                    int rrTaken = AllocateLineRoundRobin(
                        phaseTag: "P1_RR",
                        grpLines[idx], lineRemain[idx], batch, req,
                        eomByDiv, itemDiv, sohMap, sohByStoreDiv,
                        allocStoreItem, allocStoreDiv,
                        p1RrSI, p1RrSD,
                        boxAllocByPhase,
                        output, trace,
                        respectDivCap: true);
                    lineRemain[idx] -= rrTaken;
                    p1RrQty  += rrTaken;
                    totalQty += rrTaken;
                }
            }

            // (No Phase-1c step anymore — EOM Balance is a hard cap. Boxes whose
            // qty can't be placed after 1a + 1b stay partially allocated.)
        }

        // ============================================================
        // PHASE 2 — Non-LPM Boxes
        // ============================================================
        // Group Non-LPM boxes; sort by **total Qty DESC** so the largest boxes
        // claim store capacity first, then by overlap with Phase 1 items DESC
        // (so among boxes of similar size, those whose items already started
        // filling SKU Balance in Phase 1 come first).
        // Within each box, the store-fill order is unchanged: Priority Rank ASC,
        // Wt-Avg-Sold-Qty DESC (set when eomByDiv was sorted earlier).
        var p2Groups = nonLpmBoxes
            .GroupBy(b => b.BoxNo)
            .Select(g => new
            {
                BoxNo   = g.Key,
                Lines   = g.ToList(),
                TotalQty = g.Sum(x => (long)x.Qty),
                OverlapCount = g.Count(x => phase1Items.Contains(x.ItemCode)),
            })
            .OrderByDescending(x => x.TotalQty)            // ← FLIPPED: largest boxes first
            .ThenByDescending(x => x.OverlapCount)
            .ToList();

        foreach (var grp in p2Groups)
        {
            distinctBoxes.Add(grp.BoxNo);
            var lineRemain = grp.Lines.Select(l => l.Qty).ToArray();

            // 2a normal allocation — SKU + EOM caps both apply
            for (int idx = 0; idx < grp.Lines.Count; idx++)
            {
                var line = grp.Lines[idx];
                lineRemain[idx] = AllocateLineNormal(
                    isPhase1: false, line, batch, req,
                    eomByDiv, itemDiv, sohMap, sohByStoreDiv,
                    allocStoreItem, allocStoreDiv,
                    p2NormalSI, p2NormalSD,
                    boxAllocByPhase,
                    output, trace,
                    ref p2NormalLines, ref p2NormalQty,
                    ref itemsWithoutDiv, ref boxesSkipped);
                totalQty += line.Qty - lineRemain[idx];
            }

            // 2b RR — same shape as Phase 1b: gated by post-Phase-2a usability%
            // and honours both SKU and EOM caps (cumulative across all phases).
            var boxQty2            = grp.TotalQty;
            var p2NormalQtyForBox  = lineRemain.Select((rem, i) => (long)(grp.Lines[i].Qty - rem)).Sum();
            var p2UsabilityPct     = boxQty2 > 0 ? (decimal)p2NormalQtyForBox * 100m / boxQty2 : 0m;
            if (p2UsabilityPct >= req.OverrideUsabilityPct)
            {
                for (int idx = 0; idx < grp.Lines.Count; idx++)
                {
                    if (lineRemain[idx] <= 0) continue;
                    int rrTaken = AllocateLineRoundRobin(
                        phaseTag: "P2_RR",
                        grp.Lines[idx], lineRemain[idx], batch, req,
                        eomByDiv, itemDiv, sohMap, sohByStoreDiv,
                        allocStoreItem, allocStoreDiv,
                        p2RrSI, p2RrSD,
                        boxAllocByPhase,
                        output, trace,
                        respectDivCap: true);
                    lineRemain[idx] -= rrTaken;
                    p2RrQty  += rrTaken;
                    totalQty += rrTaken;
                }
            }

            // (No Phase-2c step anymore — EOM Balance is a hard cap. Boxes whose
            // qty can't be placed after 2a + 2b stay partially allocated.)
        }

        // Compute final RR line counts by walking output ONCE (O(n)).
        // P1_RR + P1_OVR both count toward the Phase-1 RR total (same for P2).
        foreach (var o in output)
        {
            if (o.Phase == "P1_RR" || o.Phase == "P1_OVR") p1RrLines++;
            else if (o.Phase == "P2_RR" || o.Phase == "P2_OVR") p2RrLines++;
        }
        msAllocate = swStep.ElapsedMilliseconds; swStep.Restart();

        // ---------------- Persist ----------------
        // Generous timeout — for very large runs the audit interceptor's
        // serialization + the actual UPDATE round-trip can exceed the default 30s
        // when the server is under load. 5 minutes is more than enough for one
        // batch row UPDATE plus its single audit-log INSERT.
        db.Database.SetCommandTimeout(300);

        // Update batch header first (count totals).
        batch.BoxesProcessed = distinctBoxes.Count;
        batch.LinesGenerated = output.Count;
        batch.TotalQty       = totalQty;
        await db.SaveChangesAsync(ct);

        // Bulk-copy everything heavy. EF AddRange + SaveChanges on millions of
        // rows would take many minutes / hours, so we use SqlBulkCopy.
        if (output.Count > 0)
            await BulkInsertOutputAsync((SqlConnection)conn, output, ct);
        msPersistOutput = swStep.ElapsedMilliseconds; swStep.Restart();

        if (trace.Count > 0)
            await BulkInsertTraceAsync((SqlConnection)conn, trace, ct);
        msPersistTrace = swStep.ElapsedMilliseconds; swStep.Restart();

        await BulkInsertStoreItemBalancesAsync((SqlConnection)conn, batch.LPMBatchNo, batch.CreateTS,
            sohMap, eomByDiv, itemDiv,
            p1NormalSI, p1RrSI, p2NormalSI, p2RrSI, ct);

        await BulkInsertStoreDivBalancesAsync((SqlConnection)conn, batch.LPMBatchNo, batch.CreateTS,
            sohByStoreDiv, eomByDiv,
            p1NormalSD, p1RrSD, p2NormalSD, p2RrSD, ct);

        await BulkInsertBoxBalancesAsync((SqlConnection)conn, batch.LPMBatchNo, batch.CreateTS,
            lpmBoxes, nonLpmBoxes, boxAllocByPhase, ct);
        msPersistBalances = swStep.ElapsedMilliseconds;

        return new LpmSimGenerateResult
        {
            LPMBatchNo           = batch.LPMBatchNo,
            BoxesProcessed       = distinctBoxes.Count,
            LinesGenerated       = output.Count,
            TotalQty             = totalQty,
            ItemsWithoutDivision = itemsWithoutDiv,
            BoxesSkipped         = boxesSkipped,
            P1NormalLines        = p1NormalLines,
            P1RrLines            = p1RrLines,
            P2NormalLines        = p2NormalLines,
            P2RrLines            = p2RrLines,
            P1NormalQty          = p1NormalQty,
            P1RrQty              = p1RrQty,
            P2NormalQty          = p2NormalQty,
            P2RrQty              = p2RrQty,
            MsReadBoxes          = msReadBoxes,
            MsReadItemDiv        = msReadItemDiv,
            MsReadSoh            = msReadSoh,
            MsReadEom            = msReadEom,
            MsAllocate           = msAllocate,
            MsPersistOutput      = msPersistOutput,
            MsPersistTrace       = msPersistTrace,
            MsPersistBalances    = msPersistBalances,
            MsTotal              = swTotal.ElapsedMilliseconds,
            TraceRowsWritten     = trace.Count,
        };
    }

    // ============================================================
    // Allocation primitives
    // ============================================================

    /// <summary>
    /// Normal allocation for one (box, item, qty) line — distributes units
    /// in a <strong>round-robin fashion</strong>: one unit per store per cycle
    /// in priority order, repeating until the line is exhausted or every
    /// candidate store has hit its SKU/EOM cap. This guarantees an even
    /// fill-rate across stores in the division — without RR-style cycling
    /// the top-priority store would drain a box's qty before the next
    /// store gets anything.
    ///
    /// Phase tag stays <c>P1</c>/<c>P2</c> with <c>IsRoundRobin=false</c> —
    /// the round-robin behaviour is just the fill mechanism, this is still
    /// the "normal" pass. Phase 1b/2b RR remains a distinct top-up step
    /// gated by the Round Robin Box Usability % threshold.
    ///
    /// Caps applied per cycle (cumulative across all phases):
    ///   • SKU Max balance = <c>SKUMax − SOH − cumItem</c>
    ///   • EOM Div balance = <c>TargetEOM − DivSOH − cumDiv</c>
    /// Returns the qty STILL UNPLACED on this line.
    /// </summary>
    private static int AllocateLineNormal(
        bool isPhase1,
        BoxItem line,
        LpmSimBatch batch,
        LpmSimGenerateRequest req,
        Dictionary<int, List<EomStore>> eomByDiv,
        Dictionary<string, int> itemDiv,
        Dictionary<(string, string), int> sohMap,
        Dictionary<(string, int), int> sohByStoreDiv,
        Dictionary<(string Store, string Item), int> allocStoreItem,
        Dictionary<(string Store, int    Div ), int> allocStoreDiv,
        Dictionary<(string Store, string Item), int> phaseStoreItem,
        Dictionary<(string Store, int    Div ), int> phaseStoreDiv,
        Dictionary<(string Box, string Phase), long> boxAllocByPhase,
        List<LpmSimOutput> output,
        List<LpmSimAllocTrace> trace,
        ref int phaseLineCount,
        ref long phaseQtyCount,
        ref int itemsWithoutDiv,
        ref int boxesSkipped)
    {
        var phaseTag = isPhase1 ? "P1" : "P2";

        if (!itemDiv.TryGetValue(line.ItemCode, out var divCode))
        {
            itemsWithoutDiv++;
            trace.Add(NewTrace(batch, phaseTag, line, null, null,
                LineQty: line.Qty, Take: 0, Decision: "SKIP_NO_DIV"));
            return line.Qty;
        }
        if (!eomByDiv.TryGetValue(divCode, out var stores))
        {
            boxesSkipped++;
            trace.Add(NewTrace(batch, phaseTag, line, divCode, null,
                LineQty: line.Qty, Take: 0, Decision: "SKIP_NO_EOM"));
            return line.Qty;
        }

        int remaining = line.Qty;

        // ── Round-robin distribution ──
        // Bucket per (store) so we add ONE LpmSimOutput per store at the end,
        // not one per unit. Caps are re-evaluated every cycle so a store gets
        // skipped the moment its skuBalance or divBalance hits 0.
        var buckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Stores that are over a cap from cycle 1 onwards: remember the first
        // SKIP reason so we can emit a single trace row per store at the end
        // (when VerboseTrace is on). Drops the cycle-by-cycle noise.
        var skipDecision = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (req.FillStrategy == LpmSimFillStrategy.EqualFillRate)
        {
            // EqualFillRate strategy: each unit goes to the eligible store with
            // the LOWEST current FillRate% (= cumDiv / EomBalance). Tie-break is
            // PriorityRank ASC because `stores` is already in that order — first
            // match in the foreach wins on equal FillRate. This naturally pulls
            // every store toward the same Division-level fill share rather than
            // the same per-store qty.
            while (remaining > 0)
            {
                EomStore? best = null;
                decimal bestFill = decimal.MaxValue;
                int bestSku = 0; decimal bestDiv = 0m;
                int bestSoh = 0, bestDivSoh = 0, bestCumItem = 0, bestCumDiv = 0;

                foreach (var s in stores)
                {
                    var soh        = sohMap.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
                    var divSoh     = sohByStoreDiv.GetValueOrDefault((s.StoreID, divCode), 0);
                    var cumItem    = allocStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
                    var cumDiv     = allocStoreDiv .GetValueOrDefault((s.StoreID, divCode),       0);
                    var skuBalance = s.SKUMax - soh - cumItem;
                    var divBalance = (decimal)s.TargetEOM - divSoh - cumDiv;

                    if (skuBalance <= 0) { if (req.VerboseTrace) skipDecision.TryAdd(s.StoreID, "SKIP_SKUMAX"); continue; }
                    if (divBalance <= 0) { if (req.VerboseTrace) skipDecision.TryAdd(s.StoreID, "SKIP_TARGET"); continue; }

                    var eomBalance = (decimal)s.TargetEOM - divSoh;
                    if (eomBalance <= 0m) continue;       // no headroom to begin with — pure capped store
                    var fillRate = (decimal)cumDiv / eomBalance;

                    if (fillRate < bestFill)
                    {
                        bestFill   = fillRate; best = s;
                        bestSku    = skuBalance; bestDiv = divBalance;
                        bestSoh    = soh; bestDivSoh = divSoh;
                        bestCumItem= cumItem; bestCumDiv = cumDiv;
                    }
                }

                if (best is null) break;          // no eligible store — line stops

                buckets[best.StoreID] = buckets.GetValueOrDefault(best.StoreID, 0) + 1;
                allocStoreItem[(best.StoreID, line.ItemCode)] = bestCumItem + 1;
                allocStoreDiv [(best.StoreID, divCode)]       = bestCumDiv  + 1;
                phaseStoreItem[(best.StoreID, line.ItemCode)] = phaseStoreItem.GetValueOrDefault((best.StoreID, line.ItemCode), 0) + 1;
                phaseStoreDiv [(best.StoreID, divCode)]       = phaseStoreDiv .GetValueOrDefault((best.StoreID, divCode),       0) + 1;
                remaining--;
                skipDecision.Remove(best.StoreID);
            }
        }
        else
        {
            // EqualPerStore strategy (default): 1 unit per store per cycle in
            // PriorityRank order. Hard guard against runaway loop: at most one
            // cycle per remaining unit.
            var maxCycles = remaining + 1;
            int cycle = 0;
            while (remaining > 0 && cycle < maxCycles)
            {
                bool tookAnyThisCycle = false;
                foreach (var s in stores)
                {
                    if (remaining <= 0) break;

                    var soh        = sohMap.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
                    var divSoh     = sohByStoreDiv.GetValueOrDefault((s.StoreID, divCode), 0);
                    var cumItem    = allocStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
                    var cumDiv     = allocStoreDiv .GetValueOrDefault((s.StoreID, divCode),       0);
                    var skuBalance = s.SKUMax - soh - cumItem;
                    var divBalance = (decimal)s.TargetEOM - divSoh - cumDiv;

                    if (skuBalance <= 0) { if (req.VerboseTrace) skipDecision.TryAdd(s.StoreID, "SKIP_SKUMAX"); continue; }
                    if (divBalance <= 0) { if (req.VerboseTrace) skipDecision.TryAdd(s.StoreID, "SKIP_TARGET"); continue; }

                    buckets[s.StoreID] = buckets.GetValueOrDefault(s.StoreID, 0) + 1;
                    allocStoreItem[(s.StoreID, line.ItemCode)] = cumItem + 1;
                    allocStoreDiv [(s.StoreID, divCode)]       = cumDiv  + 1;
                    phaseStoreItem[(s.StoreID, line.ItemCode)] = phaseStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0) + 1;
                    phaseStoreDiv [(s.StoreID, divCode)]       = phaseStoreDiv .GetValueOrDefault((s.StoreID, divCode),       0) + 1;
                    remaining--;
                    tookAnyThisCycle = true;
                    skipDecision.Remove(s.StoreID);
                }
                if (!tookAnyThisCycle) break;
                cycle++;
            }
        }

        // Emit one ALLOC row per (store) carrying the bucketed qty.
        foreach (var s in stores)
        {
            if (!buckets.TryGetValue(s.StoreID, out var qty) || qty <= 0) continue;

            output.Add(new LpmSimOutput
            {
                LPMBatchNo   = batch.LPMBatchNo,
                BoxNo        = line.BoxNo,
                LPMDt        = line.LPMDt,
                Itemcode     = line.ItemCode,
                Qty          = qty,
                StoreID      = s.StoreID,
                CreateTS     = batch.CreateTS,
                CreatedBy    = req.User,
                Phase        = phaseTag,
                IsRoundRobin = false, // still NORMAL phase — RR-style is just the fill mechanism
            });
            boxAllocByPhase[(line.BoxNo, phaseTag)] = boxAllocByPhase.GetValueOrDefault((line.BoxNo, phaseTag), 0L) + qty;

            // Trace row reflects post-loop cumulative state.
            var soh        = sohMap.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
            var divSoh     = sohByStoreDiv.GetValueOrDefault((s.StoreID, divCode), 0);
            var cumItem    = allocStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
            var cumDiv     = allocStoreDiv .GetValueOrDefault((s.StoreID, divCode),       0);
            var skuBalance = s.SKUMax - soh - cumItem;
            var divBalance = (decimal)s.TargetEOM - divSoh - cumDiv;
            var t = NewTraceForCandidate(batch, phaseTag, line, divCode, s,
                soh, divSoh, skuBalance, cumItem, cumDiv, divBalance);
            t.Take     = qty;
            t.Decision = "ALLOC";
            trace.Add(t);

            phaseLineCount++;
            phaseQtyCount += qty;
        }

        // Emit at most one SKIP_* trace per store that was capped throughout.
        if (req.VerboseTrace && skipDecision.Count > 0)
        {
            foreach (var (storeId, decision) in skipDecision)
            {
                var s = stores.FirstOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.StoreID, storeId));
                if (s is null) continue;
                var soh        = sohMap.GetValueOrDefault((storeId, line.ItemCode), 0);
                var divSoh     = sohByStoreDiv.GetValueOrDefault((storeId, divCode), 0);
                var cumItem    = allocStoreItem.GetValueOrDefault((storeId, line.ItemCode), 0);
                var cumDiv     = allocStoreDiv .GetValueOrDefault((storeId, divCode),       0);
                var skuBalance = s.SKUMax - soh - cumItem;
                var divBalance = (decimal)s.TargetEOM - divSoh - cumDiv;
                var t = NewTraceForCandidate(batch, phaseTag, line, divCode, s,
                    soh, divSoh, skuBalance, cumItem, cumDiv, divBalance);
                t.Take     = 0;
                t.Decision = decision;
                trace.Add(t);
            }
        }

        return remaining;
    }

    /// <summary>
    /// Round-robin pass: deals 1 unit per store per cycle in priority order
    /// until either the line's remaining qty is exhausted or no candidate
    /// stores accept more units.
    ///
    /// As of the "Round Robin Box Usability %" change RR honours BOTH caps:
    ///   • SKU Max balance = <c>SKUMax − SOH(Store,Item) − cumItem</c>
    ///                       (where cumItem includes prior normal AND RR allocations)
    ///   • EOM Div balance = <c>TargetEOM − DivSOH(Store,Div) − cumDiv</c>
    ///                       (where cumDiv likewise spans normal + RR)
    ///
    /// <paramref name="respectDivCap"/> kept for signature compatibility — always
    /// true now that EOM is a hard cap. Boxes whose remaining qty cannot be placed
    /// without crossing either cap stay partially allocated.
    /// <list type="bullet">
    ///   <item><c>true</c>  — RR honours SKU + EOM caps, stops adding to a
    ///                        (Store, Item) when SKUMax is reached and to a
    ///                        (Store, Div)  when EOM Balance is reached.
    ///                        Used by Phase 1b / Phase 2b.</item>
    ///   <item><c>false</c> — RR ignores both caps and pushes the box toward 100%.
    ///                        Used by Phase 1c / Phase 2c (the threshold-triggered
    ///                        final RR — only fires when box usability ≥ Override %).</item>
    /// </list>
    /// </summary>
    private static int AllocateLineRoundRobin(
        string phaseTag,
        BoxItem line, int remaining,
        LpmSimBatch batch,
        LpmSimGenerateRequest req,
        Dictionary<int, List<EomStore>> eomByDiv,
        Dictionary<string, int> itemDiv,
        Dictionary<(string, string), int> sohMap,
        Dictionary<(string, int), int> sohByStoreDiv,
        Dictionary<(string Store, string Item), int> allocStoreItem,
        Dictionary<(string Store, int    Div ), int> allocStoreDiv,
        Dictionary<(string Store, string Item), int> phaseStoreItem,
        Dictionary<(string Store, int    Div ), int> phaseStoreDiv,
        Dictionary<(string Box, string Phase), long> boxAllocByPhase,
        List<LpmSimOutput> output,
        List<LpmSimAllocTrace> trace,
        bool respectDivCap)
    {
        if (remaining <= 0) return 0;
        if (!itemDiv.TryGetValue(line.ItemCode, out var divCode)) return 0;
        if (!eomByDiv.TryGetValue(divCode, out var stores) || stores.Count == 0) return 0;

        // Bucket per-(box,item,store) so we add ONE LpmSimOutput per (box,item,store)
        // across the cycles, not one per unit.
        var buckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int placed = 0;
        // Hard guard against runaway loop; in practice limit = stores.Count cycles is plenty.
        var maxCycles = remaining + 1;
        int cycle = 0;
        while (remaining > 0 && cycle < maxCycles)
        {
            bool tookAnyThisCycle = false;
            foreach (var s in stores)
            {
                if (remaining <= 0) break;

                // SKU Max cap (always enforced) — SKUMax − SOH − cumulative
                // (cumItem already counts prior normal AND prior RR allocations).
                var soh        = sohMap.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
                var cumItem    = allocStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
                var skuHeadroom = s.SKUMax - soh - cumItem;
                if (skuHeadroom <= 0) continue;     // skip — SKU cap already full for this store-item

                if (respectDivCap)
                {
                    var divSoh = sohByStoreDiv.GetValueOrDefault((s.StoreID, divCode), 0);
                    var cumDiv = allocStoreDiv .GetValueOrDefault((s.StoreID, divCode), 0);
                    var divHeadroom = (decimal)s.TargetEOM - divSoh - cumDiv;
                    if (divHeadroom <= 0) continue;     // skip — div cap already full
                }
                buckets[s.StoreID] = buckets.GetValueOrDefault(s.StoreID, 0) + 1;
                allocStoreItem[(s.StoreID, line.ItemCode)] = allocStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0) + 1;
                allocStoreDiv [(s.StoreID, divCode)]       = allocStoreDiv .GetValueOrDefault((s.StoreID, divCode),       0) + 1;
                phaseStoreItem[(s.StoreID, line.ItemCode)] = phaseStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0) + 1;
                phaseStoreDiv [(s.StoreID, divCode)]       = phaseStoreDiv .GetValueOrDefault((s.StoreID, divCode),       0) + 1;
                remaining--;
                placed++;
                tookAnyThisCycle = true;
            }
            if (!tookAnyThisCycle) break;
            cycle++;
        }

        // Emit one output line per (box, item, store) with the cumulative bucket qty.
        foreach (var s in stores)
        {
            if (!buckets.TryGetValue(s.StoreID, out var qty) || qty <= 0) continue;
            output.Add(new LpmSimOutput
            {
                LPMBatchNo   = batch.LPMBatchNo,
                BoxNo        = line.BoxNo,
                LPMDt        = line.LPMDt,
                Itemcode     = line.ItemCode,
                Qty          = qty,
                StoreID      = s.StoreID,
                CreateTS     = batch.CreateTS,
                CreatedBy    = req.User,
                Phase        = phaseTag,
                IsRoundRobin = true,
            });
            boxAllocByPhase[(line.BoxNo, phaseTag)] = boxAllocByPhase.GetValueOrDefault((line.BoxNo, phaseTag), 0L) + qty;
            trace.Add(new LpmSimAllocTrace
            {
                LPMBatchNo = batch.LPMBatchNo,
                BoxNo      = line.BoxNo,
                ItemCode   = line.ItemCode,
                DivCode    = divCode,
                StoreID    = s.StoreID,
                LineQty    = line.Qty,
                Take       = qty,
                Decision   = "ALLOC_RR",
                Phase      = phaseTag,
                CreateTS   = batch.CreateTS,
            });
        }
        return placed;
    }

    private static LpmSimAllocTrace NewTrace(LpmSimBatch batch, string phaseTag,
        BoxItem line, int? divCode, EomStore? s, int LineQty, int Take, string Decision)
    {
        return new LpmSimAllocTrace
        {
            LPMBatchNo = batch.LPMBatchNo,
            BoxNo      = line.BoxNo,
            ItemCode   = line.ItemCode,
            DivCode    = divCode,
            StoreID    = s?.StoreID,
            SKUMax     = s?.SKUMax,
            TargetEOM  = s?.TargetEOM,
            LineQty    = LineQty,
            Take       = Take,
            Decision   = Decision,
            Phase      = phaseTag,
            CreateTS   = batch.CreateTS,
        };
    }

    private static LpmSimAllocTrace NewTraceForCandidate(
        LpmSimBatch batch, string phaseTag,
        BoxItem line, int divCode, EomStore s,
        int soh, int divSoh, int skuBalance, int cumItem, int cumDiv, decimal divBalance)
    {
        return new LpmSimAllocTrace
        {
            LPMBatchNo       = batch.LPMBatchNo,
            BoxNo            = line.BoxNo,
            ItemCode         = line.ItemCode,
            DivCode          = divCode,
            StoreID          = s.StoreID,
            SKUMax           = s.SKUMax,
            SOH_Item         = soh,
            SkuBalance       = skuBalance,
            TargetEOM        = s.TargetEOM,
            DivSOH           = divSoh,
            AlreadyAllocated = cumDiv,           // cumulative div-level allocation pre-this-attempt
            TargetRemain     = divBalance,
            LineQty          = line.Qty,
            Take             = 0,
            Decision         = "",
            Phase            = phaseTag,
            CreateTS         = batch.CreateTS,
        };
    }

    // ============================================================
    // Source readers
    // ============================================================

    private static async Task ReadBoxesAsync(
        System.Data.Common.DbConnection conn,
        bool isLpm,
        int year, int month, string seasonClause,
        IReadOnlyList<string>? warehouses,
        List<BoxItem> dest,
        CancellationToken ct)
    {
        // shopeligible <> 'E' applies to BOTH (E = already purchased, exclude).
        var lpmDtClause = isLpm
            ? "w.LPMDt IS NOT NULL AND (YEAR(w.LPMDt) < @y OR (YEAR(w.LPMDt) = @y AND MONTH(w.LPMDt) <= @m))"
            : "w.LPMDt IS NULL";
        var (whClause, whParams) = BuildWarehouseClause(warehouses);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT w.BoxNo, w.LPMDt, w.ItemCode, w.Qty
              FROM racks.dbo.whboxitems w
              INNER JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
             WHERE pt.PalletCategory = 'ELIGIBLE'
               AND ISNULL(w.ShopEligible, '') <> 'E'
               {seasonClause}
               {whClause}
               AND {lpmDtClause};";
        cmd.Parameters.Add(new SqlParameter("@y", year));
        cmd.Parameters.Add(new SqlParameter("@m", month));
        foreach (var p in whParams) cmd.Parameters.Add(p);
        cmd.CommandTimeout = 300;
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            dest.Add(new BoxItem(
                BoxNo:    rdr.GetString(0),
                LPMDt:    rdr.IsDBNull(1) ? null : rdr.GetDateTime(1),
                ItemCode: rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                Qty:      rdr.IsDBNull(3) ? 0  : rdr.GetInt32(3)));
        }
    }

    public async Task ApproveAsync(long batchNo, string user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var batch = await db.LpmSimBatches.FirstOrDefaultAsync(b => b.LPMBatchNo == batchNo, ct)
            ?? throw new InvalidOperationException($"Batch #{batchNo} not found.");
        if (batch.Status == "Approved")
            throw new InvalidOperationException("Batch is already approved.");
        batch.Status     = "Approved";
        batch.ApprovedTS = DateTime.Now;
        batch.ApprovedBy = user;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Backup LPMSIM_Output + LPMSIM_Batch, then delete in chunks so we don't
    /// pop the SQL command timeout on huge batches. Trace + balance tables are
    /// diagnostic only and are not backed up — they're regenerated on next run.
    ///
    /// Why chunked: a single CASCADE DELETE on a batch that produced millions of
    /// LPMSIM_AllocTrace rows runs as one giant transaction with row-by-row
    /// lookups in the clustered index. That times out at the default 30s. Chunked
    /// deletes (50K per pass) give SQL Server short transactions that commit fast,
    /// avoid lock escalation, and finish reliably regardless of batch size.
    /// </summary>
    public async Task DeleteAsync(long batchNo, string user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Each individual SQL statement gets up to 10 minutes — generous for the
        // backup INSERT which is single-statement and can't be chunked.
        db.Database.SetCommandTimeout(600);

        // 1) Backup just the user-visible bits (Output + Batch). Trace + balance
        //    tables can be re-derived; backing them up doubles I/O for no benefit.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO dbo.LPMSIM_Output_Backup (Id, LPMBatchNo, BoxNo, LPMDt, Itemcode, Qty, StoreID, CreateTS, CreatedBy, Phase, IsRoundRobin, BackupTS)
SELECT Id, LPMBatchNo, BoxNo, LPMDt, Itemcode, Qty, StoreID, CreateTS, CreatedBy, Phase, IsRoundRobin, SYSDATETIME()
  FROM dbo.LPMSIM_Output WHERE LPMBatchNo = {batchNo};", ct);

        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO dbo.LPMSIM_Batch_Backup (LPMBatchNo, Country, RunYear, RunMonth, RunDate, Status,
        BoxesProcessed, LinesGenerated, TotalQty, CreateTS, CreatedBy, ApprovedTS, ApprovedBy,
        Sources, Seasons, OverrideUsabilityPct, Warehouses, FillStrategy, BackupTS, BackupBy)
SELECT LPMBatchNo, Country, RunYear, RunMonth, RunDate, Status,
       BoxesProcessed, LinesGenerated, TotalQty, CreateTS, CreatedBy, ApprovedTS, ApprovedBy,
       Sources, Seasons, OverrideUsabilityPct, Warehouses, FillStrategy,
       SYSDATETIME(), {user}
  FROM dbo.LPMSIM_Batch WHERE LPMBatchNo = {batchNo};", ct);

        // 2) Chunked delete from each child table. Empty children → CASCADE is a no-op.
        //    Order matters: children before parent.
        await ChunkDeleteAsync(db, "dbo.LPMSIM_AllocTrace",       batchNo, ct);
        await ChunkDeleteAsync(db, "dbo.LPMSIM_Output",           batchNo, ct);
        await ChunkDeleteAsync(db, "dbo.LPMSIM_StoreItemBalance", batchNo, ct);
        await ChunkDeleteAsync(db, "dbo.LPMSIM_StoreDivBalance",  batchNo, ct);
        await ChunkDeleteAsync(db, "dbo.LPMSIM_BoxBalance",       batchNo, ct);

        // 3) Finally, the batch row itself.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM dbo.LPMSIM_Batch WHERE LPMBatchNo = {batchNo};", ct);
    }

    /// <summary>
    /// Deletes rows for a given LPMBatchNo in 50,000-row chunks. Each chunk is
    /// its own auto-committed statement so locks stay short and the command
    /// timeout never trips, regardless of how many rows the batch produced.
    /// </summary>
    private static async Task ChunkDeleteAsync(LpmDbContext db, string fullyQualifiedTable, long batchNo, CancellationToken ct)
    {
        const int chunk = 50_000;
        while (!ct.IsCancellationRequested)
        {
            // Parameterised; @bn is bound by EF core.
            var sql = $"DELETE TOP ({chunk}) FROM {fullyQualifiedTable} WHERE LPMBatchNo = @bn;";
            var rows = await db.Database.ExecuteSqlRawAsync(
                sql, new[] { new SqlParameter("@bn", batchNo) }, ct);
            if (rows < chunk) break;
        }
    }

    public async Task<List<LpmSimOutput>> GetBatchLinesAsync(long batchNo, int top = 5000, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LpmSimOutputs.AsNoTracking()
            .Where(x => x.LPMBatchNo == batchNo)
            .OrderBy(x => x.StoreID).ThenBy(x => x.Itemcode)
            .Take(top)
            .ToListAsync(ct);
    }

    private record BoxItem(string BoxNo, DateTime? LPMDt, string ItemCode, int Qty);
    private record EomStore(string StoreID, int DivCode, int SKUMax, decimal TargetEOM,
                            decimal PriorityRank, decimal WtAvgSold, string VolumeGroup);

    // ============================================================
    // Case-insensitive comparers for the running dictionaries.
    // SQL Server's default collation is case-insensitive, so two row keys
    // that differ only in case (e.g. ItemCode "HTTPS:" vs "https:") would
    // collide as the same PK. These comparers make C# dictionaries see them
    // as the same key, so allocation totals aggregate correctly and bulk
    // inserts never produce duplicate-key violations.
    // ============================================================

    private sealed class StoreItemComparer : IEqualityComparer<(string Store, string Item)>
    {
        public static readonly StoreItemComparer Instance = new();
        public bool Equals((string Store, string Item) x, (string Store, string Item) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Store, y.Store)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Item,  y.Item);
        public int GetHashCode((string Store, string Item) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Store ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item  ?? ""));
    }

    private sealed class StoreDivComparer : IEqualityComparer<(string Store, int Div)>
    {
        public static readonly StoreDivComparer Instance = new();
        public bool Equals((string Store, int Div) x, (string Store, int Div) y)
            => x.Div == y.Div
            && StringComparer.OrdinalIgnoreCase.Equals(x.Store, y.Store);
        public int GetHashCode((string Store, int Div) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Store ?? ""),
                obj.Div);
    }

    private sealed class BoxPhaseComparer : IEqualityComparer<(string Box, string Phase)>
    {
        public static readonly BoxPhaseComparer Instance = new();
        public bool Equals((string Box, string Phase) x, (string Box, string Phase) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Box,   y.Box)
            && StringComparer.Ordinal.Equals(x.Phase, y.Phase);  // Phase is "P1"/"P1_RR"/etc — case-sensitive literal
        public int GetHashCode((string Box, string Phase) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Box   ?? ""),
                StringComparer.Ordinal.GetHashCode(obj.Phase ?? ""));
    }

    // ============================================================
    // Bulk-insert helpers
    // ============================================================

    private static async Task BulkInsertOutputAsync(SqlConnection conn, List<LpmSimOutput> rows, CancellationToken ct)
    {
        using var dt = new System.Data.DataTable();
        dt.Columns.Add("LPMBatchNo",   typeof(long));
        dt.Columns.Add("BoxNo",        typeof(string));
        dt.Columns.Add("LPMDt",        typeof(DateTime));
        dt.Columns.Add("Itemcode",     typeof(string));
        dt.Columns.Add("Qty",          typeof(int));
        dt.Columns.Add("StoreID",      typeof(string));
        dt.Columns.Add("CreateTS",     typeof(DateTime));
        dt.Columns.Add("CreatedBy",    typeof(string));
        dt.Columns.Add("Phase",        typeof(string));
        dt.Columns.Add("IsRoundRobin", typeof(bool));

        foreach (var r in rows)
        {
            dt.Rows.Add(
                r.LPMBatchNo,
                r.BoxNo,
                (object?)r.LPMDt ?? DBNull.Value,
                r.Itemcode,
                r.Qty,
                r.StoreID,
                r.CreateTS,
                r.CreatedBy,
                r.Phase,
                r.IsRoundRobin);
        }

        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.LPMSIM_Output",
            BatchSize            = 5000,
            BulkCopyTimeout      = 600,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    private static async Task BulkInsertTraceAsync(SqlConnection conn, List<LpmSimAllocTrace> rows, CancellationToken ct)
    {
        using var dt = new System.Data.DataTable();
        dt.Columns.Add("LPMBatchNo",       typeof(long));
        dt.Columns.Add("BoxNo",            typeof(string));
        dt.Columns.Add("ItemCode",         typeof(string));
        dt.Columns.Add("DivCode",          typeof(int));
        dt.Columns.Add("StoreID",          typeof(string));
        dt.Columns.Add("SKUMax",           typeof(int));
        dt.Columns.Add("SOH_Item",         typeof(int));
        dt.Columns.Add("SkuBalance",       typeof(int));
        dt.Columns.Add("TargetEOM",        typeof(decimal));
        dt.Columns.Add("DivSOH",           typeof(int));
        dt.Columns.Add("AlreadyAllocated", typeof(decimal));
        dt.Columns.Add("TargetRemain",     typeof(decimal));
        dt.Columns.Add("LineQty",          typeof(int));
        dt.Columns.Add("Take",             typeof(int));
        dt.Columns.Add("Decision",         typeof(string));
        dt.Columns.Add("Phase",            typeof(string));
        dt.Columns.Add("CreateTS",         typeof(DateTime));

        foreach (var r in rows)
        {
            dt.Rows.Add(
                r.LPMBatchNo,
                r.BoxNo,
                r.ItemCode,
                (object?)r.DivCode          ?? DBNull.Value,
                (object?)r.StoreID          ?? DBNull.Value,
                (object?)r.SKUMax           ?? DBNull.Value,
                (object?)r.SOH_Item         ?? DBNull.Value,
                (object?)r.SkuBalance       ?? DBNull.Value,
                (object?)r.TargetEOM        ?? DBNull.Value,
                (object?)r.DivSOH           ?? DBNull.Value,
                (object?)r.AlreadyAllocated ?? DBNull.Value,
                (object?)r.TargetRemain     ?? DBNull.Value,
                r.LineQty,
                r.Take,
                r.Decision,
                r.Phase,
                r.CreateTS);
        }

        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.LPMSIM_AllocTrace",
            BatchSize            = 5000,
            BulkCopyTimeout      = 600,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    private static async Task BulkInsertStoreItemBalancesAsync(
        SqlConnection conn, long batchNo, DateTime createTs,
        Dictionary<(string, string), int> sohMap,
        Dictionary<int, List<EomStore>> eomByDiv,
        Dictionary<string, int> itemDiv,
        Dictionary<(string Store, string Item), int> p1n,
        Dictionary<(string Store, string Item), int> p1r,
        Dictionary<(string Store, string Item), int> p2n,
        Dictionary<(string Store, string Item), int> p2r,
        CancellationToken ct)
    {
        // Collect every (Store,Item) that appears in any phase totals — case-insensitive
        // so duplicates that differ only in case are merged (matches SQL PK collation).
        var keys = new HashSet<(string, string)>(StoreItemComparer.Instance);
        keys.UnionWith(p1n.Keys);
        keys.UnionWith(p1r.Keys);
        keys.UnionWith(p2n.Keys);
        keys.UnionWith(p2r.Keys);
        if (keys.Count == 0) return;

        // SKUMax per (Store, Div) — pull from eomByDiv index. Case-insensitive
        // on StoreID so the lookup matches the keys built above.
        var skuMaxLookup = new Dictionary<(string Store, int Div), int>(StoreDivComparer.Instance);
        foreach (var (div, list) in eomByDiv)
            foreach (var s in list)
                skuMaxLookup[(s.StoreID, div)] = s.SKUMax;

        using var dt = new System.Data.DataTable();
        dt.Columns.Add("LPMBatchNo",          typeof(long));
        dt.Columns.Add("StoreID",             typeof(string));
        dt.Columns.Add("ItemCode",            typeof(string));
        dt.Columns.Add("DivCode",             typeof(int));
        dt.Columns.Add("SKUMax",              typeof(int));
        dt.Columns.Add("SOH_Item",            typeof(int));
        dt.Columns.Add("P1_NormalAlloc",      typeof(int));
        dt.Columns.Add("P1_RR",               typeof(int));
        dt.Columns.Add("P2_NormalAlloc",      typeof(int));
        dt.Columns.Add("P2_RR",               typeof(int));
        dt.Columns.Add("TotalAlloc",          typeof(int));
        dt.Columns.Add("SkuBalanceRemaining", typeof(int));
        dt.Columns.Add("CreateTS",            typeof(DateTime));

        foreach (var (store, item) in keys)
        {
            int div = itemDiv.TryGetValue(item, out var d) ? d : 0;
            int? skuMax = (div != 0 && skuMaxLookup.TryGetValue((store, div), out var sm)) ? sm : null;
            int soh = sohMap.GetValueOrDefault((store, item), 0);
            int a = p1n.GetValueOrDefault((store, item), 0);
            int b = p1r.GetValueOrDefault((store, item), 0);
            int c = p2n.GetValueOrDefault((store, item), 0);
            int e = p2r.GetValueOrDefault((store, item), 0);
            int total = a + b + c + e;
            int? remain = skuMax.HasValue ? skuMax.Value - soh - total : null;
            dt.Rows.Add(
                batchNo, store, item,
                div == 0 ? (object)DBNull.Value : div,
                (object?)skuMax ?? DBNull.Value,
                soh, a, b, c, e, total,
                (object?)remain ?? DBNull.Value,
                createTs);
        }

        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.LPMSIM_StoreItemBalance",
            BatchSize            = 5000,
            BulkCopyTimeout      = 600,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    private static async Task BulkInsertStoreDivBalancesAsync(
        SqlConnection conn, long batchNo, DateTime createTs,
        Dictionary<(string Store, int Div), int> sohByStoreDiv,
        Dictionary<int, List<EomStore>> eomByDiv,
        Dictionary<(string Store, int Div), int> p1n,
        Dictionary<(string Store, int Div), int> p1r,
        Dictionary<(string Store, int Div), int> p2n,
        Dictionary<(string Store, int Div), int> p2r,
        CancellationToken ct)
    {
        // Case-insensitive on StoreID — matches SQL PK collation, dedupes case-only differences.
        var keys = new HashSet<(string, int)>(StoreDivComparer.Instance);
        keys.UnionWith(p1n.Keys);
        keys.UnionWith(p1r.Keys);
        keys.UnionWith(p2n.Keys);
        keys.UnionWith(p2r.Keys);
        if (keys.Count == 0) return;

        var eomLookup = new Dictionary<(string Store, int Div), decimal>(StoreDivComparer.Instance);
        foreach (var (div, list) in eomByDiv)
            foreach (var s in list)
                eomLookup[(s.StoreID, div)] = s.TargetEOM;

        using var dt = new System.Data.DataTable();
        dt.Columns.Add("LPMBatchNo",          typeof(long));
        dt.Columns.Add("StoreID",             typeof(string));
        dt.Columns.Add("DivCode",             typeof(int));
        dt.Columns.Add("TargetEOM",           typeof(decimal));
        dt.Columns.Add("DivSOH",              typeof(int));
        dt.Columns.Add("P1_NormalAlloc",      typeof(int));
        dt.Columns.Add("P1_RR",               typeof(int));
        dt.Columns.Add("P2_NormalAlloc",      typeof(int));
        dt.Columns.Add("P2_RR",               typeof(int));
        dt.Columns.Add("TotalAlloc",          typeof(int));
        dt.Columns.Add("DivBalanceRemaining", typeof(decimal));
        dt.Columns.Add("CreateTS",            typeof(DateTime));

        foreach (var (store, div) in keys)
        {
            decimal? eom = eomLookup.TryGetValue((store, div), out var e) ? e : null;
            int divSoh = sohByStoreDiv.GetValueOrDefault((store, div), 0);
            int a = p1n.GetValueOrDefault((store, div), 0);
            int b = p1r.GetValueOrDefault((store, div), 0);
            int c = p2n.GetValueOrDefault((store, div), 0);
            int f = p2r.GetValueOrDefault((store, div), 0);
            int total = a + b + c + f;
            decimal? remain = eom.HasValue ? eom.Value - divSoh - total : null;
            dt.Rows.Add(
                batchNo, store, div,
                (object?)eom ?? DBNull.Value,
                divSoh, a, b, c, f, total,
                (object?)remain ?? DBNull.Value,
                createTs);
        }

        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.LPMSIM_StoreDivBalance",
            BatchSize            = 5000,
            BulkCopyTimeout      = 600,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    /// <summary>
    /// Per-box snapshot — one row per source box that fed Phase 1 or Phase 2.
    /// Matches the SIM Boxes report tab so a SELECT * already gives the same
    /// Box-level numbers (BoxQty, P1/P2 Normal/RR, Usability %, LeftOver).
    /// </summary>
    private static async Task BulkInsertBoxBalancesAsync(
        SqlConnection conn, long batchNo, DateTime createTs,
        List<BoxItem> lpmBoxes, List<BoxItem> nonLpmBoxes,
        Dictionary<(string Box, string Phase), long> boxAllocByPhase,
        CancellationToken ct)
    {
        // Index source rows once: BoxQty (sum of per-line Qty) + LPMDt (any).
        // Case-insensitive on BoxNo so it matches the dictionary key collation.
        var boxMeta = new Dictionary<string, (long BoxQty, DateTime? Lpmdt, string Kind)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var ln in lpmBoxes)
        {
            if (!boxMeta.TryGetValue(ln.BoxNo, out var m))
                boxMeta[ln.BoxNo] = (ln.Qty, ln.LPMDt, "LPM");
            else
                boxMeta[ln.BoxNo] = (m.BoxQty + ln.Qty, m.Lpmdt ?? ln.LPMDt, "LPM");
        }
        foreach (var ln in nonLpmBoxes)
        {
            if (!boxMeta.TryGetValue(ln.BoxNo, out var m))
                boxMeta[ln.BoxNo] = (ln.Qty, ln.LPMDt, "Non-LPM");
            else
                boxMeta[ln.BoxNo] = (m.BoxQty + ln.Qty, m.Lpmdt ?? ln.LPMDt, "Non-LPM");
        }
        if (boxMeta.Count == 0) return;

        using var dt = new System.Data.DataTable();
        dt.Columns.Add("LPMBatchNo",     typeof(long));
        dt.Columns.Add("BoxNo",          typeof(string));
        dt.Columns.Add("BoxKind",        typeof(string));
        dt.Columns.Add("LPMDt",          typeof(DateTime));
        dt.Columns.Add("BoxQty",         typeof(long));
        dt.Columns.Add("P1_NormalAlloc", typeof(int));
        dt.Columns.Add("P1_RR",          typeof(int));
        dt.Columns.Add("P2_NormalAlloc", typeof(int));
        dt.Columns.Add("P2_RR",          typeof(int));
        dt.Columns.Add("TotalAlloc",     typeof(long));
        dt.Columns.Add("LeftOverQty",    typeof(long));
        dt.Columns.Add("UsabilityPct",   typeof(decimal));
        dt.Columns.Add("CreateTS",       typeof(DateTime));

        foreach (var (box, m) in boxMeta)
        {
            int p1n = (int)boxAllocByPhase.GetValueOrDefault((box, "P1"),    0L);
            int p1r = (int)boxAllocByPhase.GetValueOrDefault((box, "P1_RR"), 0L);
            int p2n = (int)boxAllocByPhase.GetValueOrDefault((box, "P2"),    0L);
            int p2r = (int)boxAllocByPhase.GetValueOrDefault((box, "P2_RR"), 0L);
            long total = (long)p1n + p1r + p2n + p2r;
            long left  = Math.Max(0L, m.BoxQty - total);
            decimal use = m.BoxQty > 0
                ? Math.Round((decimal)total * 100m / m.BoxQty, 1)
                : 0m;
            dt.Rows.Add(
                batchNo, box, m.Kind,
                (object?)m.Lpmdt ?? DBNull.Value,
                m.BoxQty, p1n, p1r, p2n, p2r, total, left, use, createTs);
        }

        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.LPMSIM_BoxBalance",
            BatchSize            = 5000,
            BulkCopyTimeout      = 600,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }
}
