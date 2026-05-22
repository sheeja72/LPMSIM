using LpmSim.Core;
using LpmSim.Core.Entities;
using LpmSim.Data.Warehouse;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;   // 1.14.67 — GetDbTransaction() extension on IDbContextTransaction

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
        bool includePurchasedBoxes = false,
        IReadOnlyList<string>? palletCategories = null,
        IReadOnlyList<DateTime>? lpmMonths = null,
        long? viewBatchNo = null,
        // 1.14.67 — Pass the current page user so this method can hide
        // "Running" batches that another user started but hasn't finished.
        // Null defaults to the legacy behaviour (no per-user filter — every
        // batch visible) for backward compat with any tests/callers that
        // don't have a user context handy.
        string? currentUser = null,
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
        // 1.14.101 — Hoisted to method scope so the separate closed-box query
        // below (outside this try block) can reuse them.
        string? whSrc = null;
        string? dataNameForClosed = null;
        try
        {
            // Country-aware whboxitems source — UAE uses racks.dbo.whboxitems,
            // others use [<DataName>].dbo.WHBoxItemsExport.
            whSrc      = await WhBoxItemsSource.ResolveAsync(conn, country, ct);
            // 1.14.101 — dataName needed for the separate closed-box query
            // (non-UAE only). UAE returns null and uses the upcboxhead
            // shape; non-UAE uses the [<DataName>] FQTN.
            dataNameForClosed = await WhBoxItemsSource.ResolveDataNameAsync((SqlConnection)conn, country, ct);
            // 1.14.74 — The closed-box exclusion (1.14.70) is now applied
            // ONLY in the allocator (ReadBoxesAsync) and surfaced in the
            // Allocation Gap diagnostic. The Input Readiness counts grid
            // shows the FULL whboxitems totals — operator preference: the
            // grid is a "what's in the warehouse" view, not a "what will
            // ship" view. So the closed-box predicate is intentionally
            // NOT applied to the SELECT below.
            using var cmd = conn.CreateCommand();
            // Build a parameterised IN-list for warehouses (empty = no filter, all warehouses).
            var (whClause, whParams)         = BuildWarehouseClause(warehouses);
            // Same for pallet categories. Empty/null → no filter (every category counted).
            var (palletClause, palletParams) = BuildPalletCategoryClause(palletCategories);
            // LPMDt clause — REPLACES the default "< endExclusive" cap when the
            // user has picked specific months (otherwise we'd AND the two and
            // get only the intersection — i.e., the planner's future months
            // would be silently dropped).
            //   • Months picked  → "AND (LPMDt IS NULL OR LPMDt in any selected month)"
            //   • Months empty   → legacy "AND (LPMDt IS NULL OR LPMDt < endExclusive)"
            var lpmMonthCheckParams = new List<SqlParameter>();
            string lpmDtClause;
            bool useEndExclusive;
            if (lpmMonths is { Count: > 0 })
            {
                var ors = new List<string>(lpmMonths.Count);
                for (int i = 0; i < lpmMonths.Count; i++)
                {
                    var ms = new DateTime(lpmMonths[i].Year, lpmMonths[i].Month, 1);
                    var me = ms.AddMonths(1);
                    ors.Add($"(w.LPMDt >= @lm{i}_s AND w.LPMDt < @lm{i}_e)");
                    lpmMonthCheckParams.Add(new SqlParameter($"@lm{i}_s", ms));
                    lpmMonthCheckParams.Add(new SqlParameter($"@lm{i}_e", me));
                }
                lpmDtClause     = "AND (w.LPMDt IS NULL OR " + string.Join(" OR ", ors) + ")";
                useEndExclusive = false;
            }
            else
            {
                // 1.14.11: "All months" used to mean "current + elapsed only"
                // (LPMDt < @endExclusive). The planner asked for the
                // eligibility view to show LPM across ALL months — including
                // future-dated LPM tags — so dropping the date filter when
                // the LPM Months selector is empty. Non-LPM (LPMDt IS NULL)
                // is unaffected. Specific months still restrict as before.
                lpmDtClause     = "";
                useEndExclusive = true;  // kept for the @endExclusive param below
            }
            // 1.14.11: Two changes to this query:
            //   • Dropped the LPMDt date filter from the WHERE when
            //     lpmMonths is empty ("All months") so the LPM column
            //     in the eligibility view shows ALL LPM dates including
            //     future-dated tags. When lpmMonths is specified, the
            //     specific-months filter still applies.
            //   • Dropped the WHERE-level ShopEligible filter and moved
            //     it INTO the CASE statements so each segment splits
            //     into Purchased + Non-Purchased buckets. The "selBoxes"
            //     downstream logic still respects the includePurchasedBoxes
            //     toggle; only the SQL changes.
            // 1.14.18 — pallettype JOIN restored for BOX-level Season counting.
            // The Input Readiness grid shows boxes/qty/lines bucketed by
            // BOX-level (pt.Season) — same as the box-selection logic SIM
            // Generate uses. PalletCategory still uses w.PalletCategory (1.14.17).
            cmd.CommandText = $@"
                SELECT
                    -- LPM × Summer × Purchased
                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') <> 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN 1 ELSE 0 END) AS LpmSummerLines,
                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') <> 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LpmSummerQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') <> 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN w.BoxNo END) AS LpmSummerBoxes,

                    -- Non-LPM × Summer × Purchased
                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') <> 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN 1 ELSE 0 END) AS NonLpmSummerLines,
                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') <> 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS NonLpmSummerQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') <> 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN w.BoxNo END) AS NonLpmSummerBoxes,

                    -- LPM × Winter × Purchased
                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') = 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN 1 ELSE 0 END) AS LpmWinterLines,
                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') = 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LpmWinterQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') = 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN w.BoxNo END) AS LpmWinterBoxes,

                    -- Non-LPM × Winter × Purchased
                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') = 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN 1 ELSE 0 END) AS NonLpmWinterLines,
                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') = 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS NonLpmWinterQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') = 'W' AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E') THEN w.BoxNo END) AS NonLpmWinterBoxes,

                    -- LPM × Summer × Non-Purchased  (1.14.11)
                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') <> 'W' AND w.ShopEligible = 'E' THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LpmSummerNpQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') <> 'W' AND w.ShopEligible = 'E' THEN w.BoxNo END) AS LpmSummerNpBoxes,

                    -- Non-LPM × Summer × Non-Purchased
                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') <> 'W' AND w.ShopEligible = 'E' THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS NonLpmSummerNpQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') <> 'W' AND w.ShopEligible = 'E' THEN w.BoxNo END) AS NonLpmSummerNpBoxes,

                    -- LPM × Winter × Non-Purchased
                    SUM(CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') = 'W' AND w.ShopEligible = 'E' THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LpmWinterNpQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NOT NULL AND ISNULL(pt.Season, '') = 'W' AND w.ShopEligible = 'E' THEN w.BoxNo END) AS LpmWinterNpBoxes,

                    -- Non-LPM × Winter × Non-Purchased
                    SUM(CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') = 'W' AND w.ShopEligible = 'E' THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS NonLpmWinterNpQty,
                    COUNT(DISTINCT CASE WHEN w.LPMDt IS NULL AND ISNULL(pt.Season, '') = 'W' AND w.ShopEligible = 'E' THEN w.BoxNo END) AS NonLpmWinterNpBoxes
                  -- 1.14.101 — Closed-box subsets MOVED to a separate query
                  -- (was inline 1.14.99 — 4 per-row EXISTS sub-queries
                  -- against USA.dbo.upcboxhead inflated the runtime and
                  -- could throw / time out, killing the entire BoxSegments
                  -- result via the swallowing catch below. Now isolated.)
                  FROM {whSrc} w
                  INNER JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
                 WHERE 1 = 1
                   {palletClause}
                   {lpmDtClause}
                   {whClause};";
            // 1.14.74 — Closed-box exclusion REMOVED from this readiness
            // count (was added in 1.14.70). The grid now shows the
            // unfiltered whboxitems counts — operator preference. Closed
            // boxes still get filtered out in ReadBoxesAsync (the
            // allocator never sees them) and they appear as CLOSED_BOX
            // entries in the Allocation Gap tab.
            // First day of the month AFTER the run period — half-open
            // interval excludes future-dated LPM boxes.
            cmd.Parameters.Add(new SqlParameter("@endExclusive",
                new DateTime(year, month, 1).AddMonths(1)));
            foreach (var p in whParams)            cmd.Parameters.Add(p);
            foreach (var p in palletParams)        cmd.Parameters.Add(p);
            foreach (var p in lpmMonthCheckParams) cmd.Parameters.Add(p);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                // Indexes 0–11 unchanged (purchased). 12–19 new (non-purchased).
                int  lsB  = rdr.IsDBNull(2)  ? 0 : rdr.GetInt32(2);
                long lsQ  = rdr.IsDBNull(1)  ? 0 : rdr.GetInt64(1);
                int  nsB  = rdr.IsDBNull(5)  ? 0 : rdr.GetInt32(5);
                long nsQ  = rdr.IsDBNull(4)  ? 0 : rdr.GetInt64(4);
                int  lwB  = rdr.IsDBNull(8)  ? 0 : rdr.GetInt32(8);
                long lwQ  = rdr.IsDBNull(7)  ? 0 : rdr.GetInt64(7);
                int  nwB  = rdr.IsDBNull(11) ? 0 : rdr.GetInt32(11);
                long nwQ  = rdr.IsDBNull(10) ? 0 : rdr.GetInt64(10);
                // Non-Purchased buckets (1.14.11)
                long lsQn = rdr.IsDBNull(12) ? 0 : rdr.GetInt64(12);
                int  lsBn = rdr.IsDBNull(13) ? 0 : rdr.GetInt32(13);
                long nsQn = rdr.IsDBNull(14) ? 0 : rdr.GetInt64(14);
                int  nsBn = rdr.IsDBNull(15) ? 0 : rdr.GetInt32(15);
                long lwQn = rdr.IsDBNull(16) ? 0 : rdr.GetInt64(16);
                int  lwBn = rdr.IsDBNull(17) ? 0 : rdr.GetInt32(17);
                long nwQn = rdr.IsDBNull(18) ? 0 : rdr.GetInt64(18);
                int  nwBn = rdr.IsDBNull(19) ? 0 : rdr.GetInt32(19);
                // 1.14.101 — Closed-box subsets reverted from indices 20-23.
                // Now sourced from a separate query below so a failure
                // doesn't kill the whole BoxSegments rollup.
                segments = new BoxSegmentCounts(
                    LpmSummerBoxes: lsB,      LpmSummerQty: lsQ,
                    NonLpmSummerBoxes: nsB,   NonLpmSummerQty: nsQ,
                    LpmWinterBoxes: lwB,      LpmWinterQty: lwQ,
                    NonLpmWinterBoxes: nwB,   NonLpmWinterQty: nwQ,
                    LpmSummerNpBoxes: lsBn,   LpmSummerNpQty: lsQn,
                    NonLpmSummerNpBoxes: nsBn,NonLpmSummerNpQty: nsQn,
                    LpmWinterNpBoxes: lwBn,   LpmWinterNpQty: lwQn,
                    NonLpmWinterNpBoxes: nwBn,NonLpmWinterNpQty: nwQn);
            }
        }
        catch { /* leave null */ }

        // 1.14.101 — Closed-box rollup as a SEPARATE query with its own
        // try/catch. If this fails (timeout, permission, missing source
        // table), the main BoxSegments above still renders — the closed
        // row just shows 0/0 instead of breaking the entire table.
        // Implementation switches from per-row EXISTS to an INNER JOIN
        // against a pre-filtered closed-box set so SQL Server can use
        // standard index seeks (BoxNo on whboxitems + BoxNo on the closed
        // source) instead of executing the EXISTS sub-query for every
        // whboxitems row.
        // Skips entirely if whSrc never got resolved (main query bombed
        // before line 75 — no source to join against anyway).
        if (segments is not null && !string.IsNullOrEmpty(whSrc))
        {
            try
            {
                // Build a "closed BoxNo set" CTE that fits both UAE and non-UAE
                // shapes via the SAME WhBoxItemsSource helper. The full
                // expression already references `w.BoxNo`, so we rebuild it as
                // a standalone SELECT here for the JOIN target.
                string closedSetSql = country.Equals("UAE", StringComparison.OrdinalIgnoreCase)
                    ? @"SELECT DISTINCT BoxNo FROM USA.dbo.upcboxhead WHERE Closed = 'Y'"
                    : $@"SELECT DISTINCT Trfno    AS BoxNo FROM [{dataNameForClosed}].dbo.Exclude_Transfers_Sim
                         UNION
                         SELECT DISTINCT palletno AS BoxNo FROM [{dataNameForClosed}].dbo.CloseR1Pallet";

                var (whClauseC,     whParamsC)     = BuildWarehouseClause(warehouses);
                var (palletClauseC, palletParamsC) = BuildPalletCategoryClause(palletCategories);

                using var cmdClosed = conn.CreateCommand();
                cmdClosed.CommandText = $@"
                    WITH ClosedBoxes AS (
                        {closedSetSql}
                    )
                    SELECT
                        ClosedLpmQty      = SUM(CASE WHEN w.LPMDt IS NOT NULL THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END),
                        ClosedLpmBoxes    = COUNT(DISTINCT CASE WHEN w.LPMDt IS NOT NULL THEN w.BoxNo END),
                        ClosedNonLpmQty   = SUM(CASE WHEN w.LPMDt IS NULL     THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END),
                        ClosedNonLpmBoxes = COUNT(DISTINCT CASE WHEN w.LPMDt IS NULL     THEN w.BoxNo END)
                      FROM {whSrc} w
                      INNER JOIN ClosedBoxes c ON c.BoxNo = w.BoxNo
                     WHERE 1 = 1
                       {palletClauseC}
                       {whClauseC};";
                foreach (var p in whParamsC)     cmdClosed.Parameters.Add(p);
                foreach (var p in palletParamsC) cmdClosed.Parameters.Add(p);
                cmdClosed.CommandTimeout = 60;
                using var rdrC = await cmdClosed.ExecuteReaderAsync(ct);
                if (await rdrC.ReadAsync(ct))
                {
                    var clQ = rdrC.IsDBNull(0) ? 0L : rdrC.GetInt64(0);
                    var clB = rdrC.IsDBNull(1) ? 0  : rdrC.GetInt32(1);
                    var cnQ = rdrC.IsDBNull(2) ? 0L : rdrC.GetInt64(2);
                    var cnB = rdrC.IsDBNull(3) ? 0  : rdrC.GetInt32(3);
                    segments = segments with
                    {
                        ClosedLpmBoxes    = clB,
                        ClosedLpmQty      = clQ,
                        ClosedNonLpmBoxes = cnB,
                        ClosedNonLpmQty   = cnQ,
                    };
                }
            }
            catch { /* leave closed-box counts at 0 — main table still renders */ }
        }

        // Sum the requested-segment counts for the readiness check / metric.
        // 1.14.11: now adds Non-Purchased buckets when the planner has
        // "Incl. Non-Purchased" checked, preserving the old behaviour
        // (where the WHERE-level filter used to do this for us).
        int  selBoxes = 0;
        long selQty   = 0;
        if (segments is not null)
        {
            if (sources.HasFlag(LpmSimSourceFlags.LpmBoxes))
            {
                if (seasons.HasFlag(LpmSimSeasonFlags.Summer))
                {
                    selBoxes += segments.LpmSummerBoxes; selQty += segments.LpmSummerQty;
                    if (includePurchasedBoxes) { selBoxes += segments.LpmSummerNpBoxes; selQty += segments.LpmSummerNpQty; }
                }
                if (seasons.HasFlag(LpmSimSeasonFlags.Winter))
                {
                    selBoxes += segments.LpmWinterBoxes; selQty += segments.LpmWinterQty;
                    if (includePurchasedBoxes) { selBoxes += segments.LpmWinterNpBoxes; selQty += segments.LpmWinterNpQty; }
                }
            }
            if (sources.HasFlag(LpmSimSourceFlags.NonLpmBoxes))
            {
                if (seasons.HasFlag(LpmSimSeasonFlags.Summer))
                {
                    selBoxes += segments.NonLpmSummerBoxes; selQty += segments.NonLpmSummerQty;
                    if (includePurchasedBoxes) { selBoxes += segments.NonLpmSummerNpBoxes; selQty += segments.NonLpmSummerNpQty; }
                }
                if (seasons.HasFlag(LpmSimSeasonFlags.Winter))
                {
                    selBoxes += segments.NonLpmWinterBoxes; selQty += segments.NonLpmWinterQty;
                    if (includePurchasedBoxes) { selBoxes += segments.NonLpmWinterNpBoxes; selQty += segments.NonLpmWinterNpQty; }
                }
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

        // Default: load the latest batch for (Country, RunDate). When the
        // page user has clicked into a non-latest batch (viewBatchNo set),
        // load THAT batch instead so the readiness fields surface its
        // metadata (Sources/Seasons/etc.) rather than the latest's.
        //
        // 1.14.67 — Skip "Running" batches that another user is currently
        // generating, unless the viewer is that same user (so the creator
        // can still see their own in-flight run). The 30-minute safety net
        // hides stale Running batches even from the creator — a crashed
        // mid-flight build shouldn't clutter the UI forever.
        var staleRunningCutoff = DateTime.Now.AddMinutes(-30);
        bool VisibleToViewer(LpmSimBatch b) =>
            b.Status != "Running"
            || (string.Equals(b.CreatedBy, currentUser, StringComparison.OrdinalIgnoreCase)
                && b.CreateTS >= staleRunningCutoff);

        LpmSimBatch? existing;
        if (viewBatchNo.HasValue)
        {
            existing = await db.LpmSimBatches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.LPMBatchNo == viewBatchNo.Value, ct);
            // Honour explicit batch picks — if the user typed a batch # in
            // "View another batch", show it regardless of Status (the
            // creator's own Running batch lookup should still resolve).
        }
        else
        {
            // Pick the latest visible batch — applies the Running filter
            // server-side so we don't drag rows we'll discard. Done by
            // pulling a small candidate set and filtering in-memory; the
            // (Country, RunDate) index keeps the candidate set tiny (most
            // periods have 1–5 batches).
            var candidates = await db.LpmSimBatches.AsNoTracking()
                .Where(b => b.Country == country && b.RunDate == runDate.Date)
                .OrderByDescending(b => b.LPMBatchNo)
                .Take(20)
                .ToListAsync(ct);
            existing = candidates.FirstOrDefault(VisibleToViewer);
        }

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
            // 1.14.107 — Surface the cap-formula short code so the Result
            // Preview header can show a "Cap: …" chip per batch.
            CurrentBatchCapMode              = existing?.CapMode,
            CurrentRunDate                   = existing?.RunDate,
            CurrentCreateTS                  = existing?.CreateTS,
            CurrentCreatedBy                 = existing?.CreatedBy,
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

    /// <summary>
    /// Truncate a long error message to a sensible length for the build
    /// banner / StageDetail column. Adds an ellipsis when truncated so
    /// readers know the full error is in the SQL log, not lost.
    /// </summary>
    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    /// <summary>
    /// 1.14.21 — humanise a millisecond count for the SKU Max Build
    /// stage detail. Mirrors the convention from
    /// <c>SkuMaxBuildJobManager.FormatDuration(TimeSpan)</c> but takes
    /// a long ms count (avoids constructing a TimeSpan in hot stage-
    /// string building). Examples:
    ///   • 850         → "850ms"
    ///   • 4 800       → "4.8s"
    ///   • 65 200      → "1m 5s"
    ///   • 319 000     → "5m 19s"
    /// </summary>
    private static string FormatMs(long ms)
    {
        if (ms < 1_000)   return $"{ms}ms";
        if (ms < 60_000)  return $"{ms / 1000.0:0.0}s";
        var totalSec = ms / 1000;
        var min = totalSec / 60;
        var sec = totalSec % 60;
        return $"{min}m {sec}s";
    }
    private static string FormatMs(int ms) => FormatMs((long)ms);

    private static string SeasonLabel(LpmSimSeasonFlags f)
    {
        if (f == LpmSimSeasonFlags.None || f == LpmSimSeasonFlags.Both) return "All seasons";
        var parts = new List<string>(2);
        if (f.HasFlag(LpmSimSeasonFlags.Summer)) parts.Add("Summer");
        if (f.HasFlag(LpmSimSeasonFlags.Winter)) parts.Add("Winter");
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Builds a SQL fragment + parameters for
    /// <c>AND w.Warehouse IN (@wh0, @wh1, ...)</c>. Returns
    /// <c>("", [])</c> when <paramref name="warehouses"/> is null/empty
    /// (= no filter, all warehouses). The order of <paramref name="warehouses"/>
    /// no longer carries semantic weight — priority order moved to
    /// <c>dbo.LPM_WarehousePriority</c>, which <c>ReadBoxesAsync</c> joins
    /// to for ORDER BY. This helper only feeds the WHERE filter.
    /// </summary>
    /// <summary>
    /// 1.14.78 — Build a parameterised IN-clause for a list of country codes,
    /// used by the SIM Generate path when widening filters to include child
    /// countries (e.g. UAE + OMAN). Returns the literal fragment like
    /// <c>"(@cn0, @cn1)"</c> + the bound parameters. Caller chooses the
    /// column to compare against — typical use is
    /// <c>"AND ds.SIMCountry IN {countryClause}"</c>.
    /// </summary>
    /// <param name="countries">Country list. First entry is the parent;
    /// subsequent entries are children.</param>
    /// <param name="prefix">Parameter prefix — default <c>"@cn"</c>. Override
    /// when a single query embeds two country lists to avoid name clash.</param>
    private static (string clause, List<SqlParameter> parameters) BuildCountryInClause(
        IReadOnlyList<string> countries, string prefix = "@cn")
    {
        if (countries is null || countries.Count == 0)
        {
            // Empty list → match nothing. Using a sentinel string keeps the
            // SQL syntactically valid; caller doesn't need to special-case.
            return ("('__NO_COUNTRY_IN_SCOPE__')", new List<SqlParameter>());
        }

        var paramNames = new List<string>(countries.Count);
        var parms      = new List<SqlParameter>(countries.Count);
        for (int i = 0; i < countries.Count; i++)
        {
            var name = $"{prefix}{i}";
            paramNames.Add(name);
            parms.Add(new SqlParameter(name, countries[i]));
        }
        return ($"({string.Join(", ", paramNames)})", parms);
    }

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

    /// <summary>
    /// Builds a parameterised <c>AND w.PalletCategory IN (@pc0, @pc1, ...)</c>
    /// fragment from the planner-supplied list. Empty/null → no filter (every
    /// category included). Default selection on the page is just "ELIGIBLE",
    /// which produces the legacy single-category clause.
    /// 1.14.17: switched from <c>pt.PalletCategory</c> to <c>w.PalletCategory</c>
    /// when the pallettype JOIN was removed from all SIM Generate queries.
    /// </summary>
    private static (string clause, List<SqlParameter> parameters) BuildPalletCategoryClause(IReadOnlyList<string>? categories)
    {
        if (categories is null || categories.Count == 0)
            return ("", new List<SqlParameter>());

        var distinct = categories
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return ("", new List<SqlParameter>());

        var paramNames = new List<string>(distinct.Count);
        var parms      = new List<SqlParameter>(distinct.Count);
        for (int i = 0; i < distinct.Count; i++)
        {
            var name = $"@pc{i}";
            paramNames.Add(name);
            parms.Add(new SqlParameter(name, distinct[i]));
        }
        // 1.14.17: clause now references w.PalletCategory (column on
        // whboxitems / WHBoxItemsExport) instead of pt.PalletCategory.
        // The pallettype master JOIN has been dropped from every SIM
        // Generate query — Season + PalletCategory come from whboxitems
        // directly. Same rationale as 1.14.9's SKU Max Build switch:
        //   • avoids silently dropping boxes whose PalletType has no
        //     master row (INNER JOIN side-effect),
        //   • avoids stale-master mismatches between w.* and pt.*,
        //   • removes a multi-million-row hash/merge JOIN per call.
        return ($"AND w.PalletCategory IN ({string.Join(", ", paramNames)})", parms);
    }

    /// <summary>
    /// Comma-separated label for the snapshot column on LPMSIM_Batch.
    /// Sorted alphabetically — order in the UI list no longer carries any
    /// meaning (priority moved to dbo.LPM_WarehousePriority).
    /// </summary>
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
    /// 1.14.90 — Concurrency gate for SIM Generate. Throws
    /// <see cref="SimBatchAlreadyRunningException"/> when another generate is
    /// already in flight for the same (Country, RunDate). "In flight" =
    /// LPM_SimBatch row with Status='Running' whose CreateTS is within the
    /// last 30 minutes (matches the stale-row safety window used by
    /// CheckAsync). Blocks BOTH cross-user races and same-user double-clicks.
    /// </summary>
    private async Task EnsureNoConcurrentGenerateAsync(LpmSimGenerateRequest req, CancellationToken ct)
    {
        var staleCutoff = DateTime.Now.AddMinutes(-30);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var inFlight = await db.LpmSimBatches.AsNoTracking()
            .Where(b => b.Country  == req.Country
                     && b.RunDate  == req.RunDate.Date
                     && b.Status   == "Running"
                     && b.CreateTS >= staleCutoff)
            .OrderByDescending(b => b.LPMBatchNo)
            .FirstOrDefaultAsync(ct);
        if (inFlight is null) return;

        // Human-friendly time stamp in GST so the message matches what the
        // user sees on the batch-pill list.
        var when = TimeFormatting.ToGccString(inFlight.CreateTS, "dd-MMM HH:mm");
        var conflictingUser = string.IsNullOrWhiteSpace(inFlight.CreatedBy)
                              ? "another user"
                              : inFlight.CreatedBy;
        var isSelf = !string.IsNullOrEmpty(inFlight.CreatedBy)
                     && string.Equals(inFlight.CreatedBy, req.User, StringComparison.OrdinalIgnoreCase);
        var msg = isSelf
            ? $"You already have a SIM Generate running for {req.Country} {req.RunDate:yyyy-MM-dd} "
              + $"(batch #{inFlight.LPMBatchNo}, started {when} GST). "
              + "Wait for that run to finish before starting another."
            : $"{conflictingUser} is currently running SIM Generate for {req.Country} {req.RunDate:yyyy-MM-dd} "
              + $"(batch #{inFlight.LPMBatchNo}, started {when} GST). "
              + "Please wait for that run to finish before starting a new one.";
        throw new SimBatchAlreadyRunningException(
            msg, inFlight.CreatedBy ?? "", inFlight.LPMBatchNo, inFlight.CreateTS);
    }

    /// <summary>
    /// Runs the two-phase allocation. Behaviour with existing batches for the
    /// same (Country, RunDate):
    ///   • Existing DRAFT      → replaced (its rows are deleted first).
    ///   • Existing APPROVED   → kept; a new Draft batch is created alongside.
    ///                           The planner can navigate between batches via
    ///                           the "View another batch" lookup.
    ///   • Existing Production Schedule on a Draft → blocks re-Generate
    ///     (planner must delete the schedule first so they're aware of the
    ///     impact). A schedule attached to an Approved batch does NOT block
    ///     a brand-new Draft — the Approved batch + its schedule are
    ///     untouched.
    ///   • 1.14.90 — An in-flight Running batch for the same (Country,
    ///     RunDate) blocks the new run (cross-user or same-user double-click).
    ///     See <see cref="EnsureNoConcurrentGenerateAsync"/>.
    /// </summary>
    public async Task<LpmSimGenerateResult> GenerateAsync(LpmSimGenerateRequest req, CancellationToken ct = default)
    {
        if (req.Sources == LpmSimSourceFlags.None)
            throw new InvalidOperationException("Pick at least one Box Source (LPM and/or Non-LPM).");
        if (req.Seasons == LpmSimSeasonFlags.None)
            throw new InvalidOperationException("Pick at least one Season (Summer and/or Winter).");

        // ---- 1.14.90 — Concurrency gate --------------------------------------
        // Block any concurrent SIM Generate for the same (Country, RunDate).
        // Uses the 1.14.67 Status="Running" marker on LPM_SimBatch + the same
        // 30-minute stale-row safety window that CheckAsync uses, so a
        // crashed mid-flight batch doesn't block new runs forever. The check
        // catches BOTH cross-user races (User B clicks Generate while User
        // A's run is still going) AND same-user double-clicks. Fails fast
        // before paying for the SKU Max gate / EOM read / allocation.
        await EnsureNoConcurrentGenerateAsync(req, ct);

        // ---- SKU Max existence gate ------------------------------------------
        // SKU Max is a separate user-driven build (decoupled from SIM Generate
        // for speed). SIM Generate refuses to run only when NO snapshot exists
        // for the run period — stale snapshots (built before today) are
        // allowed so planners can iterate without paying for a full rebuild
        // every day. The UI surfaces an amber "stale" warning so it's still
        // visible that the snapshot isn't fresh.
        var skuStatus = await GetLastSkuMaxBuildAsync(req.Country, req.RunYear, req.RunMonth, ct);
        if (skuStatus.LastBuildTS is null)
            throw new SkuMaxStaleException(
                $"No SKU Max snapshot exists for {req.Country} {req.RunYear:D4}-{req.RunMonth:D2}. Click 'Build SKU Max' before generating SIM.",
                skuStatus);

        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        long msReadBoxes = 0, msReadItemDiv = 0, msReadSoh = 0, msReadEom = 0;
        long msAllocate = 0, msPersistOutput = 0, msPersistTrace = 0, msPersistBalances = 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 1.14.78 — Country linkage Part 2. When the parent country has child
        // countries linked in dbo.LPM_CountryLink, the SIM allocator includes
        // the children's stores in the same batch. UAE → OMAN is the seeded
        // example: a UAE run pulls UAE + OMAN stores' EOM rows, SOH, and SKU
        // Max so the allocator can ship from UAE's warehouse to stores in
        // both countries.
        //
        // scopeCountries[0] == req.Country (the parent). children follow in
        // alphabetical order. CountryPriority on each loaded EomStore = the
        // index in this list, so the parent's stores always rank above any
        // child's within each allocator phase.
        var childCountries = await CountryLinkResolver.GetChildCountriesAsync(db, req.Country, ct);
        var scopeCountries = new List<string>(childCountries.Count + 1) { req.Country };
        scopeCountries.AddRange(childCountries);
        // Lookup: country name → priority index (0 for parent, 1+ for children).
        var countryPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < scopeCountries.Count; i++)
            countryPriority[scopeCountries[i]] = i;

        // AsNoTracking — we never modify this entity; we delete the row directly
        // via raw SQL below. Without AsNoTracking, EF's change tracker holds a
        // phantom reference to the now-deleted row and the next SaveChangesAsync
        // can throw DbUpdateException with confusing "an entity with the same
        // key" errors.
        // Latest batch for the period is the candidate to replace IF it's a
        // Draft. Approved batches stay so the planner can keep them as a
        // reference and run new Draft scenarios alongside.
        var latest = await db.LpmSimBatches.AsNoTracking()
            .Where(b => b.Country == req.Country && b.RunDate == req.RunDate.Date)
            .OrderByDescending(b => b.LPMBatchNo)
            .FirstOrDefaultAsync(ct);

        // Replace logic only fires for an existing DRAFT. An Approved latest
        // is left untouched — the new batch is created fresh and becomes the
        // new "latest". The planner can still see the Approved one via the
        // "View another batch" lookup on the result page.
        if (latest is { Status: "Draft" })
        {
            // Lock: refuse to replace a Draft that has a production schedule
            // attached — that's lost work the planner needs to consciously
            // discard. 1.14.22: the page can opt-in to cascade-delete the
            // schedule too via req.DeleteExistingSchedule (set only after
            // the page shows an explicit confirmation dialog naming the
            // schedule). When the flag is false (default), we still throw
            // so other callers, scripts, or future code paths can't lose
            // schedule work silently.
            var schedExists = await db.LpmSimProductionSchedules.AsNoTracking()
                .AnyAsync(s => s.LPMBatchNo == latest.LPMBatchNo, ct);
            if (schedExists)
            {
                if (!req.DeleteExistingSchedule)
                    throw new InvalidOperationException(
                        $"A production schedule exists for the existing Draft batch #{latest.LPMBatchNo}. Delete the schedule before re-generating SIM.");

                // Caller has opted-in to cascade-delete. Drop the schedule
                // first so the downstream batch-delete (below) doesn't trip
                // the same row.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $@"DELETE FROM dbo.LPMSIM_ProductionSchedule WHERE LPMBatchNo = {latest.LPMBatchNo};", ct);
            }

            db.Database.SetCommandTimeout(600);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_AllocTrace",       latest.LPMBatchNo, ct);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_Output",           latest.LPMBatchNo, ct);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_StoreItemBalance", latest.LPMBatchNo, ct);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_StoreDivBalance",  latest.LPMBatchNo, ct);
            await ChunkDeleteAsync(db, "dbo.LPMSIM_BoxBalance",       latest.LPMBatchNo, ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"DELETE FROM dbo.LPMSIM_Batch WHERE LPMBatchNo = {latest.LPMBatchNo};", ct);
            // Defensive: clear any tracker state that snuck in during the deletes,
            // so the next SaveChangesAsync only sees our new batch entity.
            db.ChangeTracker.Clear();
        }
        // If latest is Approved (or null) — fall through and create a new
        // Draft batch. The Approved batch (and its production schedule, if
        // any) survive intact.

        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        // ---------------- SKU Max ↔ Deactivation sync ----------------------
        // Why this runs every Generate, even though BuildSkuMax already
        // pre-zeros deactivated (Store, Div) pairs:
        //   • Planners edit LPM_StoreDivAccess between SIM runs without
        //     rebuilding SKU Max (the rebuild can take minutes; the change
        //     is one row).
        //   • If we don't re-apply IsActive=0 here, the snapshot still
        //     holds the OLD non-zero SKU Max for those pairs — which then
        //     surfaces in Item Details / SKU Max Detail reports as "this
        //     deactivated div has SKU Max > 0", which is misleading.
        // The UPDATE below is fast: it only touches rows where SKUMax > 0
        // AND a matching IsActive=0 row exists in LPM_StoreDivAccess. Most
        // (Store, Div) pairs aren't deactivated, so the join is narrow.
        // Idempotent — re-running it is a no-op if the snapshot is already
        // in sync (zero rows updated).
        long deactivationsSynced = 0;
        using (var sync = conn.CreateCommand())
        {
            // Two-step: first audit every (Store × Item) row that's about to
            // be zeroed (capturing PriorSKUMax > 0), then run the UPDATE.
            // The audit INSERT is idempotent — it skips rows that already
            // have a deactivation audit row for this period (avoids dupes
            // when the sync re-runs without any data change). The WHERE
            // m.SKUMax > 0 also ensures we only audit + zero rows that
            // ACTUALLY change — re-running is a no-op once everything is
            // already zero.
            // 1.14.32 — also populate DivisionName / Brand / GroupCode here
            // so the post-build sync audits look the same as the main-phase
            // audits. Lookups are inline because the sync's row count is
            // typically small (only Store-Div pairs that were deactivated
            // AFTER the Build, with SKUMax > 0 left over). OUTER APPLY
            // with TOP 1 keeps multiplicities in upcbarcodes / ItemMaster
            // from inflating the result.
            sync.CommandText = @"
                INSERT INTO dbo.LPM_SimItemSkuMaxExcluded
                       (Country, Year1, Month1, StoreID, ItemCode, Season, DivCode,
                        PriorSKUMax, SourceTable, Reason, MatchedKey, CreateTS,
                        DivisionName, Brand, GroupCode)
                SELECT m.Country, m.Year1, m.Month1, m.StoreID, m.ItemCode, m.Season, m.DivCode,
                       m.SKUMax,
                       'dbo.LPM_StoreDivAccess',
                       'Store-Div deactivated post-build (Pre-Generate sync)',
                       CONCAT('Div=', m.DivCode),
                       SYSDATETIME(),
                       (SELECT CAST(d.Division AS varchar(80))
                          FROM dbo.Division d WHERE d.DivCode = m.DivCode),
                       bl.Brand,
                       gl.GroupCode
                  FROM dbo.LPM_SimItemSkuMax m
                  INNER JOIN dbo.LPM_StoreDivAccess sda
                          ON sda.Country = m.Country
                         AND sda.StoreID = m.StoreID
                         AND sda.DivCode = m.DivCode
                  OUTER APPLY (
                      SELECT TOP 1 CAST(b.Vendor AS varchar(80)) AS Brand
                        FROM usa.dbo.upcbarcodes b
                       WHERE b.itemcode = m.ItemCode
                         AND b.Vendor IS NOT NULL
                         AND LTRIM(RTRIM(b.Vendor)) <> ''
                       ORDER BY b.Vendor
                  ) bl
                  OUTER APPLY (
                      SELECT TOP 1 CAST(im.GroupCode AS varchar(50)) AS GroupCode
                        FROM Hodata.dbo.ItemMaster im
                       WHERE im.ItemCode = m.ItemCode
                         AND im.GroupCode IS NOT NULL
                         AND LTRIM(RTRIM(im.GroupCode)) <> ''
                       ORDER BY im.GroupCode
                  ) gl
                 WHERE m.Country = @country
                   AND m.Year1   = @y
                   AND m.Month1  = @m
                   AND sda.IsActive = 0
                   AND m.SKUMax > 0
                   AND NOT EXISTS (
                       SELECT 1 FROM dbo.LPM_SimItemSkuMaxExcluded ex
                        WHERE ex.Country  = m.Country
                          AND ex.Year1    = m.Year1
                          AND ex.Month1   = m.Month1
                          AND ex.StoreID  = m.StoreID
                          AND ex.ItemCode = m.ItemCode
                          AND ex.SourceTable = 'dbo.LPM_StoreDivAccess'
                   );

                UPDATE m
                   SET m.SKUMax = 0
                  FROM dbo.LPM_SimItemSkuMax m
                  INNER JOIN dbo.LPM_StoreDivAccess sda
                          ON sda.Country = m.Country
                         AND sda.StoreID = m.StoreID
                         AND sda.DivCode = m.DivCode
                 WHERE m.Country = @country
                   AND m.Year1   = @y
                   AND m.Month1  = @m
                   AND sda.IsActive = 0
                   AND m.SKUMax > 0;
                SELECT @@ROWCOUNT;";
            sync.Parameters.Add(new SqlParameter("@country", req.Country));
            sync.Parameters.Add(new SqlParameter("@y",       req.RunYear));
            sync.Parameters.Add(new SqlParameter("@m",       req.RunMonth));
            sync.CommandTimeout = 120;
            try
            {
                var ret = await sync.ExecuteScalarAsync(ct);
                deactivationsSynced = ret is null || ret == DBNull.Value ? 0L : Convert.ToInt64(ret);
            }
            catch (SqlException)
            {
                // Non-fatal — if LPM_StoreDivAccess hasn't been migrated yet,
                // or the snapshot table is missing, just skip the sync. The
                // SKU Max gate above already guarantees the snapshot exists
                // for the period; this catch only covers infrastructure
                // edge cases.
                deactivationsSynced = 0;
            }
        }

        // ---------------- Inputs ----------------
        // 1.14.18 — BOX-level Season filter: revert to pt.Season (pallettype
        // master) per the new mixed-Season model. The SELECT in ReadBoxesAsync
        // re-adds the pallettype JOIN to make this work, while still
        // projecting per-item w.Season as the BoxItem.Season field for the
        // C# allocator's per-item drop (see "per-item Season filter" comment
        // above where lpmBoxes/nonLpmBoxes are filtered). This split is
        // intentional:
        //   • SQL filters BOXES by pt.Season (Summer pallet = pt.Season != 'W')
        //   • C# filters ITEMS by w.Season (each item's own seasonality)
        //
        // 1.14.17 had swapped both to w.Season; 1.14.18 restores the box-
        // level filter to pt.Season because the user wants Summer pallets
        // selected even if they contain a few Winter items (the C# pass
        // drops those mismatched items).
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

        // 1.14.70 — Resolve the country DataName once for the closed-box
        // exclusion (passed through to ReadBoxesAsync, used to build the
        // EXISTS clauses against [<DataName>]..Exclude_Transfers_Sim /
        // CloseR1Pallet for non-UAE, or USA..upcboxhead.Closed='Y' for UAE).
        // Null for UAE; ResolveDataNameAsync throws if a non-UAE country is
        // missing its DataName in DataSettings (matches ResolveAsync's
        // existing behaviour — fail fast rather than silently misroute).
        var simDataName  = await WhBoxItemsSource.ResolveDataNameAsync(conn, req.Country, ct);
        // Shared dictionary collected across both ReadBoxesAsync calls; keyed
        // by BoxNo with first-write-wins on the meta. Used by the diagnostic
        // builder below to emit CLOSED_BOX rows so the planner sees WHY a box
        // wasn't shipped.
        var closedBoxes  = new Dictionary<string, ClosedBoxMeta>(StringComparer.OrdinalIgnoreCase);

        if (req.Sources.HasFlag(LpmSimSourceFlags.LpmBoxes))
        {
            await ReadBoxesAsync(conn, true, req.Country, simDataName, req.RunYear, req.RunMonth, seasonClause, req.Warehouses, req.IncludePurchasedBoxes, req.PalletCategories, req.LpmMonths, lpmBoxes, closedBoxes, ct);
        }
        if (req.Sources.HasFlag(LpmSimSourceFlags.NonLpmBoxes))
        {
            // Non-LPM boxes have LPMDt IS NULL by definition — LpmMonths
            // doesn't apply, so we pass null here.
            await ReadBoxesAsync(conn, false, req.Country, simDataName, req.RunYear, req.RunMonth, seasonClause, req.Warehouses, req.IncludePurchasedBoxes, req.PalletCategories, null, nonLpmBoxes, closedBoxes, ct);
        }
        msReadBoxes = swStep.ElapsedMilliseconds; swStep.Restart();

        // 1.14.18 — per-item Season filter.
        //
        // Box selection at SQL time uses BOX-level pt.Season (pallettype master)
        // via the seasonClause: e.g. user picks Summer ⇒ only boxes whose
        // pallettype is Summer-tagged are returned. But a Summer-tagged pallet
        // can contain Winter items (rare, but real). The allocator must drop
        // those mismatched item rows so the user's season choice flows through
        // to the per-item allocation level.
        //
        // Implementation: filter the in-memory lists immediately after read.
        // Doing it here (rather than at every per-line site) guarantees one
        // consistent rule across all phases (P1a, P1b RR, P2a, P2b RR).
        // "Both Seasons" → no filter.
        //
        // 1.14.26 — snapshot pre-filter box-level summary so we can later
        // attribute "FILTERED_SEASON" in the LPMSIM_UnallocatedDiagnostic
        // table to boxes that were eligible by SQL but had every item
        // dropped by the per-item Season check. eligibleBoxes is the full
        // 32,930-ish set; postFilterBoxes is the subset that actually
        // reached the allocator. The diff is the FILTERED_SEASON set.
        var eligibleBoxes = new Dictionary<string, EligibleBoxSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in lpmBoxes)
            eligibleBoxes.TryAdd(b.BoxNo, new EligibleBoxSummary(b.PalletNo, b.LPMDt, "LPM", b.BoxQty));
        foreach (var b in nonLpmBoxes)
            eligibleBoxes.TryAdd(b.BoxNo, new EligibleBoxSummary(b.PalletNo, b.LPMDt, "Non-LPM", b.BoxQty));

        if (req.Seasons == LpmSimSeasonFlags.Summer)
        {
            lpmBoxes.RemoveAll(b => string.Equals(b.Season, "W", StringComparison.OrdinalIgnoreCase));
            nonLpmBoxes.RemoveAll(b => string.Equals(b.Season, "W", StringComparison.OrdinalIgnoreCase));
        }
        else if (req.Seasons == LpmSimSeasonFlags.Winter)
        {
            lpmBoxes.RemoveAll(b => !string.Equals(b.Season, "W", StringComparison.OrdinalIgnoreCase));
            nonLpmBoxes.RemoveAll(b => !string.Equals(b.Season, "W", StringComparison.OrdinalIgnoreCase));
        }

        // 1.14.26 — boxes that the per-item filter removed entirely (every
        // line was wrong-season). These never reach the allocator, so they
        // get FILTERED_SEASON in the diagnostic instead of trace-based
        // reasons.
        var postFilterBoxNos = new HashSet<string>(
            lpmBoxes.Select(b => b.BoxNo).Concat(nonLpmBoxes.Select(b => b.BoxNo)),
            StringComparer.OrdinalIgnoreCase);
        var filteredOutBoxes = new HashSet<string>(
            eligibleBoxes.Keys.Where(k => !postFilterBoxNos.Contains(k)),
            StringComparer.OrdinalIgnoreCase);

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
            // 1.14.78 — Widen to parent + linked child countries
            // (scopeCountries) so a UAE run also picks up OMAN store SOH.
            var (sohCountryClause, sohCountryParams) = BuildCountryInClause(scopeCountries);
            cmd.CommandText = $@"
                SELECT ls.StoreID, ls.Itemcode, ls.DivCode, ISNULL(ls.SOH, 0) AS SOH
                  FROM racks.dbo.LPM_LocStock ls
                  INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
                 WHERE ds.SIMCountry IN {sohCountryClause}
                   AND ls.StoreID  IS NOT NULL AND ls.StoreID  <> ''
                   AND ls.Itemcode IS NOT NULL AND ls.Itemcode <> '';";
            foreach (var p in sohCountryParams) cmd.Parameters.Add(p);
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
            // The cap column we read depends on req.WeekNo (1..4) — the
            // planner picks a week on SIM Generate and we drive the
            // allocator's weekly cap from that week's per-week column.
            // CASE on a parameter lets SQL Server still parameterise the
            // plan (no string concatenation into the FROM/SELECT). Fall
            // back to the legacy MerchNeedWeek column when WeekNo is
            // outside 1..4 (defensive — req validation should keep it in
            // range, but a NULL or 0 must not fail open).
            // 1.14.78 — Widen to scopeCountries (parent + linked children) so
            // a UAE batch loads EOM rows for both UAE and OMAN stores.
            // SIMCountry projected so the C# loop can compute CountryPriority
            // (parent's stores rank first within each phase).
            var (eomCountryClause, eomCountryParams) = BuildCountryInClause(scopeCountries);
            // 1.14.87 — Switched cap source from the per-week
            // MerchNeedWeekN columns to the monthly MerchNeedMonth column
            // (the per-week SQL CASE on @weekNo is gone; @weekNo parameter
            // dropped). Grade added so the 1b/2b RR can iterate stores in
            // grade-tier order. Both columns already exist on LPM_EOM_Output
            // (see Core.Entities.LpmEomOutput) and are populated by every
            // Approved EOM batch.
            cmd.CommandText = $@"
                SELECT eo.StoreID, eo.DivCode, ISNULL(eo.SKUMax, 0) AS SKUMax,
                       ISNULL(eo.TargetEOM, 0)      AS TargetEOM,
                       ISNULL(eo.PriorityRank, 0)   AS PriorityRank,
                       ISNULL(eo.WtAvgSoldQty, 0)   AS WtAvgSoldQty,
                       ISNULL(eo.VolumeGroup, '')   AS VolumeGroup,
                       ISNULL(eo.MerchNeedMonth, 0) AS MerchNeedMonth,   -- 1.14.87
                       ISNULL(eo.Grade, '')         AS Grade,             -- 1.14.87
                       ds.SIMCountry                AS SIMCountry         -- 1.14.78
                  FROM dbo.LPM_EOM_Output eo
                  INNER JOIN dbo.DataSettings ds
                          ON ds.StoreID = eo.StoreID
                         AND ds.SIMCountry IN {eomCountryClause}
                 WHERE eo.Country IN {eomCountryClause}
                   AND eo.Year1   = @y
                   AND eo.Month1  = @m;";
            foreach (var p in eomCountryParams) cmd.Parameters.Add(p);
            cmd.Parameters.Add(new SqlParameter("@y", req.RunYear));
            cmd.Parameters.Add(new SqlParameter("@m", req.RunMonth));
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                // 1.14.87 — column indexes shifted: MerchNeedMonth at 7, Grade at 8,
                // SIMCountry at 9 (was at 8).
                var simCountry = rdr.IsDBNull(9) ? "" : rdr.GetString(9);
                var cp = countryPriority.TryGetValue(simCountry, out var p) ? p : int.MaxValue;
                var s = new EomStore(
                    StoreID:        rdr.GetString(0),
                    DivCode:        rdr.GetInt32(1),
                    SKUMax:         rdr.GetInt32(2),
                    TargetEOM:      rdr.GetDecimal(3),
                    PriorityRank:   rdr.GetDecimal(4),
                    WtAvgSold:      rdr.GetDecimal(5),
                    VolumeGroup:    rdr.GetString(6),
                    // MerchNeedMonth is stored as int on LPM_EOM_Output;
                    // read as int and let the record convert to decimal.
                    MerchNeedMonth: rdr.GetInt32(7),
                    Grade:          rdr.GetString(8),
                    CountryPriority: cp);
                if (!eomByDiv.TryGetValue(s.DivCode, out var list))
                    eomByDiv[s.DivCode] = list = new();
                list.Add(s);
            }
        }
        foreach (var (div, list) in eomByDiv.ToList())
        {
            // Spec ordering: CountryPriority ASC (parent first), then
            // Priority Rank ASC, then Wt-Avg-Sold-Qty DESC.
            // 1.14.78 — CountryPriority is the new primary sort key. For
            // runs with no linked children, every store has CountryPriority=0
            // so the secondary keys (PriorityRank, WtAvgSold) drive the order
            // exactly as they did pre-1.14.78.
            eomByDiv[div] = list
                .OrderBy(s => s.CountryPriority)
                .ThenBy(s => s.PriorityRank)
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

        // ---- Load LPM_SimItemSkuMax (per Store × Item × Season) for THIS run period.
        // Build is a SEPARATE user-driven step now (decoupled from SIM Generate
        // for speed) — see GetLastSkuMaxBuildAsync / BuildSkuMaxAsync. The
        // staleness gate at the top of GenerateAsync ensures we never reach
        // here without a fresh snapshot.
        // 1.14.78 — Pass scopeCountries so SkuMax loads for parent + children.
        var skuMaxByStoreItem = await LoadItemSkuMaxAsync((SqlConnection)conn, req, scopeCountries, ct);

        // ---------------- Allocation state ----------------
        var batch = new LpmSimBatch
        {
            Country  = req.Country,
            RunYear  = req.RunYear,
            RunMonth = req.RunMonth,
            RunDate  = req.RunDate.Date,
            // 1.14.67 — Insert as "Running" so other users querying the
            // SIM Generate page don't see this batch as a CURRENT BATCH
            // while allocation is still in flight (a previous bug: User B
            // would see ajmal's mid-generation batch #62 listed as the
            // current Draft before any output rows had been committed).
            // After the entire allocation finishes successfully, the
            // transition to "Draft" happens explicitly just before this
            // method returns. CheckAsync / ListBatchesAsync /
            // GetBatchesForPeriodAsync filter Running batches not created
            // by the current user (and any Running batch older than 30 min
            // — stale safety net for the rare crashed-mid-flight case).
            Status   = "Running",
            CreateTS = DateTime.Now,
            CreatedBy = req.User,
            // Snapshot the filters that produced this batch — so the Result preview
            // never shows ambiguous results when the user later changes the checkboxes.
            // Marker:
            //   * = IncludePurchasedBoxes ("Include Non-Purchased Boxes" toggle)
            // PalletCategories aren't encoded into Sources (they're potentially
            // a long list); they live in their own snapshot column if/when added.
            Sources              = SourceLabel(req.Sources)
                                   + (req.IncludePurchasedBoxes ? "*" : ""),
            Seasons              = SeasonLabel(req.Seasons),
            OverrideUsabilityPct = req.OverrideUsabilityPct,
            Warehouses           = WarehousesLabel(req.Warehouses),
            // 1.14.98 — Persist PalletCategories + LpmMonths so the Gap by
            // UPC report (and any future replays) can reproduce the exact
            // box-eligibility filter the allocator used. Stored as
            // comma-separated strings; NULL when not set (= no filter).
            PalletCategories     = req.PalletCategories is { Count: > 0 }
                                   ? string.Join(",", req.PalletCategories)
                                   : null,
            LpmMonths            = req.LpmMonths is { Count: > 0 }
                                   ? string.Join(",", req.LpmMonths.Select(d => d.ToString("yyyy-MM")))
                                   : null,
            // Append " (SM)" when MerchNeed was bypassed so the result page
            // makes the run mode obvious without a second column. Short suffix
            // is intentional — the FillStrategy column is varchar(20) (pre
            // migration 035, varchar(40) post). "SM" is documented in the UI
            // tooltip + the in-page strategy banner expands it to "SKU Max only".
            FillStrategy         = req.IgnoreMerchNeed
                ? $"{req.FillStrategy} (SM)"
                : req.FillStrategy.ToString(),
            // Stamp the chosen run-week (1..4) onto the batch so downstream
            // (ADM, Reports, Production Schedule) can tell which week's
            // Merch Need cap drove the allocation.
            WeekNo               = req.WeekNo,
            // 1.14.107 — Persist the cap-formula mode so the Result Preview
            // can show planners which cap drove the allocation. Short code
            // matches migration 061: "EOM_BAL" / "MNM".
            CapMode              = req.CapMode == LpmSimCapMode.EomBalancePlusSkuMax
                                       ? "EOM_BAL"
                                       : "MNM",
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
                    skuMaxByStoreItem,
                    allocStoreItem, allocStoreDiv,
                    p1NormalSI, p1NormalSD,
                    boxAllocByPhase,
                    output, trace,
                    ref p1NormalLines, ref p1NormalQty,
                    ref itemsWithoutDiv, ref boxesSkipped);
                totalQty += line.Qty - lineRemain[idx];
                phase1Items.Add(line.ItemCode);
            }

            // 1b RR (OVERRIDE) — fires for boxes whose Phase-1a usability%
            // crossed the user-defined "Box %" threshold. RR fills the box
            // toward 100% by BYPASSING both caps (SKU Max + Merch Need Week):
            //   • SKU Max  = SKUMax − SOH − cumItem  (cumulative across phases)
            //   • Merch Need (Week)  = MerchNeedWeek − cumDiv (weekly cap)
            // Each row is tagged IsOverride = true so reports surface the
            // override qty separately. Boxes that didn't reach the threshold
            // skip RR — their leftover stays unallocated for the next cycle.
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
                        skuMaxByStoreItem,
                        allocStoreItem, allocStoreDiv,
                        p1RrSI, p1RrSD,
                        boxAllocByPhase,
                        output, trace,
                        bypassAllCaps: true);
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
                    skuMaxByStoreItem,
                    allocStoreItem, allocStoreDiv,
                    p2NormalSI, p2NormalSD,
                    boxAllocByPhase,
                    output, trace,
                    ref p2NormalLines, ref p2NormalQty,
                    ref itemsWithoutDiv, ref boxesSkipped);
                totalQty += line.Qty - lineRemain[idx];
            }

            // 2b RR (OVERRIDE) — same shape as Phase 1b: gated by post-Phase-2a
            // usability%; bypasses BOTH SKU Max and Merch Need (Week) caps
            // when usability ≥ Box%. Tagged IsOverride = true.
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
                        skuMaxByStoreItem,
                        allocStoreItem, allocStoreDiv,
                        p2RrSI, p2RrSD,
                        boxAllocByPhase,
                        output, trace,
                        bypassAllCaps: true);
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

        // 1.14.67 — Wrap the entire persist phase in a SQL transaction so
        // every bulk insert (Output, Trace, StoreItemBalance, StoreDivBalance,
        // BoxBalance, UnallocatedDiagnostic) and the final Status="Draft" flip
        // commit atomically. If anything in this scope throws (timeout,
        // deadlock, connection drop), the rollback erases ALL per-batch rows
        // so dbo.LPMSIM_Output / dbo.LPMSIM_AllocTrace / dbo.LPMSIM_*Balance
        // never sit with phantom rows from a failed Generate (the bug seen
        // on UAE 2026-05-20 batch #62 — 164K Output rows present even
        // though Generate timed out).
        //
        // The INITIAL batch row (inserted at line 1035 with Status="Running")
        // is NOT inside this transaction — it was committed earlier so
        // batch.LPMBatchNo is real and stable. The batch row stays in
        // "Running" forever if Generate throws (hidden from other users by
        // CheckAsync / ListBatchesAsync / GetBatchesForPeriodAsync filters
        // and the 30-minute TTL safety net).
        //
        // Lock scope: with TableLock removed from the Output/Trace bulk
        // inserts (see BulkInsertOutputAsync), the transaction only holds
        // row-level X locks on rows tagged with the new LPMBatchNo. These
        // don't block readers of other batches.
        await using var persistTx = await db.Database.BeginTransactionAsync(ct);
        var sqlTx = (SqlTransaction)persistTx.GetDbTransaction();

        try
        {
            // Update batch header first (count totals).
            batch.BoxesProcessed = distinctBoxes.Count;
            batch.LinesGenerated = output.Count;
            batch.TotalQty       = totalQty;
            await db.SaveChangesAsync(ct);

        // 1.14.18 — Stamp UsabilityPct on every output row before bulk insert.
        // Per-box metric: SUM(allocated Qty across all stores for this box)
        // / BoxQty × 100. Same value repeated on every row of the same box
        // (the box is shipped as a unit, so usability is box-level). Done
        // in-process rather than a post-insert UPDATE so the bulk insert
        // writes the final value in one shot.
        if (output.Count > 0)
        {
            var allocByBox = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in output)
                allocByBox[o.BoxNo] = allocByBox.GetValueOrDefault(o.BoxNo, 0L) + o.Qty;
            foreach (var o in output)
            {
                if (o.BoxQty is > 0 && allocByBox.TryGetValue(o.BoxNo, out var alloc))
                {
                    o.UsabilityPct = Math.Round((decimal)alloc * 100m / o.BoxQty.Value, 2);
                }
            }
        }

        // Bulk-copy everything heavy. EF AddRange + SaveChanges on millions of
        // rows would take many minutes / hours, so we use SqlBulkCopy.
        // 1.14.67 — All 5 bulk inserts now run inside `sqlTx`, so they
        // commit/rollback together with the rest of the persist phase.
        if (output.Count > 0)
            await BulkInsertOutputAsync((SqlConnection)conn, output, ct, sqlTx);
        msPersistOutput = swStep.ElapsedMilliseconds; swStep.Restart();

        if (trace.Count > 0)
            await BulkInsertTraceAsync((SqlConnection)conn, trace, ct, sqlTx);
        msPersistTrace = swStep.ElapsedMilliseconds; swStep.Restart();

        await BulkInsertStoreItemBalancesAsync((SqlConnection)conn, batch.LPMBatchNo, batch.CreateTS,
            sohMap, skuMaxByStoreItem, itemDiv,
            p1NormalSI, p1RrSI, p2NormalSI, p2RrSI, ct, sqlTx);

        await BulkInsertStoreDivBalancesAsync((SqlConnection)conn, batch.LPMBatchNo, batch.CreateTS,
            sohByStoreDiv, eomByDiv,
            p1NormalSD, p1RrSD, p2NormalSD, p2RrSD, ct, sqlTx);

        await BulkInsertBoxBalancesAsync((SqlConnection)conn, batch.LPMBatchNo, batch.CreateTS,
            lpmBoxes, nonLpmBoxes, boxAllocByPhase, ct, sqlTx);
        msPersistBalances = swStep.ElapsedMilliseconds;

        // 1.14.26 — Per-eligible-box allocation gap diagnostic. One row in
        // dbo.LPMSIM_UnallocatedDiagnostic for every box where the SQL
        // filter said "eligible" but the allocator left RemainingQty > 0.
        // Forward-fill only: pre-1.14.26 batches don't get this; new
        // batches always do. Wrapped in try/catch so a missing table
        // (mig 046 not applied yet) is non-fatal — the build itself
        // is already committed by this point.
        //
        // 1.14.28 — Precompute the "every item × every eligible store is
        // SKUMax=0" check so the diagnostic can label affected boxes as
        // EXCLUDED_BY_RULE instead of generic CAP. Two-step calculation
        // for speed:
        //   1. itemFullyExcluded: per ItemCode, is the item barred from
        //      every store eligible for its division? Pre-computed once
        //      across all distinct items.
        //   2. excludedByRuleBoxes: per BoxNo (that reached allocator),
        //      are ALL its items fully excluded? If yes, the box's gap
        //      is purely a Rule-1-through-7 exclusion, not a cap.
        var itemFullyExcluded = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in itemDiv)
        {
            var itemCode = kv.Key;
            var div      = kv.Value;
            if (!eomByDiv.TryGetValue(div, out var divStores) || divStores.Count == 0)
            {
                // No eligible stores for this division ⇒ everything is
                // blocked, but the reason is SKIP_NO_EOM not exclusion.
                // Mark NOT-fully-excluded so the diagnostic falls through
                // to the SKIP_NO_EOM branch.
                itemFullyExcluded[itemCode] = false;
                continue;
            }
            bool anyAllowed = false;
            foreach (var s in divStores)
            {
                if (skuMaxByStoreItem.GetValueOrDefault((s.StoreID, itemCode), 0) > 0)
                {
                    anyAllowed = true;
                    break;
                }
            }
            itemFullyExcluded[itemCode] = !anyAllowed;
        }
        // Per-box check: a box is EXCLUDED_BY_RULE if it reached the
        // allocator AND every item it contains is itemFullyExcluded.
        // Iterates lpmBoxes+nonLpmBoxes (the post-filter sets — boxes
        // that actually entered the allocator), deduped by BoxNo.
        var excludedByRuleBoxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boxItemsByBox = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in lpmBoxes.Concat(nonLpmBoxes))
        {
            if (!boxItemsByBox.TryGetValue(b.BoxNo, out var itemsInBox))
            {
                itemsInBox = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                boxItemsByBox[b.BoxNo] = itemsInBox;
            }
            itemsInBox.Add(b.ItemCode);
        }
        foreach (var (boxNo, itemsInBox) in boxItemsByBox)
        {
            if (itemsInBox.Count == 0) continue;
            bool allExcluded = true;
            foreach (var item in itemsInBox)
            {
                if (!itemFullyExcluded.TryGetValue(item, out var fullyExcluded) || !fullyExcluded)
                {
                    allExcluded = false;
                    break;
                }
            }
            if (allExcluded) excludedByRuleBoxes.Add(boxNo);
        }

        try
        {
            await BuildAndInsertUnallocatedDiagnosticAsync(
                (SqlConnection)conn,
                batch.LPMBatchNo,
                batch.CreateTS,
                req.Country,       // 1.14.44 — needed for the boxesWithViableLines
                req.RunYear,       // SkuMax lookup that reclassifies UNKNOWN → CAP
                req.RunMonth,      // when the box's items have SkuMax rows for this period
                eligibleBoxes,
                filteredOutBoxes,
                excludedByRuleBoxes,
                closedBoxes,       // 1.14.70 — closed-box meta for CLOSED_BOX gap rows
                output,
                trace,
                ct,
                sqlTx);            // 1.14.67 — enrol diagnostic insert in outer persist tx
        }
        catch (SqlException ex) when (ex.Number == 208 /* invalid object — table missing */)
        {
            // Migration 046 hasn't been applied yet. Build succeeded;
            // diagnostic just won't be available for this batch.
        }
        catch (Exception)
        {
            // Diagnostic write is best-effort — never fail the build over it.
        }

        // 1.14.44 — Refresh LPM_SimItemSkuMax.SOH with the live SOH that the
        // allocator actually used. The snapshot's SOH was frozen at Build
        // SKU Max time. If stock moved (new arrivals, ETL refresh) between
        // the build and this SIM run, the snapshot's SOH (and the computed
        // ToFillQty column derived from it) would be stale — planners
        // reading the SkuMax table after a SIM run would see ToFillQty
        // values that don't match what the allocator just decided.
        //
        // 1.14.46 — Scope the refresh to ITEMS THIS BATCH TOUCHED, not the
        // whole SkuMax snapshot. The 1.14.44 implementation updated all
        // ~13.8M LPM_SimItemSkuMax rows for the period, regardless of how
        // small the SIM run was (e.g. a 4K-box LPM batch). With ToFillQty
        // being a PERSISTED computed column, every UPDATE triggers a
        // recompute + persisted-write — added 2-5 minutes to every SIM
        // Generate.
        //
        // The new scope is "items in any eligible box from this batch" —
        // union of items in LPMSIM_Output (successfully allocated) and
        // items in boxes that appear in LPMSIM_UnallocatedDiagnostic
        // (eligible but not fully allocated). This covers every item the
        // planner might investigate after the run. Items NOT in this
        // batch's box set keep their last-built snapshot SOH, which is
        // correct because the allocator didn't touch them this run.
        //
        // Expected speedup: 13.8M-row UPDATE → typically 100K-2M-row
        // UPDATE depending on batch size. SIM Generate's tail drops from
        // 2-5 min back to 5-20 seconds.
        //
        // Best-effort: wrapped in try/catch so any failure here doesn't
        // affect the build. Values just stay stale until the next Build
        // SKU Max or the next SIM run.
        try
        {
            // 1.14.61 — Country-aware whboxitems source for the unallocated-box
            // half of the BatchItems union. The allocated half (LPMSIM_Output)
            // is country-agnostic since it's just rows for this batch.
            var refreshWhSrc = await WhBoxItemsSource.ResolveAsync((SqlConnection)conn, req.Country, ct);
            using var refresh = conn.CreateCommand();
            refresh.Transaction = sqlTx;   // 1.14.67 — enrol refresh UPDATE in outer persist tx
            refresh.CommandText = $@"
                WITH BatchItems AS (
                    -- Items the allocator successfully placed
                    SELECT DISTINCT Itemcode
                      FROM dbo.LPMSIM_Output
                     WHERE LPMBatchNo = @batchNo
                    UNION
                    -- Items in eligible-but-unallocated boxes (so the planner
                    -- investigating an UNKNOWN/CAP diagnostic also gets fresh
                    -- ToFillQty for those items, not just allocated ones).
                    SELECT DISTINCT w.itemcode
                      FROM {refreshWhSrc} w
                      INNER JOIN dbo.LPMSIM_UnallocatedDiagnostic ud
                              ON ud.BoxNo = w.BoxNo
                     WHERE ud.LPMBatchNo = @batchNo
                )
                UPDATE sm
                   SET sm.SOH = CAST(ISNULL(s.SohLive, 0) AS int)
                  FROM dbo.LPM_SimItemSkuMax sm
                  INNER JOIN BatchItems bi ON bi.Itemcode = sm.ItemCode
                  LEFT JOIN (
                      SELECT ls.StoreID, ls.Itemcode,
                             SohLive = SUM(CAST(ISNULL(ls.SOH, 0) AS bigint))
                        FROM racks.dbo.LPM_LocStock ls
                        INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
                       WHERE ds.SIMCountry = @country
                         AND ls.StoreID  IS NOT NULL AND ls.StoreID  <> ''
                         AND ls.Itemcode IS NOT NULL AND ls.Itemcode <> ''
                       GROUP BY ls.StoreID, ls.Itemcode
                  ) s ON s.StoreID = sm.StoreID AND s.Itemcode = sm.ItemCode
                 WHERE sm.Country = @country
                   AND sm.Year1   = @y
                   AND sm.Month1  = @m;";
            refresh.Parameters.Add(new SqlParameter("@country", req.Country));
            refresh.Parameters.Add(new SqlParameter("@y", req.RunYear));
            refresh.Parameters.Add(new SqlParameter("@m", req.RunMonth));
            refresh.Parameters.Add(new SqlParameter("@batchNo", batch.LPMBatchNo));
            refresh.CommandTimeout = 300;
            await refresh.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Best-effort — build is committed; refresh is just a UX nicety.
        }

        // 1.14.67 — All allocation writes are now staged inside `persistTx`.
        // Flip the batch from "Running" → "Draft" and then COMMIT — the
        // status change + every bulk insert above become visible to other
        // sessions atomically at the CommitAsync call. If the method threw
        // before reaching this point, the OUTER catch (below) rolls back
        // every persist write AND the batch row stays in "Running" state
        // (the initial INSERT at line 1035 happened OUTSIDE this tx). The
        // readiness/list filters hide the orphan "Running" row from the
        // creator's co-workers, and the 30-minute TTL safety-net hides it
        // from everyone (including the creator) once stale, so it doesn't
        // clutter the UI forever.
        batch.Status = "Draft";
        db.LpmSimBatches.Update(batch);
        await db.SaveChangesAsync(ct);

            // 1.14.67 — Commit. Output / Trace / Balance / Diagnostic /
            // Status flip all become visible to other connections here in
            // one atomic step.
            await persistTx.CommitAsync(ct);
        }
        catch
        {
            // 1.14.67 — Any persist-phase failure (timeout, deadlock,
            // network blip) rolls back every bulk-inserted row for this
            // batch. After this, dbo.LPMSIM_Output / LPMSIM_AllocTrace /
            // LPMSIM_StoreItemBalance / LPMSIM_StoreDivBalance /
            // LPMSIM_BoxBalance / LPMSIM_UnallocatedDiagnostic contain
            // ZERO rows for this batch — clean slate for a re-Generate.
            // The initial batch row (Status="Running", inserted outside
            // this tx) stays; it's hidden by the per-user filter and gets
            // the 30-min TTL safety net.
            try { await persistTx.RollbackAsync(ct); }
            catch { /* tx already doomed or disposed by SQL Server */ }
            throw;
        }

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
            DeactivationsSynced  = deactivationsSynced,
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
        Dictionary<(string Store, string Item), int> skuMaxByStoreItem,
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

        // ── Phase F perf fix: cache per-line state in flat arrays ──
        // The previous implementation did 5 dictionary lookups per store per
        // cycle (soh, divSoh, cumItem, cumDiv, skuMax). For a 100-unit box
        // across 50 stores that's 25,000 lookups per line. We:
        //   1) Read each per-(store,item) static value ONCE into an int[].
        //   2) Track in-line deltas locally — only flush to the running-total
        //      dictionaries once at end-of-line (before trace generation).
        //   3) Skip stores that fail the cap check up-front; remove them
        //      from the candidate pool the moment they hit a cap mid-line.
        // Gives ~5–10× speedup on the allocator inner loop without changing
        // any outward behaviour.
        int n = stores.Count;
        var sohArr      = new int[n];
        var divSohArr   = new int[n];
        var skuMaxArr   = new int[n];
        var startCumIt  = new int[n];   // cumItem at line start (per store/item)
        var startCumDv  = new int[n];   // cumDiv  at line start (per store/div)
        var skuBalArr   = new int[n];   // running SKU headroom (decreases as we allocate)
        var divBalArr   = new decimal[n]; // running div headroom
        var deltaItem   = new int[n];   // delta to allocStoreItem (per chosen store within this line)
        var deltaDiv    = new int[n];   // delta to allocStoreDiv  (per chosen store within this line)
        var dead        = new bool[n];  // capped — exclude from candidate scan

        // IgnoreMerchNeed mode: SKU Max is the only cap. The MerchNeed cap is
        // bypassed (so divBalArr is never decremented and never blocks placement).
        // For EqualFillRate, the "fill rate" denominator switches to SkuMax so
        // stores with the most SKU headroom are still preferred — gives a
        // meaningful sort order even without MerchNeedWeek.
        var ignoreMerch = req.IgnoreMerchNeed;
        for (int i = 0; i < n; i++)
        {
            var s = stores[i];
            var soh     = sohMap.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
            var divSoh  = sohByStoreDiv.GetValueOrDefault((s.StoreID, divCode), 0);
            var cumItem = allocStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
            var cumDiv  = allocStoreDiv.GetValueOrDefault((s.StoreID, divCode), 0);
            var skuMax  = skuMaxByStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
            sohArr[i] = soh; divSohArr[i] = divSoh; skuMaxArr[i] = skuMax;
            startCumIt[i] = cumItem; startCumDv[i] = cumDiv;
            // 1.14.31 — negative SOH / divSoh (oversold or data anomaly in
            // LPM_LocStock) was inflating both cap headrooms:
            //   sb     = SKUMax    − (−21) − cumItem  → SKUMax + 21
            //   tgt    = TargetEOM − (−N)  − cumDiv   → TargetEOM + N
            // Clamp both at 0 for the cap math — negative stock should
            // never amplify a ceiling. Trace columns below (sohArr,
            // divSohArr) keep the RAW values so the planner can spot the
            // underlying negative-stock anomaly in the Allocation Trace tab.
            var effSoh    = Math.Max(0, soh);
            var sb = skuMax - effSoh - cumItem;
            // 1.14.107 — Cap formula is mode-driven (req.CapMode):
            //   • EomBalancePlusSkuMax (default since 1.14.107):
            //       db = TargetEOM − DivSOH − cumDiv
            //   • MerchNeedMonthPlusSkuMax (legacy, opt-in):
            //       db = MerchNeedMonth − cumDiv   (matches 1.14.87 default)
            //
            // EOM Balance mode does NOT clamp divSoh at 0 — chosen by the
            // planner in 1.14.107. A negative DivSOH (store currently in
            // arrears: oversold or data anomaly in LPM_LocStock) INFLATES
            // the cap by the deficit so the make-whole shipment can reach
            // TargetEOM. This is opposite of how SOH/SkuMax is clamped
            // (1.14.31) — the planner's reasoning is that the deficit
            // represents real headroom in the store's plan.
            decimal db = req.CapMode == LpmSimCapMode.EomBalancePlusSkuMax
                ? s.TargetEOM - divSoh - cumDiv
                : s.MerchNeedMonth - cumDiv;
            skuBalArr[i] = sb; divBalArr[i] = db;
            if (sb <= 0)         { dead[i] = true; if (req.VerboseTrace) skipDecision.TryAdd(s.StoreID, "SKIP_SKUMAX"); continue; }
            if (!ignoreMerch && db <= 0)
            {
                dead[i] = true;
                if (req.VerboseTrace)
                    skipDecision.TryAdd(s.StoreID,
                        req.CapMode == LpmSimCapMode.EomBalancePlusSkuMax ? "SKIP_EOM_BAL" : "SKIP_MNM");
                continue;
            }
            // EqualFillRate normally requires the cap headroom denominator > 0
            // to compute fillRate. In IgnoreMerchNeed mode the denominator
            // becomes SkuMax instead, so stores with cap = 0 are still candidates.
            // 1.14.107 — denominator picks TargetEOM − DivSOH (EOM mode) or
            // MerchNeedMonth (MNM mode).
            if (req.FillStrategy == LpmSimFillStrategy.EqualFillRate && !ignoreMerch)
            {
                decimal capCeiling = req.CapMode == LpmSimCapMode.EomBalancePlusSkuMax
                    ? s.TargetEOM - divSoh
                    : s.MerchNeedMonth;
                if (capCeiling <= 0m) dead[i] = true;
            }
            if (req.FillStrategy == LpmSimFillStrategy.EqualFillRate
                && ignoreMerch && skuMax <= 0)
            { dead[i] = true; }
        }

        if (req.FillStrategy == LpmSimFillStrategy.EqualFillRate)
        {
            // EqualFillRate strategy: each unit goes to the eligible store with
            // the LOWEST current FillRate% (= cumDiv / MerchNeedWeek). Tie-break
            // is PriorityRank ASC because `stores` is already in that order —
            // first match wins on equal FillRate. This naturally pulls every
            // store toward the same Division-level fill share rather than the
            // same per-store qty.
            //
            // Cap source changed in Phase C₂ from EomBalance (TargetEOM − DivSOH)
            // to Merch Need (Week) ((TargetEOM − DivSOH + TargetSales) / 4) so
            // SIM is now driven by a weekly open-to-receive instead of a
            // monthly EOM headroom.
            while (remaining > 0)
            {
                int bestIdx = -1;
                decimal bestFill = decimal.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (dead[i]) continue;
                    var s   = stores[i];
                    decimal fr;
                    if (ignoreMerch)
                    {
                        // SKU-based fill rate: cumItem / SkuMax. Stores with
                        // less-consumed SKU headroom rise to the top.
                        var consumedSku = startCumIt[i] + deltaItem[i];
                        fr = skuMaxArr[i] > 0 ? (decimal)consumedSku / skuMaxArr[i] : 1m;
                    }
                    else
                    {
                        // current cumDiv = startCumDv[i] + deltaDiv[i]
                        var cur = startCumDv[i] + deltaDiv[i];
                        // 1.14.107 — Denominator matches the active cap formula.
                        // EOM mode: TargetEOM − DivSOH (cap ceiling). MNM mode:
                        // MerchNeedMonth (legacy 1.14.87 behaviour). Guard against
                        // div-by-zero: a store with capCeiling ≤ 0 is already in
                        // `dead[]` (entry gate above) so we wouldn't reach here,
                        // but the safety check costs nothing.
                        decimal capDenom = req.CapMode == LpmSimCapMode.EomBalancePlusSkuMax
                            ? s.TargetEOM - divSohArr[i]
                            : s.MerchNeedMonth;
                        fr = capDenom > 0m ? (decimal)cur / capDenom : 1m;
                    }
                    if (fr < bestFill) { bestFill = fr; bestIdx = i; }
                }
                if (bestIdx < 0) break;          // no eligible store — line stops
                var bs = stores[bestIdx];
                buckets[bs.StoreID] = buckets.GetValueOrDefault(bs.StoreID, 0) + 1;
                deltaItem[bestIdx]++;
                deltaDiv [bestIdx]++;
                skuBalArr[bestIdx]--;
                if (!ignoreMerch) divBalArr[bestIdx]--;
                remaining--;
                skipDecision.Remove(bs.StoreID);
                // SKU cap always blocks. Div cap only blocks when MerchNeed is honoured.
                if (skuBalArr[bestIdx] <= 0
                    || (!ignoreMerch && divBalArr[bestIdx] <= 0))
                    dead[bestIdx] = true;
            }
        }
        else
        {
            // EqualPerStore strategy (default): 1 unit per store per cycle in
            // PriorityRank order (stores list is already pre-sorted). Hard
            // guard against runaway loop: at most one cycle per remaining unit.
            var maxCycles = remaining + 1;
            int cycle = 0;
            while (remaining > 0 && cycle < maxCycles)
            {
                bool tookAnyThisCycle = false;
                for (int i = 0; i < n; i++)
                {
                    if (remaining <= 0) break;
                    if (dead[i]) continue;
                    var s = stores[i];
                    buckets[s.StoreID] = buckets.GetValueOrDefault(s.StoreID, 0) + 1;
                    deltaItem[i]++;
                    deltaDiv [i]++;
                    skuBalArr[i]--;
                    if (!ignoreMerch) divBalArr[i]--;
                    remaining--;
                    tookAnyThisCycle = true;
                    skipDecision.Remove(s.StoreID);
                    if (skuBalArr[i] <= 0
                        || (!ignoreMerch && divBalArr[i] <= 0))
                        dead[i] = true;
                }
                if (!tookAnyThisCycle) break;
                cycle++;
            }
        }

        // Flush per-line deltas to the running-total dictionaries. Trace rows
        // below depend on these reflecting the post-line state.
        for (int i = 0; i < n; i++)
        {
            if (deltaItem[i] == 0 && deltaDiv[i] == 0) continue;
            var s = stores[i];
            if (deltaItem[i] != 0)
            {
                var k = (s.StoreID, line.ItemCode);
                allocStoreItem[k] = startCumIt[i] + deltaItem[i];
                phaseStoreItem[k] = phaseStoreItem.GetValueOrDefault(k, 0) + deltaItem[i];
            }
            if (deltaDiv[i] != 0)
            {
                var k = (s.StoreID, divCode);
                allocStoreDiv[k] = startCumDv[i] + deltaDiv[i];
                phaseStoreDiv[k] = phaseStoreDiv.GetValueOrDefault(k, 0) + deltaDiv[i];
            }
        }

        // Emit one ALLOC row per (store) carrying the bucketed qty.
        // Trace fields read directly from the cached arrays (built at line
        // start + per-line deltas) — no extra dictionary work.
        for (int i = 0; i < n; i++)
        {
            var s = stores[i];
            if (!buckets.TryGetValue(s.StoreID, out var qty) || qty <= 0) continue;

            output.Add(new LpmSimOutput
            {
                LPMBatchNo   = batch.LPMBatchNo,
                BoxNo        = line.BoxNo,
                PalletNo     = line.PalletNo,        // 1.14.12 — migration 041
                LPMDt        = line.LPMDt,
                Itemcode     = line.ItemCode,
                Qty          = qty,
                StoreID      = s.StoreID,
                CreateTS     = batch.CreateTS,
                CreatedBy    = req.User,
                Phase        = phaseTag,
                IsRoundRobin = false, // still NORMAL phase — RR-style is just the fill mechanism
                // 1.14.18 — migration 044 columns. UsabilityPct is stamped
                // post-allocation in StampUsabilityPct() (a pre-bulk-insert
                // pass) once the total allocated qty per box is known.
                // SKUMax is the value the allocator used for this
                // (Store, Item) — keyed by (Store, Item) only after mig 045.
                Season       = line.Season,
                BoxQty       = line.BoxQty,
                BoxItemQty   = line.Qty,
                DivCode      = divCode,
                SKUMax       = skuMaxArr[i],
            });
            boxAllocByPhase[(line.BoxNo, phaseTag)] = boxAllocByPhase.GetValueOrDefault((line.BoxNo, phaseTag), 0L) + qty;

            // Trace row reflects post-loop cumulative state. Pull values
            // straight from the cached arrays — these reflect the same
            // numbers we just flushed to the running-total dictionaries.
            var cumItem    = startCumIt[i] + deltaItem[i];
            var cumDiv     = startCumDv[i] + deltaDiv[i];
            var skuBalance = skuBalArr[i];
            var divBalance = divBalArr[i];
            var t = NewTraceForCandidate(batch, phaseTag, line, divCode, s, skuMaxArr[i],
                sohArr[i], divSohArr[i], skuBalance, cumItem, cumDiv, divBalance);
            t.Take     = qty;
            t.Decision = "ALLOC";
            trace.Add(t);

            phaseLineCount++;
            phaseQtyCount += qty;
        }

        // Emit at most one SKIP_* trace per store that was capped throughout.
        // Walk by index so we can pull cached arrays without re-doing dict reads.
        if (req.VerboseTrace && skipDecision.Count > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var s = stores[i];
                if (!skipDecision.TryGetValue(s.StoreID, out var decision)) continue;
                var cumItem    = startCumIt[i] + deltaItem[i];
                var cumDiv     = startCumDv[i] + deltaDiv[i];
                var skuBalance = skuMaxArr[i] - sohArr[i] - cumItem;
                var divBalance = (decimal)s.TargetEOM - divSohArr[i] - cumDiv;
                var t = NewTraceForCandidate(batch, phaseTag, line, divCode, s, skuMaxArr[i],
                    sohArr[i], divSohArr[i], skuBalance, cumItem, cumDiv, divBalance);
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
    /// As of Phase C₁ ("Box% override") RR runs in OVERRIDE mode for Phase
    /// 1b/2b — when a box's post-normal usability % crosses the user's
    /// "Box %" threshold, RR fills the box toward 100% by BYPASSING both
    /// caps:
    ///   • SKU Max balance (SKUMax − SOH − cumItem)
    ///   • Merch Need (Week) balance (MerchNeedWeek − cumDiv)
    /// Each row produced this way is tagged <c>IsOverride = true</c> so
    /// reports can roll up "Override Qty" separately from regular SIM Qty.
    /// Cumulative counters are still updated so subsequent phases see the
    /// inflated base (preventing duplicate top-ups in P2 after P1 override).
    ///
    /// <paramref name="bypassAllCaps"/>:
    /// <list type="bullet">
    ///   <item><c>true</c>  — Override mode: skip both caps, fill toward 100%,
    ///                        tag rows <c>IsOverride = true</c>. Used by
    ///                        Phase 1b / Phase 2b.</item>
    ///   <item><c>false</c> — Legacy strict mode: honour SKU Max + Merch Need
    ///                        caps. Currently no caller uses this — kept for
    ///                        future flexibility or A/B testing.</item>
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
        Dictionary<(string Store, string Item), int> skuMaxByStoreItem,
        Dictionary<(string Store, string Item), int> allocStoreItem,
        Dictionary<(string Store, int    Div ), int> allocStoreDiv,
        Dictionary<(string Store, string Item), int> phaseStoreItem,
        Dictionary<(string Store, int    Div ), int> phaseStoreDiv,
        Dictionary<(string Box, string Phase), long> boxAllocByPhase,
        List<LpmSimOutput> output,
        List<LpmSimAllocTrace> trace,
        bool bypassAllCaps)
    {
        if (remaining <= 0) return 0;
        if (!itemDiv.TryGetValue(line.ItemCode, out var divCode)) return 0;
        if (!eomByDiv.TryGetValue(divCode, out var stores) || stores.Count == 0) return 0;

        // Bucket per-(box,item,store) so we add ONE LpmSimOutput per (box,item,store)
        // across the cycles, not one per unit.
        var buckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 1.14.87 — REWRITTEN RR ITERATION (final, matches the planner's
        // Option 1 spec):
        //
        // Old shape (pre-1.14.87): 1 unit per store per cycle in PriorityRank
        // order, MerchNeedWeek bypassed but SKU Max honoured.
        //
        // New shape:
        //   - Group eligible stores into grade tiers (Diamond → Platinum →
        //     Gold → Silver). Stores without one of these four grades are
        //     EXCLUDED from RR entirely.
        //   - Per-store quantum varies by tier:
        //       Diamond  → 2 units per pass
        //       Platinum → 2 units per pass
        //       Gold     → 1 unit  per pass
        //       Silver   → 1 unit  per pass
        //   - Within each tier, sort by MerchNeedMonth Balance DESC
        //     (= MerchNeedMonth − cumDiv DESC; most-undersupplied store
        //     against its monthly demand goes first). Pre-sorted once at
        //     entry — order stays stable across passes; exhausted stores
        //     simply get skipped on later passes.
        //   - SKUMax = 0 exclusion + SKUMax ceiling always honoured.
        //   - MerchNeedMonth Balance ≤ 0 → skip the store. A store that's
        //     already at or above its monthly demand gets nothing from RR
        //     (planner clarification: even in override mode, don't push
        //     past monthly demand).
        //   - Take per pass = min(quota, remaining, skuHeadroom, mnmBal).
        //
        // The bypassAllCaps parameter is now effectively a no-op for the
        // MNM cap (always honoured) — kept on the signature for API
        // stability. SKUMax is honoured regardless.
        var GradeOrder = new[] { "DIAMOND", "PLATINUM", "GOLD", "SILVER" };
        var byGrade = new Dictionary<string, List<EomStore>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stores)
        {
            var g = (s.Grade ?? "").Trim().ToUpperInvariant();
            if (Array.IndexOf(GradeOrder, g) < 0) continue;   // excluded — no recognised grade
            if (!byGrade.TryGetValue(g, out var list))
                byGrade[g] = list = new();
            list.Add(s);
        }
        if (byGrade.Count == 0)
        {
            // No graded stores in this division — RR can't place anything.
            // Trace one row so the planner can see why on the Trace tab.
            trace.Add(NewTrace(batch, phaseTag, line, divCode, null,
                LineQty: line.Qty, Take: 0, Decision: "SKIP_NO_GRADE"));
            return 0;
        }
        // Pre-sort each tier by (CountryPriority ASC, MerchNeedMonth Balance DESC).
        // 1.14.88 — CountryPriority added as the PRIMARY within-tier sort key
        // (1.14.87 originally sorted by MNM Balance only — that mixed parent
        // and child countries inside the same grade tier, so an OMAN Diamond
        // store with higher MNM could be picked before a UAE Diamond store
        // with lower MNM). Now the parent country's stores come first inside
        // each grade tier (UAE Diamond → OMAN Diamond → UAE Platinum → OMAN
        // Platinum → ...), matching the 1a/2a sort behaviour and the planner
        // spec "OMAN should be after UAE stores".
        // For single-country runs (no LPM_CountryLink rows for the parent),
        // every store has CountryPriority = 0 so the secondary MNM-Balance
        // sort drives the order entirely — no behaviour change.
        foreach (var g in GradeOrder)
        {
            if (!byGrade.TryGetValue(g, out var gStores)) continue;
            // 1.14.107 — Within-tier sort key matches the active cap formula
            // so the "most-undersupplied store first" ordering stays meaningful:
            //   • EOM mode: TargetEOM − DivSOH − cumDiv (DESC)
            //   • MNM mode: MerchNeedMonth − cumDiv      (DESC; legacy 1.14.87)
            // MNM is still NOT a hard cap in 1b/2b (planner spec: RR can push
            // past monthly demand for partial-box fill); it only drives ORDER.
            byGrade[g] = gStores
                .OrderBy(s => s.CountryPriority)        // 1.14.88 — parent country first
                .ThenByDescending(s =>
                {
                    var cumDivForSort = allocStoreDiv.GetValueOrDefault((s.StoreID, divCode), 0);
                    if (req.CapMode == LpmSimCapMode.EomBalancePlusSkuMax)
                    {
                        var divSohForSort = sohByStoreDiv.GetValueOrDefault((s.StoreID, divCode), 0);
                        return s.TargetEOM - divSohForSort - cumDivForSort;
                    }
                    return (decimal)(s.MerchNeedMonth - cumDivForSort);
                })
                .ToList();
        }

        // 1.14.87 — Per-grade quantum.
        //   Diamond  → 2 / pass
        //   Platinum → 2 / pass
        //   Gold     → 1 / pass
        //   Silver   → 1 / pass
        static int QuotaForGrade(string g) => g switch
        {
            "DIAMOND"  => 2,
            "PLATINUM" => 2,
            "GOLD"     => 1,
            "SILVER"   => 1,
            _          => 0,    // unreachable — pre-filtered above
        };

        int placed = 0;
        // Outer cap: at most `remaining` passes (each pass places ≥ 1 unit
        // when it runs to completion; in practice we exit far sooner).
        var maxOuterPasses = remaining + 1;
        int pass = 0;
        while (remaining > 0 && pass < maxOuterPasses)
        {
            bool tookAnyThisPass = false;
            foreach (var grade in GradeOrder)
            {
                if (remaining <= 0) break;
                if (!byGrade.TryGetValue(grade, out var gStores)) continue;
                int quota = QuotaForGrade(grade);
                foreach (var s in gStores)
                {
                    if (remaining <= 0) break;
                    // SKUMax = 0 is an exclusion (rules 1-7). Skip everywhere.
                    var skuMaxExcl = skuMaxByStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
                    if (skuMaxExcl <= 0) continue;
                    // SKUMax ceiling — honoured in 1b/2b per planner spec.
                    var soh         = sohMap.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
                    var cumItem     = allocStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0);
                    var skuHeadroom = skuMaxExcl - Math.Max(0, soh) - cumItem;
                    if (skuHeadroom <= 0) continue;
                    // 1.14.87 (final): MerchNeedMonth is NOT a cap in 1b/2b —
                    // it's used only as the within-tier SORT key (OrderByDescending
                    // upstream). RR override deliberately allows pushing past
                    // monthly demand for partial-box fill. A Diamond store with
                    // MNM Balance ≤ 0 still receives RR units (it just sorts last
                    // within its tier).
                    // Take = min(per-grade quota, box remaining, SKU headroom).
                    var cumDiv  = allocStoreDiv.GetValueOrDefault((s.StoreID, divCode), 0);
                    var take = Math.Min(Math.Min(quota, remaining), skuHeadroom);
                    if (take <= 0) continue;

                    buckets[s.StoreID] = buckets.GetValueOrDefault(s.StoreID, 0) + take;
                    allocStoreItem[(s.StoreID, line.ItemCode)] = cumItem + take;
                    allocStoreDiv [(s.StoreID, divCode)]       = cumDiv + take;
                    phaseStoreItem[(s.StoreID, line.ItemCode)] = phaseStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0) + take;
                    phaseStoreDiv [(s.StoreID, divCode)]       = phaseStoreDiv .GetValueOrDefault((s.StoreID, divCode),       0) + take;
                    remaining -= take;
                    placed    += take;
                    tookAnyThisPass = true;
                }
            }
            if (!tookAnyThisPass) break;
            pass++;
        }

        // Emit one output line per (box, item, store) with the cumulative bucket qty.
        // Decision tag distinguishes override RR ("ALLOC_RR_OVR") from legacy
        // strict RR ("ALLOC_RR") for the trace report.
        var decisionTag = bypassAllCaps ? "ALLOC_RR_OVR" : "ALLOC_RR";
        foreach (var s in stores)
        {
            if (!buckets.TryGetValue(s.StoreID, out var qty) || qty <= 0) continue;
            output.Add(new LpmSimOutput
            {
                LPMBatchNo   = batch.LPMBatchNo,
                BoxNo        = line.BoxNo,
                PalletNo     = line.PalletNo,        // 1.14.12 — migration 041
                LPMDt        = line.LPMDt,
                Itemcode     = line.ItemCode,
                Qty          = qty,
                StoreID      = s.StoreID,
                CreateTS     = batch.CreateTS,
                CreatedBy    = req.User,
                Phase        = phaseTag,
                IsRoundRobin = true,
                IsOverride   = bypassAllCaps,
                // 1.14.18 — migration 044 columns. See note at the Phase-1
                // construction site above re UsabilityPct + SKUMax. The
                // `skuMax` local from the bypassAllCaps branch isn't in
                // scope here, so look it up directly — same value, same
                // (Store, Item) key.
                Season       = line.Season,
                BoxQty       = line.BoxQty,
                BoxItemQty   = line.Qty,
                DivCode      = divCode,
                SKUMax       = skuMaxByStoreItem.GetValueOrDefault((s.StoreID, line.ItemCode), 0),
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
                Decision   = decisionTag,
                Phase      = phaseTag,
                IsOverride = bypassAllCaps,
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
        BoxItem line, int divCode, EomStore s, int skuMax,
        int soh, int divSoh, int skuBalance, int cumItem, int cumDiv, decimal divBalance)
    {
        return new LpmSimAllocTrace
        {
            LPMBatchNo       = batch.LPMBatchNo,
            BoxNo            = line.BoxNo,
            ItemCode         = line.ItemCode,
            DivCode          = divCode,
            StoreID          = s.StoreID,
            SKUMax           = skuMax,           // per-(Store, Item) from LPM_SimItemSkuMax
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

    // 1.14.70 — `dataName` is the per-country DataName (null for UAE) so the
    // closed-box exclusion expression can address the right country DB. The
    // `closedBoxesDest` dictionary collects every BoxNo this query identifies
    // as closed (UAE: USA..upcboxhead.Closed='Y'; non-UAE: Exclude_Transfers_Sim
    // or CloseR1Pallet). Closed boxes are FILTERED OUT of `dest` so the
    // allocator never sees them, but their per-box meta is captured here for
    // the Gap-list CLOSED_BOX diagnostic insertion that runs in
    // BuildAndInsertUnallocatedDiagnosticAsync.
    private static async Task ReadBoxesAsync(
        System.Data.Common.DbConnection conn,
        bool isLpm,
        string country,
        string? dataName,
        int year, int month, string seasonClause,
        IReadOnlyList<string>? warehouses,
        bool includePurchasedBoxes,
        IReadOnlyList<string>? palletCategories,
        IReadOnlyList<DateTime>? lpmMonths,
        List<BoxItem> dest,
        Dictionary<string, ClosedBoxMeta> closedBoxesDest,
        CancellationToken ct)
    {
        // SARGable predicates (Phase F perf fix):
        //   • LPMDt cap uses < @endExclusive (= first day of NEXT month) instead
        //     of YEAR()/MONTH() wrappers, so SQL can index-seek any index on
        //     LPMDt instead of scanning every row in whboxitems.
        //   • ShopEligible filter rewritten as IS NULL OR <> 'E' so the
        //     ISNULL wrapper doesn't kill index usage.
        //   • includePurchasedBoxes (default false) skips the ShopEligible filter
        //     entirely so already-shopped boxes (ShopEligible = 'E') are also
        //     pulled into the allocation pool — surfaced as the "Include Non-
        //     Purchased Boxes" toggle on the SIM Generate page.
        //   • palletCategories — list of PalletCategory values to include.
        //     Empty/null means "no pallet-category filter" (every category
        //     pulled in). Page default is ["ELIGIBLE"] (legacy behaviour).
        // LPM months filter — when isLpm AND the planner picked specific
        // months, replace the default "< endExclusive" cap with an OR'd set
        // of date ranges (one per selected month). SARGable: each range is
        // a half-open interval [@m{i}_start, @m{i}_end) so the predicate
        // can use any index on LPMDt.
        var lpmMonthParams = new List<SqlParameter>();
        string lpmDtClause;
        if (isLpm)
        {
            if (lpmMonths is { Count: > 0 })
            {
                var ors = new List<string>(lpmMonths.Count);
                for (int i = 0; i < lpmMonths.Count; i++)
                {
                    var ms = new DateTime(lpmMonths[i].Year, lpmMonths[i].Month, 1);
                    var me = ms.AddMonths(1);
                    ors.Add($"(w.LPMDt >= @lm{i}_s AND w.LPMDt < @lm{i}_e)");
                    lpmMonthParams.Add(new SqlParameter($"@lm{i}_s", ms));
                    lpmMonthParams.Add(new SqlParameter($"@lm{i}_e", me));
                }
                lpmDtClause = "w.LPMDt IS NOT NULL AND (" + string.Join(" OR ", ors) + ")";
            }
            else
            {
                lpmDtClause = "w.LPMDt IS NOT NULL AND w.LPMDt < @endExclusive";
            }
        }
        else
        {
            lpmDtClause = "w.LPMDt IS NULL";
        }
        var (palletClause, palletParams) = BuildPalletCategoryClause(palletCategories);
        var shopEligibleClause = includePurchasedBoxes
            ? ""
            : "AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')";
        // The Warehouses multi-select on SIM Generate is now ONLY a filter
        // (which warehouses to include); priority order moved to
        // dbo.LPM_WarehousePriority so planners don't have to re-set it on
        // every run. We still build the IN(...) clause from the UI list.
        var (whClause, whParams) = BuildWarehouseClause(warehouses);
        // Box-stream ordering — drives the allocator's processing order:
        //   1. Warehouse priority ASC from dbo.LPM_WarehousePriority — null
        //      (= warehouse not in the table) sorts last via ISNULL(., 9999).
        //   2. BoxQty DESC — bigger boxes processed first within a WH.
        //   3. BoxNo, ItemCode — deterministic tiebreak so back-to-back runs
        //      with identical inputs produce identical batches.
        // BoxQty is the SUM of qty across all items in the box, computed via a
        // window function so the order remains stable per-box across all of
        // its item rows (a box with 3 items keeps all 3 rows together).
        // Country-aware whboxitems source — UAE uses racks.dbo.whboxitems,
        // others use [<DataName>].dbo.WHBoxItemsExport.
        var whSrc = await WhBoxItemsSource.ResolveAsync(conn, country, ct);
        using var cmd = conn.CreateCommand();
        // 1.14.12: w.PalletNo added to the projection so the allocator can
        // persist it on each LpmSimOutput row (LPMSIM_Output.PalletNo column,
        // migration 041). Doesn't affect ordering, grouping, or filtering.
        // 1.14.18 — pallettype JOIN restored for BOX-level Season filtering
        // (seasonClause uses pt.Season). The per-row Season column projected
        // out to BoxItem stays on w.Season — this is what the C# allocator's
        // per-item Season drop reads. So:
        //   • seasonClause           ⇒ pt.Season   (box-level filter)
        //   • Season projection      ⇒ w.Season    (per-item value)
        //   • PalletCategory filter  ⇒ w.PalletCategory  (unchanged from 1.14.17;
        //                              category data is identical between pt and w
        //                              and using w avoids a redundant lookup).
        // 1.14.70 — Closed-box flag. Projected as a bit column on every row so
        // the C# loop below can split eligible vs closed without a second
        // round-trip. The expression composes country-specific EXISTS
        // sub-queries against USA..upcboxhead (UAE) or [<DataName>]..
        // Exclude_Transfers_Sim + CloseR1Pallet (non-UAE). See
        // WhBoxItemsSource.BuildIsClosedExpression for the exact shape.
        var isClosedExpr = WhBoxItemsSource.BuildIsClosedExpression(country, dataName);
        cmd.CommandText = $@"
            SELECT w.BoxNo, w.PalletNo, w.LPMDt, w.ItemCode, w.Qty,
                   Season = CASE WHEN UPPER(ISNULL(w.Season, '')) = 'W' THEN 'W' ELSE 'S' END,
                   BoxQty = SUM(w.Qty) OVER (PARTITION BY w.BoxNo),
                   IsClosed = CASE WHEN {isClosedExpr} THEN 1 ELSE 0 END
              FROM {whSrc} w
              INNER JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
              LEFT  JOIN dbo.LPM_WarehousePriority wp
                     ON wp.Country   = @whCountry
                    AND wp.Warehouse = w.Warehouse
                    AND wp.IsActive  = 1
             WHERE 1 = 1
               {palletClause}
               {shopEligibleClause}
               {seasonClause}
               {whClause}
               AND {lpmDtClause}
             ORDER BY ISNULL(wp.Priority, 9999) ASC, BoxQty DESC, w.BoxNo, w.ItemCode;";
        cmd.Parameters.Add(new SqlParameter("@whCountry", country));
        foreach (var p in palletParams)    cmd.Parameters.Add(p);
        foreach (var p in lpmMonthParams)  cmd.Parameters.Add(p);
        // First day of the month AFTER the run period — half-open
        // interval excludes future-dated LPM boxes. Only used when LpmMonths
        // is empty (legacy "all months up to run period" path).
        cmd.Parameters.Add(new SqlParameter("@endExclusive",
            new DateTime(year, month, 1).AddMonths(1)));
        foreach (var p in whParams) cmd.Parameters.Add(p);
        cmd.CommandTimeout = 300;
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        var kindLabel = isLpm ? "LPM" : "Non-LPM";
        while (await rdr.ReadAsync(ct))
        {
            var boxNo    = rdr.GetString(0);
            var palletNo = rdr.IsDBNull(1) ? null : rdr.GetString(1);
            var lpmDt    = rdr.IsDBNull(2) ? null : (DateTime?)rdr.GetDateTime(2);
            var boxQty   = rdr.IsDBNull(6) ? 0L  : Convert.ToInt64(rdr.GetValue(6));
            // 1.14.70 — IsClosed split. Closed boxes go to closedBoxesDest
            // (keyed by BoxNo so per-row repeats collapse to one entry) and are
            // skipped from `dest` so the allocator never iterates them.
            // The kindLabel reflects the CURRENT call's isLpm (LPM call ⇒ "LPM",
            // Non-LPM call ⇒ "Non-LPM"); since a single BoxNo is exclusively
            // LPM or Non-LPM per its LPMDt, the first-write-wins behaviour
            // never mislabels a box.
            if (!rdr.IsDBNull(7) && rdr.GetInt32(7) == 1)
            {
                if (!closedBoxesDest.ContainsKey(boxNo))
                {
                    closedBoxesDest[boxNo] = new ClosedBoxMeta(
                        PalletNo: palletNo,
                        LPMDt:    lpmDt,
                        BoxKind:  kindLabel,
                        BoxQty:   boxQty);
                }
                continue;
            }
            dest.Add(new BoxItem(
                BoxNo:    boxNo,
                PalletNo: palletNo,
                LPMDt:    lpmDt,
                ItemCode: rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                Qty:      rdr.IsDBNull(4) ? 0  : rdr.GetInt32(4),
                Season:   rdr.IsDBNull(5) ? "S" : rdr.GetString(5),
                // 1.14.18: read the BoxQty window-function column (was already
                // projected for ORDER BY in 1.14.x; now consumed by the
                // allocator too).
                BoxQty:   boxQty));
        }
    }

    public async Task ApproveAsync(long batchNo, string user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var batch = await db.LpmSimBatches.FirstOrDefaultAsync(b => b.LPMBatchNo == batchNo, ct)
            ?? throw new InvalidOperationException($"Batch #{batchNo} not found.");
        if (batch.Status == "Approved")
            throw new InvalidOperationException("Batch is already approved.");

        // Lock: a production schedule pinned to this batch must be deleted
        // before the SIM batch state can change (avoids the schedule going
        // stale silently when the underlying allocation gets re-approved).
        var schedExists = await db.LpmSimProductionSchedules.AsNoTracking()
            .AnyAsync(s => s.LPMBatchNo == batchNo, ct);
        if (schedExists)
            throw new InvalidOperationException(
                $"A production schedule exists for batch #{batchNo}. Delete the schedule before approving the SIM batch.");

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

        // Lock: a production schedule must be deleted first — otherwise the
        // schedule's per-row Day pointers would be archived along with the
        // batch and there'd be no way to clean it up later. (FK CASCADE
        // would handle deletion, but we want the planner to do it
        // explicitly so they're aware their schedule is being lost.)
        var schedExists = await db.LpmSimProductionSchedules.AsNoTracking()
            .AnyAsync(s => s.LPMBatchNo == batchNo, ct);
        if (schedExists)
            throw new InvalidOperationException(
                $"A production schedule exists for batch #{batchNo}. Delete the schedule first.");

        // Each individual SQL statement gets up to 10 minutes — generous for the
        // backup INSERT which is single-statement and can't be chunked.
        db.Database.SetCommandTimeout(600);

        // 1) Backup just the user-visible bits (Output + Batch). Trace + balance
        //    tables can be re-derived; backing them up doubles I/O for no benefit.
        // 1.14.12: PalletNo column added to both tables (migration 041).
        // 1.14.18: Season + BoxQty + BoxItemQty + UsabilityPct + DivCode +
        // SKUMax added to both tables (migration 044). Listed explicitly in
        // the column list so the INSERT works whether the backup table has
        // the columns or not — migrations add them idempotently and the
        // runtime INSERT will fail loudly if shapes ever drift.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO dbo.LPMSIM_Output_Backup (Id, LPMBatchNo, BoxNo, PalletNo, LPMDt, Itemcode, Qty, StoreID, CreateTS, CreatedBy, Phase, IsRoundRobin, IsOverride, [Day], Season, BoxQty, BoxItemQty, UsabilityPct, DivCode, SKUMax, BackupTS)
SELECT Id, LPMBatchNo, BoxNo, PalletNo, LPMDt, Itemcode, Qty, StoreID, CreateTS, CreatedBy, Phase, IsRoundRobin, IsOverride, [Day], Season, BoxQty, BoxItemQty, UsabilityPct, DivCode, SKUMax, SYSDATETIME()
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

    /// <summary>
    /// Returns the latest <c>CreateTS</c> / row count / user that built the
    /// per-(Country, Year, Month) <c>LPM_SimItemSkuMax</c> snapshot — and
    /// whether it was built today (server-local calendar day).
    /// </summary>
    public async Task<LpmSimSkuMaxBuildStatus> GetLastSkuMaxBuildAsync(
        string country, int year, int month, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        DateTime? maxTs = null;
        long      cnt   = 0;
        string?   user  = null;
        long?     durationMs = null;
        using (var cmd = conn.CreateCommand())
        {
            // Single round-trip: max timestamp + count + most-recent user
            // + per-period duration (from LPM_SimItemSkuMaxBuild — added in
            // migration 032; left-join keeps this method working when the
            // migration hasn't been applied yet).
            //
            // 1.14.83 — Display-user source switched from per-row
            // LPM_SimItemSkuMax.CreatedBy to the per-period header
            // LPM_SimItemSkuMaxBuild.BuiltBy.
            //
            // Why: the apply phase's UPDATE statement bumps CreateTS = @now
            // when a row's data differs from staging, but does NOT update
            // CreatedBy (only INSERTs set it). So when user X built earlier
            // and user Y rebuilds, the UPDATEd rows end up with X's stale
            // CreatedBy alongside Y's fresh CreateTS — and the
            // "TOP 1 CreatedBy ORDER BY CreateTS DESC" query was therefore
            // returning X's name even though Y did the latest build.
            //
            // LPM_SimItemSkuMaxBuild is a per-period header table whose
            // BuiltBy column is MERGE-updated on every build with the
            // current @user value — always correct. We read BuiltBy from
            // it instead and only fall back to the per-row CreatedBy when
            // the header table doesn't exist (migration 032 not applied)
            // or doesn't have a row (build aborted before the MERGE).
            cmd.CommandText = @"
                SELECT MAX(CreateTS) AS MaxTS,
                       CAST(COUNT_BIG(*) AS bigint) AS RowCnt
                  FROM dbo.LPM_SimItemSkuMax
                 WHERE Country = @c AND Year1 = @y AND Month1 = @m;

                -- 1.14.83 — Primary source for the displayed user: per-period
                -- header. Returns (BuiltBy, DurationMs) — combined here so
                -- the round-trip count stays at one. Empty result set when
                -- migration 032 hasn't been applied yet.
                IF OBJECT_ID('dbo.LPM_SimItemSkuMaxBuild', 'U') IS NOT NULL
                BEGIN
                    SELECT BuiltBy, DurationMs
                      FROM dbo.LPM_SimItemSkuMaxBuild
                     WHERE Country = @c AND Year1 = @y AND Month1 = @m;
                END
                ELSE
                    SELECT CAST(NULL AS varchar(80)) AS BuiltBy,
                           CAST(NULL AS bigint)      AS DurationMs;

                -- 1.14.83 — Fallback per-row CreatedBy. Used only when the
                -- per-period header above didn't return a BuiltBy (migration
                -- 032 missing or pre-1.14.83 builds with no header row).
                SELECT TOP (1) CreatedBy
                  FROM dbo.LPM_SimItemSkuMax
                 WHERE Country = @c AND Year1 = @y AND Month1 = @m
                 ORDER BY CreateTS DESC;";
            cmd.Parameters.Add(new SqlParameter("@c", country));
            cmd.Parameters.Add(new SqlParameter("@y", year));
            cmd.Parameters.Add(new SqlParameter("@m", month));
            cmd.CommandTimeout = 60;

            // 1.14.83 — Result set order is now:
            //   #1: MAX(CreateTS), COUNT_BIG(*)
            //   #2: BuiltBy, DurationMs  (per-period header — primary user source)
            //   #3: TOP 1 CreatedBy      (per-row fallback — used only when #2's BuiltBy is NULL)
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                if (!rdr.IsDBNull(0)) maxTs = rdr.GetDateTime(0);
                cnt = rdr.IsDBNull(1) ? 0L : rdr.GetInt64(1);
            }
            // Per-period header — when migration 032 hasn't been applied, the
            // SQL returns one row of (NULL, NULL) rather than an empty result
            // set, so the ReadAsync still succeeds and BuiltBy stays null.
            string? builtBy = null;
            if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
            {
                if (!rdr.IsDBNull(0)) builtBy = rdr.GetString(0);
                if (!rdr.IsDBNull(1)) durationMs = rdr.GetInt64(1);
            }
            // Per-row fallback CreatedBy — only consulted when the header
            // didn't give us a name.
            string? fallbackBy = null;
            if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
            {
                if (!rdr.IsDBNull(0)) fallbackBy = rdr.GetString(0);
            }
            user = !string.IsNullOrEmpty(builtBy) ? builtBy : fallbackBy;
        }

        var fresh = maxTs.HasValue && maxTs.Value.Date >= DateTime.Today;
        return new LpmSimSkuMaxBuildStatus
        {
            LastBuildTS       = maxTs,
            LastBuildBy       = user,
            RowCount          = cnt,
            IsFreshToday      = fresh,
            LastBuildDuration = durationMs.HasValue ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
        };
    }

    /// <summary>
    /// Returns key timestamps / counts on the SKU Max inputs (EOM Output for
    /// Volume Group, LPM_SKUMaxRule, eligible whboxitems for the period).
    /// Surfaced in the UI so users can spot when an input has changed since
    /// the last build.
    /// </summary>
    public async Task<LpmSimInputFreshness> GetInputFreshnessAsync(
        string country, int year, int month, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        var fresh = new LpmSimInputFreshness();

        // 1) EOM Output for the period — proxy for Volume Group freshness
        //    since VolumeGroup is set on EOM Approve.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT MAX(CreateTS)
                  FROM dbo.LPM_EOM_Output
                 WHERE Country = @c AND Year1 = @y AND Month1 = @m;";
            cmd.Parameters.Add(new SqlParameter("@c", country));
            cmd.Parameters.Add(new SqlParameter("@y", year));
            cmd.Parameters.Add(new SqlParameter("@m", month));
            cmd.CommandTimeout = 30;
            var raw = await cmd.ExecuteScalarAsync(ct);
            fresh.LastVolumeGroupChange = raw is DateTime d ? d : null;
        }

        // 2) Active SKU Max Rules for the country — MAX(CreateTS) is a
        //    proxy for "last rule added/changed".
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT MAX(CreateTS)
                  FROM dbo.LPM_SKUMaxRule
                 WHERE Country = @c AND IsActive = 1;";
            cmd.Parameters.Add(new SqlParameter("@c", country));
            cmd.CommandTimeout = 30;
            var raw = await cmd.ExecuteScalarAsync(ct);
            fresh.LastSkuMaxRuleChange = raw is DateTime d ? d : null;
        }

        // 3) Latest LPMDt on eligible whboxitems for this period — proxy for
        //    "last WH Box upload that affects this period". MAX(LPMDt) tells
        //    the user how recent their box-month tagging is. Boxes with
        //    LPMDt = NULL (Non-LPM) are intentionally excluded since their
        //    timeliness is implicit.
        //
        // SARGable predicate: < @endExclusive (= start of NEXT month) instead
        // of YEAR(...)/MONTH(...) wrappers. Lets SQL use any index on LPMDt
        // and brings this query down from a multi-million-row scan to a
        // sub-second seek.
        // 1.14.103 — Route child countries (OMAN) to the parent's (UAE) WH
        // source so the "Last WH Box load" timestamp reflects the warehouse
        // OMAN actually ships from. Without this, the freshness panel
        // showed (none) / 1900-01-01 because OMAN's own WHBoxItemsExport
        // never gets loaded.
        var whSourceCountry = await CountryLinkResolver
            .ResolveWhSourceCountryAsync(db, country, ct);
        var freshSrc = await WhBoxItemsSource.ResolveAsync(conn, whSourceCountry, ct);
        using (var cmd = conn.CreateCommand())
        {
            // 1.14.17: PalletCategory now read from whboxitems (w.PalletCategory)
            // instead of pallettype master (pt.PalletCategory). pallettype
            // JOIN dropped to align this query with the other two SIM
            // Generate queries. Same rationale as 1.14.9 / BuildPalletCategoryClause.
            cmd.CommandText = $@"
                SELECT MAX(w.LPMDt)
                  FROM {freshSrc} w
                 WHERE w.PalletCategory = 'ELIGIBLE'
                   AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
                   AND w.LPMDt IS NOT NULL
                   AND w.LPMDt < @endExclusive;";
            // First day of the month AFTER the run period — half-open
            // interval excludes future-dated boxes.
            cmd.Parameters.Add(new SqlParameter("@endExclusive",
                new DateTime(year, month, 1).AddMonths(1)));
            cmd.CommandTimeout = 60;
            var raw = await cmd.ExecuteScalarAsync(ct);
            fresh.LastWHBoxLoad = raw is DateTime d ? d : null;
        }

        return fresh;
    }

    /// <summary>
    /// User-triggered "Build SKU Max" for a run period. Loads EOM Output rows
    /// for the period (for the per-store Volume Group lookup) and rebuilds
    /// <c>LPM_SimItemSkuMax</c> from <c>whboxitems</c> + <c>LPM_SKUMaxRule</c>
    /// + <c>LPM_StoreDivAccess</c>.
    ///
    /// Throws <see cref="InvalidOperationException"/> if no EOM rows exist
    /// for the period (SKU Max needs Volume Group from EOM).
    /// </summary>
    /// <summary>
    /// Counts how many items the build WOULD process for the given scope
    /// without actually running the build. Surfaced in the confirmation
    /// dialog before BuildSkuMaxAsync kicks off so the planner sees what
    /// they're committing to.
    /// </summary>
    /// <summary>
    /// Returns the distinct-item count for each of the three SKU Max build
    /// scopes (All / LpmOnly / NonLpmOnly) in a single SQL round-trip — used
    /// by the SIM Generate UI to show "10,408 SKUs" next to the Scope
    /// dropdown so the planner sees how many items each scope will rebuild
    /// before clicking Build SKU Max.
    /// </summary>
    public async Task<SkuMaxScopeCounts> GetSkuMaxScopeCountsAsync(
        string country, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        long all = 0, lpm = 0, nonLpm = 0;
        // Country-aware whboxitems source — UAE uses racks.dbo.whboxitems,
        // others use [<DataName>].dbo.WHBoxItemsExport.
        // 1.14.103 — Route child countries (OMAN) to the parent's (UAE) WH
        // source so the SKU-count chip matches what the build will actually
        // process. Without this, OMAN showed "0 SKUs · 0 LPM · 0 Non-LPM"
        // and the planner couldn't tell whether a build would be meaningful.
        var whSourceCountry = await CountryLinkResolver
            .ResolveWhSourceCountryAsync(db, country, ct);
        var scopeSrc = await WhBoxItemsSource.ResolveAsync(conn, whSourceCountry, ct);
        using (var cmd = conn.CreateCommand())
        {
            // Same ItemDiv + whboxitems shape as PreviewSkuMaxBuildAsync /
            // BuildItemSkuMaxAsync — keeps the count consistent with what
            // the build will actually process. Item is "in scope" iff it
            // exists in upc_subclass AND its subclass maps to a Division.
            cmd.CommandText = $@"
                ;WITH ItemDiv AS (
                    SELECT u.itemcode
                      FROM Datareporting.dbo.upc_subclass    u
                      INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                      INNER JOIN dbo.Division                 d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                     GROUP BY u.itemcode
                ),
                LpmItems AS (
                    SELECT DISTINCT w.ItemCode
                      FROM {scopeSrc} w
                      INNER JOIN ItemDiv id ON id.itemcode = w.ItemCode
                     WHERE w.ItemCode IS NOT NULL AND w.ItemCode <> ''
                       AND w.LPMDt IS NOT NULL
                ),
                NonLpmItems AS (
                    SELECT DISTINCT w.ItemCode
                      FROM {scopeSrc} w
                      INNER JOIN ItemDiv id ON id.itemcode = w.ItemCode
                     WHERE w.ItemCode IS NOT NULL AND w.ItemCode <> ''
                       AND w.LPMDt IS NULL
                )
                SELECT
                    (SELECT COUNT(*) FROM (SELECT ItemCode FROM LpmItems UNION SELECT ItemCode FROM NonLpmItems) u) AS AllCnt,
                    (SELECT COUNT(*) FROM LpmItems)    AS LpmCnt,
                    (SELECT COUNT(*) FROM NonLpmItems) AS NonLpmCnt;";
            cmd.CommandTimeout = 120;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                all    = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                lpm    = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                nonLpm = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
            }
        }
        return new SkuMaxScopeCounts(all, lpm, nonLpm);
    }

    public async Task<SkuMaxBuildPreview> PreviewSkuMaxBuildAsync(
        string country, int year, int month,
        LpmSimSkuMaxScope scope = LpmSimSkuMaxScope.All,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        string scopeWhere = scope switch
        {
            LpmSimSkuMaxScope.LpmOnly    => "AND w.LPMDt IS NOT NULL",
            LpmSimSkuMaxScope.NonLpmOnly => "AND w.LPMDt IS NULL",
            _                             => ""
        };

        long itemsInScope = 0, itemsInWhBoxes = 0, existingInScope = 0, existingKept = 0;
        // Country-aware whboxitems source — UAE uses racks.dbo.whboxitems,
        // others use [<DataName>].dbo.WHBoxItemsExport.
        // 1.14.103 — Route child countries (OMAN) to the parent's (UAE) WH
        // source so the preview matches what BuildItemSkuMaxAsync will
        // actually read. Without this, OMAN's confirmation dialog reported
        // "0 items to rebuild" because OMAN's own WHBoxItemsExport is empty
        // (Oman ships from UAE warehouse).
        var whSourceCountry = await CountryLinkResolver
            .ResolveWhSourceCountryAsync(db, country, ct);
        var previewSrc = await WhBoxItemsSource.ResolveAsync(conn, whSourceCountry, ct);
        using (var cmd = conn.CreateCommand())
        {
            // For the All scope the build does a FULL period wipe, so every
            // existing row is "in scope" (will be replaced) and 0 are kept.
            // For LPM/Non-LPM the build does a scoped wipe — only items in
            // ItemsInScope are deleted; the rest survive untouched.
            string existingInScopeExpr = scope == LpmSimSkuMaxScope.All
                ? @"SELECT COUNT(*) FROM dbo.LPM_SimItemSkuMax m
                       WHERE m.Country = @c AND m.Year1 = @y AND m.Month1 = @m"
                : @"SELECT COUNT(*) FROM dbo.LPM_SimItemSkuMax m
                       WHERE m.Country = @c AND m.Year1 = @y AND m.Month1 = @m
                         AND EXISTS (SELECT 1 FROM ItemsInScope s WHERE s.ItemCode = m.ItemCode)";

            string existingKeptExpr = scope == LpmSimSkuMaxScope.All
                ? "SELECT 0"
                : @"SELECT COUNT(*) FROM dbo.LPM_SimItemSkuMax m
                       WHERE m.Country = @c AND m.Year1 = @y AND m.Month1 = @m
                         AND NOT EXISTS (SELECT 1 FROM ItemsInScope s WHERE s.ItemCode = m.ItemCode)";

            cmd.CommandText = $@"
                ;WITH ItemDiv AS (
                    SELECT u.itemcode
                      FROM Datareporting.dbo.upc_subclass    u
                      INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                      INNER JOIN dbo.Division                 d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                     GROUP BY u.itemcode
                ),
                ItemsInScope AS (
                    SELECT DISTINCT w.ItemCode
                      FROM {previewSrc} w
                      INNER JOIN ItemDiv id ON id.itemcode = w.ItemCode
                     WHERE w.ItemCode IS NOT NULL AND w.ItemCode <> ''
                       {scopeWhere}
                )
                SELECT
                    (SELECT COUNT(*) FROM ItemsInScope) AS InScope,
                    (SELECT COUNT(DISTINCT w.ItemCode)
                       FROM {previewSrc} w
                      WHERE w.ItemCode IS NOT NULL AND w.ItemCode <> ''
                        {scopeWhere}
                    ) AS InWhBoxes,
                    ({existingInScopeExpr}) AS ExistingInScope,
                    ({existingKeptExpr})    AS ExistingKept;";
            cmd.Parameters.Add(new SqlParameter("@c", country));
            cmd.Parameters.Add(new SqlParameter("@y", year));
            cmd.Parameters.Add(new SqlParameter("@m", month));
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                itemsInScope    = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                itemsInWhBoxes  = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                existingInScope = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
                existingKept    = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3);
            }
        }
        return new SkuMaxBuildPreview(
            ItemsInScope:        itemsInScope,
            ItemsInWhBoxes:      itemsInWhBoxes,
            DroppedNoMaster:     Math.Max(0, itemsInWhBoxes - itemsInScope),
            ExistingRowsInScope: existingInScope,
            ExistingRowsKept:    existingKept);
    }

    public async Task<LpmSimSkuMaxBuildStatus> BuildSkuMaxAsync(
        string country, int year, int month, CancellationToken ct = default,
        string? userOverride = null, IProgress<string>? progress = null,
        LpmSimSkuMaxScope scope = LpmSimSkuMaxScope.All)
    {
        progress?.Report("Loading EOM stores…");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        // 1.14.102 — Lock gate. Final defence-in-depth check before doing any
        // real work. The SkuMaxBuildJobManager.Start path and the
        // SkuMaxBuildScheduler both check this table earlier and bail out
        // cleanly, so reaching here while locked should only happen if a
        // caller bypassed both — but we still want to refuse to overwrite
        // LPM_SimItemSkuMax for a locked country.
        var lockRow = await db.LpmSkuMaxLocks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Country == country, ct);
        if (lockRow is not null)
        {
            throw new InvalidOperationException(
                $"Build SKU Max is LOCKED for {country} " +
                $"(locked {lockRow.LockedAt:dd-MMM-yyyy HH:mm}" +
                (string.IsNullOrEmpty(lockRow.LockedBy) ? "" : $" by {lockRow.LockedBy}") +
                (string.IsNullOrEmpty(lockRow.Reason)   ? "" : $" — {lockRow.Reason}") +
                "). Delete the LPM_SkuMaxLock row to unlock.");
        }

        // 1.14.102 — Country-link routing for the WH source (whboxitems).
        // OMAN ships from UAE's warehouse, so its SKU Max must be built from
        // UAE's whboxitems. ResolveWhSourceCountryAsync reads LPM_CountryLink
        // and returns the parent country when one exists, else the country
        // itself. Same mechanism EOM Calculator has used since 1.14.77.
        var whSourceCountry = await CountryLinkResolver
            .ResolveWhSourceCountryAsync(db, country, ct);

        // Force-close on cancel — SqlCommand.Cancel() (which the .NET token
        // plumbing calls when ct fires) sends a TDS attention signal, but
        // SQL Server can take a long time to honour it during a big
        // transaction. Closing the underlying SqlConnection forces SQL
        // Server to abandon the session immediately and roll back any
        // in-flight transaction. The Register call returns a registration
        // we dispose at the end of this method (success or fail).
        using var cancelReg = ct.Register(() =>
        {
            try
            {
                if (conn is SqlConnection sqlConn && sqlConn.State == System.Data.ConnectionState.Open)
                    sqlConn.Close();
            }
            catch { /* swallow — cancel path is best-effort */ }
        });

        // Load EOM rows for the period (per-store Volume Group lives there).
        var eomByDiv = new Dictionary<int, List<EomStore>>();
        using (var cmd = conn.CreateCommand())
        {
            // 1.14.87 — Same column-list swap as the primary EOM read above:
            // MerchNeedWeek → MerchNeedMonth + Grade added so the EomStore
            // constructor is consistent across both call sites.
            cmd.CommandText = @"
                SELECT eo.StoreID, eo.DivCode, ISNULL(eo.SKUMax, 0) AS SKUMax,
                       ISNULL(eo.TargetEOM, 0)        AS TargetEOM,
                       ISNULL(eo.PriorityRank, 0)     AS PriorityRank,
                       ISNULL(eo.WtAvgSoldQty, 0)     AS WtAvgSoldQty,
                       ISNULL(eo.VolumeGroup, '')     AS VolumeGroup,
                       ISNULL(eo.MerchNeedMonth, 0)   AS MerchNeedMonth,
                       ISNULL(eo.Grade, '')           AS Grade
                  FROM dbo.LPM_EOM_Output eo
                  INNER JOIN dbo.DataSettings ds
                          ON ds.StoreID = eo.StoreID
                         AND ds.SIMCountry = @country
                 WHERE eo.Country = @country
                   AND eo.Year1   = @y
                   AND eo.Month1  = @m;";
            cmd.Parameters.Add(new SqlParameter("@country", country));
            cmd.Parameters.Add(new SqlParameter("@y", year));
            cmd.Parameters.Add(new SqlParameter("@m", month));
            cmd.CommandTimeout = 120;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var s = new EomStore(
                    StoreID:        rdr.GetString(0),
                    DivCode:        rdr.GetInt32(1),
                    SKUMax:         rdr.GetInt32(2),
                    TargetEOM:      rdr.GetDecimal(3),
                    PriorityRank:   rdr.GetDecimal(4),
                    WtAvgSold:      rdr.GetDecimal(5),
                    VolumeGroup:    rdr.GetString(6),
                    MerchNeedMonth: rdr.GetInt32(7),
                    Grade:          rdr.GetString(8));
                if (!eomByDiv.TryGetValue(s.DivCode, out var list))
                    eomByDiv[s.DivCode] = list = new();
                list.Add(s);
            }
        }
        if (eomByDiv.Count == 0)
            throw new InvalidOperationException(
                $"No EOM Output rows found for {country} {year:D4}-{month:D2}. " +
                "Run EOM Generate + Approve before building SKU Max.");

        // userOverride lets background callers (SkuMaxBuildJobManager) supply
        // the originating user without depending on the scoped ICurrentUser
        // (which only exists on the request that started the build).
        var user = !string.IsNullOrEmpty(userOverride) ? userOverride : (currentUser?.Name ?? "");
        // 1.14.102 — Pass whSourceCountry through so the worker reads the
        // parent country's whboxitems (UAE) for child-country builds (OMAN).
        var rows = await BuildItemSkuMaxAsync((SqlConnection)conn, country, whSourceCountry, year, month, user, eomByDiv, progress, scope, ct);

        return new LpmSimSkuMaxBuildStatus
        {
            LastBuildTS  = DateTime.Now,
            LastBuildBy  = string.IsNullOrEmpty(user) ? null : user,
            RowCount     = rows,
            IsFreshToday = true,
        };
    }

    /// <summary>
    /// Rebuild <c>LPM_SimItemSkuMax</c> for a run period — SQL-side fast path.
    /// <para>
    /// Phase G: rewrote the entire build as a single
    /// <c>INSERT … SELECT</c> driven by SQL-side <c>#temp</c> tables and an
    /// <c>OUTER APPLY</c> band lookup. The previous version round-tripped
    /// every output row through C# (SQL → reader → DataTable → bulk copy),
    /// which cost ~10× more wall-clock time at UAE scale. This version:
    /// </para>
    /// <list type="number">
    /// <item>Stages the four input sets into temp tables (item-warehouse
    ///       stock, EOM stores, SKUMax rule bands, deactivated overrides).</item>
    /// <item>Runs ONE big <c>INSERT … SELECT</c> that joins them, applies
    ///       the band lookup via <c>OUTER APPLY</c>, and writes directly
    ///       into <c>LPM_SimItemSkuMax</c> with <c>(TABLOCK)</c> for
    ///       minimal logging where possible.</item>
    /// </list>
    /// <para>
    /// No row ever leaves SQL Server. Targets ~5–10× speedup for
    /// 1M+ row builds; the larger the country, the bigger the win.
    /// </para>
    /// </summary>
    private static async Task<long> BuildItemSkuMaxAsync(
        SqlConnection conn,
        string country, string whSourceCountry, int year, int month, string user,
        Dictionary<int, List<EomStore>> eomByDiv,
        IProgress<string>? progress,
        LpmSimSkuMaxScope scope,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Scope filter applied at the whboxitems read — narrows the items the
        // build will refresh. Empty string = no filter (All scope).
        string scopeWhere = scope switch
        {
            LpmSimSkuMaxScope.LpmOnly    => "AND w.LPMDt IS NOT NULL",
            LpmSimSkuMaxScope.NonLpmOnly => "AND w.LPMDt IS NULL",
            _                             => ""
        };

        // Country-aware whboxitems source. UAE uses racks.dbo.whboxitems,
        // others use [<DataName>].dbo.WHBoxItemsExport.
        // 1.14.102 — Resolve against `whSourceCountry` (parent country when
        // LPM_CountryLink routes a child like OMAN to UAE), not the raw
        // run country. OMAN's SKU Max now reads UAE's whboxitems.
        var whSrc = await WhBoxItemsSource.ResolveAsync(conn, whSourceCountry, ct);

        // ATOMIC REBUILD ORDER (fixed in v1.7.1):
        //   1) Build & populate temp tables  ← non-destructive, no transaction
        //   2) BEGIN TRAN
        //      a) DELETE old rows for the period
        //      b) INSERT new rows from temp tables
        //   3) COMMIT (or ROLLBACK on error — old snapshot survives unchanged)
        //
        // Earlier (v1.7.0) the DELETE happened FIRST and the INSERT failed
        // due to a SQL syntax error — wiping the period's snapshot with no
        // replacement. This new ordering guarantees that a failure ANYWHERE
        // in the pipeline preserves the existing data.

        long msDelete = 0;

        // 1) Create temp tables. We use indexed temp tables so the big
        // INSERT below can hash-join efficiently. Column widths match the
        // source columns (varchar(30) ItemCode, varchar(25) StoreID, etc.)
        // — anything narrower would silently truncate keys and drop joins.
        progress?.Report("Building temp tables…");
        using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = @"
                IF OBJECT_ID('tempdb..#ItemWh') IS NOT NULL DROP TABLE #ItemWh;
                IF OBJECT_ID('tempdb..#Stores') IS NOT NULL DROP TABLE #Stores;
                IF OBJECT_ID('tempdb..#Rules')  IS NOT NULL DROP TABLE #Rules;
                IF OBJECT_ID('tempdb..#Deact')  IS NOT NULL DROP TABLE #Deact;

                CREATE TABLE #ItemWh (
                    ItemCode varchar(30) NOT NULL,
                    DivCode  int         NOT NULL,
                    Season   char(1)     NOT NULL,
                    WHBoxQty bigint      NOT NULL
                );
                CREATE CLUSTERED INDEX IX_ItemWh ON #ItemWh (DivCode, Season, ItemCode);

                CREATE TABLE #Stores (
                    StoreID     varchar(25) NOT NULL,
                    DivCode     int         NOT NULL,
                    VolumeGroup varchar(20) NOT NULL
                );
                CREATE CLUSTERED INDEX IX_Stores ON #Stores (DivCode, StoreID);

                CREATE TABLE #Rules (
                    DivCode     int         NOT NULL,
                    GroupCode   varchar(20) NOT NULL,
                    WHStockFrom int         NOT NULL,
                    WHStockTo   int         NOT NULL,
                    SKUMax      int         NOT NULL
                );
                CREATE CLUSTERED INDEX IX_Rules ON #Rules (DivCode, GroupCode, WHStockFrom);

                CREATE TABLE #Deact (
                    StoreID varchar(25) NOT NULL,
                    DivCode int         NOT NULL,
                    PRIMARY KEY (StoreID, DivCode)
                );";
            ddl.CommandTimeout = 60;
            await ddl.ExecuteNonQueryAsync(ct);
        }

        // 2) Populate #ItemWh — per-(Item, Div, Season) warehouse stock.
        // POLICY (Phase H): SKU Max now covers EVERY item in whboxitems —
        // we no longer narrow by PalletCategory, ShopEligible, or LPMDt.
        // Rationale: SIM Generate has toggles to widen its eligibility
        // (Include Non-Purchased, custom Pallet Categories, custom LPM
        // Months) and any item that COULD enter SIM under any combination
        // of those toggles needs a SKU Max row. Otherwise the allocator
        // sees SKUMax = 0 and silently drops the box (the "2,151 vs 121K"
        // bug the planner hit on May-2026 UAE).
        //
        // Items still need to be in upc_subclass × subclassmaster × Division
        // (the ItemDiv CTE INNER JOIN) — without that we can't classify the
        // item's division, so SIM couldn't allocate it anyway. Items missing
        // from upc_subclass need to be added there as a separate fix.
        //
        // Side-effect: WHBoxQty per (Item, Div, Season) gets larger now
        // (it sums every box for that item, not just the eligible/current
        // ones). That can push items into a different SKU Max rule band.
        // If this matters operationally, we can split the qty into
        // current/future/non-LPM buckets in a future revision.
        progress?.Report("Computing item × division × season stock (all whboxitems)…");
        using (var pop = conn.CreateCommand())
        {
            // T-SQL syntax: a CTE (WITH) must come BEFORE the INSERT keyword,
            // not between INSERT and SELECT. Leading semicolon defends against
            // any prior statement in the batch needing termination.
            // 1.14.9: Season now read from whboxitems.Season directly.
            // Was pt.Season via INNER JOIN to bfldata.dbo.pallettype, which
            // (a) silently dropped boxes whose PalletType has no master row,
            // and (b) could differ from w.Season when the master's Season
            // was stale. Aligns the SKU Max Build with WH Stock Position +
            // Variance Report + EOM Generate Division Summary (all use
            // w.Season since 1.13.2 / 1.14.7). UPPER() added for case-
            // insensitive matching, same convention as the other rules.
            pop.CommandText = $@"
                ;WITH ItemDiv AS (
                    SELECT u.itemcode, MIN(d.DivCode) AS DivCode
                      FROM Datareporting.dbo.upc_subclass    u
                      INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                      INNER JOIN dbo.Division                 d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                     GROUP BY u.itemcode
                )
                INSERT INTO #ItemWh (ItemCode, DivCode, Season, WHBoxQty)
                SELECT w.ItemCode,
                       id.DivCode,
                       CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 'W' ELSE 'S' END,
                       SUM(CAST(ISNULL(w.Qty, 0) AS bigint))
                  FROM {whSrc} w
                  INNER JOIN ItemDiv id ON id.itemcode = w.ItemCode
                 WHERE 1 = 1
                   {scopeWhere}
                 GROUP BY w.ItemCode, id.DivCode,
                          CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 'W' ELSE 'S' END;";
            pop.CommandTimeout = 1200;
            await pop.ExecuteNonQueryAsync(ct);
        }

        // 2b) Diagnostic counts — surface in the progress banner so the
        // planner sees how many items will be processed (and how many were
        // dropped because they aren't mapped in upc_subclass).
        long itemsInScope    = 0;     // distinct items ending up in #ItemWh
        long itemsInWhBoxes  = 0;     // distinct items in racks.dbo.whboxitems matching scope
        using (var cnt = conn.CreateCommand())
        {
            // Same scopeWhere as the #ItemWh insert so the "InWhBoxes"
            // count compares apples-to-apples with what we processed.
            cnt.CommandText = $@"
                SELECT
                    (SELECT COUNT(DISTINCT ItemCode) FROM #ItemWh) AS InScope,
                    (SELECT COUNT(DISTINCT w.ItemCode)
                       FROM {whSrc} w
                      WHERE w.ItemCode IS NOT NULL AND w.ItemCode <> ''
                        {scopeWhere.Replace("AND w.LPMDt", "AND w.LPMDt")}
                    ) AS InWhBoxes;";
            cnt.CommandTimeout = 60;
            using var rdr = await cnt.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                itemsInScope   = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                itemsInWhBoxes = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
            }
        }
        var droppedNoMaster = Math.Max(0, itemsInWhBoxes - itemsInScope);
        progress?.Report(
            $"{itemsInScope:N0} items will be processed " +
            $"({itemsInWhBoxes:N0} in whboxitems, {droppedNoMaster:N0} dropped — not in upc_subclass)…");

        var msItemWh = sw.ElapsedMilliseconds; sw.Restart();

        // 3) Populate #Stores from EOM Output. (DataSettings join enforces
        // the country guard the engine relies on.)
        progress?.Report("Loading store list from EOM…");
        using (var pop = conn.CreateCommand())
        {
            pop.CommandText = @"
                INSERT INTO #Stores (StoreID, DivCode, VolumeGroup)
                SELECT eo.StoreID, eo.DivCode, ISNULL(eo.VolumeGroup, '')
                  FROM dbo.LPM_EOM_Output eo
                  INNER JOIN dbo.DataSettings ds
                          ON ds.StoreID = eo.StoreID AND ds.SIMCountry = @country
                 WHERE eo.Country = @country
                   AND eo.Year1   = @y
                   AND eo.Month1  = @m;";
            pop.Parameters.Add(new SqlParameter("@country", country));
            pop.Parameters.Add(new SqlParameter("@y", year));
            pop.Parameters.Add(new SqlParameter("@m", month));
            pop.CommandTimeout = 120;
            await pop.ExecuteNonQueryAsync(ct);
        }

        // 4) Populate #Rules.
        progress?.Report("Loading SKU Max rule bands…");
        using (var pop = conn.CreateCommand())
        {
            pop.CommandText = @"
                INSERT INTO #Rules (DivCode, GroupCode, WHStockFrom, WHStockTo, SKUMax)
                SELECT DivCode, GroupCode, WHStockFrom, WHStockTo, SKUMax
                  FROM dbo.LPM_SKUMaxRule
                 WHERE IsActive = 1 AND Country = @country;";
            pop.Parameters.Add(new SqlParameter("@country", country));
            pop.CommandTimeout = 60;
            await pop.ExecuteNonQueryAsync(ct);
        }

        // 5) Populate #Deact — (Store, Div) pairs with IsActive = 0 force SKUMax = 0.
        using (var pop = conn.CreateCommand())
        {
            pop.CommandText = @"
                INSERT INTO #Deact (StoreID, DivCode)
                SELECT DISTINCT StoreID, DivCode
                  FROM dbo.LPM_StoreDivAccess
                 WHERE Country = @country AND IsActive = 0;";
            pop.Parameters.Add(new SqlParameter("@country", country));
            pop.CommandTimeout = 60;
            await pop.ExecuteNonQueryAsync(ct);
        }
        var msInputs = sw.ElapsedMilliseconds; sw.Restart();

        // 6) ATOMIC DELETE + INSERT. Wrapped in a single SQL transaction with
        // SET XACT_ABORT ON so any failure (syntax error, deadlock, timeout)
        // rolls BOTH operations back. The period's existing snapshot survives
        // unchanged unless we successfully replace it.
        //
        // Implementation note: combining DELETE and INSERT in one batch lets
        // SQL Server hold a single TABLOCK throughout, which is faster than
        // re-acquiring locks for separate statements anyway. With (TABLOCK)
        // on the INSERT also enables minimal logging in SIMPLE/BULK_LOGGED
        // recovery models.
        progress?.Report("Replacing LPM_SimItemSkuMax (delete + insert + exclusions in one transaction)…");
        long inserted = 0;
        long excluded = 0;
        long priceCapped = 0;
        long deactivated = 0;
        long deptDeactivated = 0;
        // Scope filter for the delta-apply DELETE — preserves the LpmOnly /
        // NonLpmOnly semantic that only items currently in #ItemWh (i.e.
        // items being rebuilt for the chosen scope) should have stale rows
        // pruned from the target. All-scope deletes any target row not in
        // #NewSnap; scoped builds only delete in-scope items so out-of-scope
        // rows from prior builds survive untouched.
        string deleteScopeFilter = scope == LpmSimSkuMaxScope.All
            ? ""
            : "AND EXISTS (SELECT 1 FROM #ItemWh iw WHERE iw.ItemCode = tgt.ItemCode)";
        long deltaDeleted = 0;
        long deltaUpdated = 0;
        long deltaInserted = 0;
        long msDelta = 0;
        int  msRule1 = 0, msRule2 = 0, msRule3 = 0, msRule4 = 0, msRule5 = 0, msRule6 = 0, msRule7 = 0;
        // Per-rule error capture from the SQL TRY/CATCH blocks. Key = rule
        // number (1..7); value = ERROR_MESSAGE() text or null when the rule
        // succeeded. Surfaces in the build banner so e.g. a Hodata access
        // failure on Rule 5 doesn't kill rules 1-4, 6, 7.
        var ruleErrors = new Dictionary<int, string?>
        {
            { 1, null }, { 2, null }, { 3, null }, { 4, null },
            { 5, null }, { 6, null }, { 7, null },
        };
        using (var ins = conn.CreateCommand())
        {
            // Single batch — DELETE + INSERT + 4 zero-out exclusion rules + 1
            // price-band cap rule — all wrapped in BEGIN TRY/TRAN/COMMIT so
            // failures roll back atomically. The SKUMax-snapshot tempdb table
            // at the top means each rule's audit row records the ORIGINAL
            // pre-override SKUMax even when multiple rules match the same item.
            //
            // The 7 rules that can override SKUMax in this build:
            //   1) usa.dbo.ExcludeExport_Planning           → SKUMax = 0
            //   2) usa.dbo.ExcludeSubclass                  → SKUMax = 0
            //   3) bfldata.dbo.RemoveItemsFromTransfer      → SKUMax = 0
            //   4) usa.dbo.ExcludeItemsMFCS                 → SKUMax = 0
            //   5) usa.dbo.DeptPriceMaxQty_MH4 (price band) → SKUMax = maxqty (replace)
            //   6) dbo.LPM_StoreDivAccess (IsActive=0)      → SKUMax = 0
            //   7) dbo.LPM_StoreDeptAccess (IsActive=0)     → SKUMax = 0
            //
            // All 7 rules write audit rows to dbo.LPM_SimItemSkuMaxExcluded
            // with a distinct SourceTable so admins can audit every override.
            // Rule 6 also re-runs at SIM Generate (Pre-Generate deactivation
            // sync) to catch deactivations made between the build and the run.
            //
            // Order of UPDATEs matters: the price-band CAP runs FIRST (sets
            // SKUMax = maxqty), then the 4 zero-out rules run AFTER (set
            // SKUMax = 0). When an item matches both, the zero wins —
            // exclusions are absolute and trump price caps.
            //
            // Column-name reference (verified against the live schema):
            //   • usa.dbo.ExcludeExport_Planning      (ShopName, ItemCode, …)
            //   • usa.dbo.ExcludeSubclass             (Shop,     MH4ID, Inactive, …)   ← uses Shop, not Shopname
            //   • bfldata.dbo.RemoveItemsFromTransfer (ShopName, Itemcode, Trndate, …)
            //   • usa.dbo.ExcludeItemsMFCS            (Shopname, HScode, …)
            //   • usa.dbo.DeptPriceMaxQty_MH4         (shopname, DivCode, DEPARTMENT, PriceF, PriceT, maxqty, …)
            //   • Hodata.dbo.SalesPrice               (CostCode, ItemCode, SalesRate, TrnDate, …)
            //   • Datareporting.dbo.subclassmaster    (MH4ID, Division, Department, …)
            //   • Datareporting.dbo.upc_subclass      (itemcode, MH4ID)
            //   (upcbarcodes is no longer joined — Rule 4 now uses
            //    ExcludeItemsMFCS.Itemcode directly; the 18M-row table is
            //    never touched.)
            //
            // SQL Server uses case-insensitive column names by default, so
            // "Shopname" / "shopname" / "ShopName" / "SHOPNAME" all match the
            // same column. ExcludeSubclass is the odd one out — its store
            // column is literally named "Shop" (no "name" suffix), so the
            // join on Rule 2 below explicitly references es.Shop. Mismatch
            // confirmed by the build's exclusions-SKIPPED message:
            //   "exclusions SKIPPED (Invalid column name 'Shopname'.)".
            // === STAGING SNAPSHOT === Build the full new period's snapshot
            // in tempdb (#NewSnap) instead of writing directly to
            // dbo.LPM_SimItemSkuMax. The 7 override rules below operate on
            // #NewSnap; only the FINAL delta (changed rows only) lands on
            // the user table via the delta-apply phase at the very end.
            // This pattern (delta MERGE) means rebuilding the same period
            // with unchanged inputs costs ~30s instead of ~12 minutes —
            // only rows whose (SKUMax, WHBoxQty, VolumeGroup, DivCode)
            // actually changed get touched.
            //
            // #NewSnap has only a clustered index — none of the 3 NCIs
            // that dbo.LPM_SimItemSkuMax carries. INSERT into a temp table
            // with a single index is ~4× faster than INSERT into the
            // production table, which is the second source of speedup
            // (combined with the NCI drop/recreate during the delta-apply
            // phase, Option A).
            //
            // The legacy DELETE of dbo.LPM_SimItemSkuMax rows for the
            // period is REMOVED here — the delta-apply phase handles
            // DELETE/UPDATE/INSERT against the target authoritatively
            // based on what's in #NewSnap.
            ins.CommandText = @"
                -- SET NOCOUNT ON suppresses the per-statement 'X rows
                -- affected' info messages that DDL (CREATE INDEX) and DML
                -- (SELECT INTO) otherwise stream back to SqlClient as
                -- separate notifications. Without this, the SqlClient
                -- reader receives multiple 'result' frames and the
                -- subsequent SqlCommand on the same connection occasionally
                -- sees a session reset that drops session-scoped temp
                -- tables — the cause of the 1.9.3/1.9.4 'Invalid object
                -- name #NewSnap' regression. Matches the pattern used by
                -- the other temp-table commands in this method.
                --
                -- No BEGIN TRAN: SELECT INTO is atomic on its own, and
                -- wrapping in BEGIN TRAN/COMMIT has its own subtle temp-
                -- table-persistence issues across SqlCommand boundaries.
                SET NOCOUNT ON;

                IF OBJECT_ID('tempdb..#SohLookup') IS NOT NULL DROP TABLE #SohLookup;

                -- 1.14.35 — Per-(Store, Item) SOH from racks.dbo.LPM_LocStock for
                -- this country. Mirrors the canonical SOH read at the top of
                -- LpmSimGenerator.GenerateAsync: DataSettings.SIMCountry filter
                -- (bulletproof against empty / mis-tagged LocStock.Country),
                -- SUM across multiple LocStock rows for the same (Store, Item).
                -- LEFT-joined into the staging insert below so items not in
                -- LocStock get SOH=0, matching the same default the allocator
                -- uses.
                SELECT ls.StoreID,
                       ls.Itemcode,
                       SOH = SUM(CAST(ISNULL(ls.SOH, 0) AS bigint))
                  INTO #SohLookup
                  FROM racks.dbo.LPM_LocStock ls
                  INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
                 WHERE ds.SIMCountry = @country
                   AND ls.StoreID  IS NOT NULL AND ls.StoreID  <> ''
                   AND ls.Itemcode IS NOT NULL AND ls.Itemcode <> ''
                 GROUP BY ls.StoreID, ls.Itemcode;

                CREATE CLUSTERED INDEX IX_SohLookup ON #SohLookup (StoreID, Itemcode);

                -- 1.14.43 — Persistent staging table replaces the old
                -- session-scoped #NewSnap. SqlClient connection pooling
                -- occasionally triggers a session reset between this
                -- SqlCommand and the exclusions SqlCommand, which would
                -- drop #NewSnap mid-build (the documented intermittent
                -- 'Invalid object name #NewSnap' bug). The persistent
                -- table survives session resets, so the exclusions and
                -- delta-apply phases that read it always find data.
                --
                -- Clear THIS country's prior staging rows. Other
                -- countries' rows (if any) are left alone so concurrent
                -- builds for different countries don't collide.
                DELETE FROM dbo.LPM_SimItemSkuMax_Staging
                 WHERE Country = @country;

                INSERT INTO dbo.LPM_SimItemSkuMax_Staging
                       (Country, StoreID, ItemCode, Season, DivCode, WHBoxQty,
                        VolumeGroup, SKUMax, SOH)
                SELECT
                    @country, s.StoreID, iw.ItemCode, iw.Season,
                    iw.DivCode, iw.WHBoxQty, s.VolumeGroup,
                    CAST(ISNULL(r.SKUMax, 0) AS int),
                    -- Clamp at int range (SOH can theoretically exceed int if
                    -- summed across many sub-locations, but realistically per
                    -- (Store, Item) it stays small). Falls back to 0 for
                    -- items not in LocStock for this country.
                    CAST(ISNULL(sl.SOH, 0) AS int)
                  FROM #ItemWh iw
                  INNER JOIN #Stores s
                          ON s.DivCode = iw.DivCode
                  OUTER APPLY (
                      SELECT TOP 1 r2.SKUMax
                        FROM #Rules r2
                       WHERE r2.DivCode    = iw.DivCode
                         AND r2.GroupCode  = s.VolumeGroup
                         AND iw.WHBoxQty BETWEEN r2.WHStockFrom AND r2.WHStockTo
                       ORDER BY r2.WHStockFrom
                  ) r
                  LEFT JOIN #SohLookup sl
                         ON sl.StoreID  = s.StoreID
                        AND sl.Itemcode = iw.ItemCode;

                DECLARE @rc bigint = @@ROWCOUNT;

                -- Clustered index already present on the staging table as
                -- PK_LPM_SimItemSkuMax_Staging (Country, StoreID, ItemCode,
                -- Season). No CREATE INDEX needed here.

                SELECT @rc AS Rc;";
            // 1.14.35 — @country needed for the #SohLookup CTE (filter by
            // DataSettings.SIMCountry, same pattern as the canonical SOH read
            // at the top of GenerateAsync).
            ins.Parameters.Add(new SqlParameter("@country", country));
            ins.CommandTimeout = 1800;  // 30 min ceiling
            // ExecuteScalarAsync (not ExecuteReaderAsync) — single value
            // return + immediate result-set drain matches the pattern used
            // by every other count-returning command in this file. The
            // ExecuteReaderAsync + manual `using var rdr` pattern that
            // 1.9.3/1.9.4 used was leaving the connection in a state that
            // intermittently lost #NewSnap on the next SqlCommand.
            var stageRc = await ins.ExecuteScalarAsync(ct);
            inserted = stageRc is null || stageRc == DBNull.Value
                ? 0L
                : Convert.ToInt64(stageRc);
        }

        // === EXCLUSION RULES === best-effort, non-fatal.
        // Each rule joins to an external table (usa.dbo.ExcludeExport_Planning,
        // ExcludeSubclass, ExcludeItemsMFCS, bfldata.dbo.RemoveItemsFromTransfer)
        // whose column names vary across deployments. Wrapping the entire
        // exclusion phase in a separate batch + try/catch means a missing
        // column on any one of these tables only loses the exclusions —
        // the snapshot rebuild stays intact.
        string? exclusionWarning = null;
        progress?.Report("Applying exclusion rules…");
        try
        {
            using (var excl = conn.CreateCommand())
            {
                excl.CommandText = @"
                    -- XACT_ABORT must be OFF so per-rule TRY/CATCH can swallow
                    -- a single rule's failure (column-name drift, linked-server
                    -- timeout, missing source table, etc.) without taking the
                    -- other 6 rules down with it. With XACT_ABORT ON, severe
                    -- errors auto-terminate the batch despite TRY/CATCH.
                    -- The apply phase (audit + UPDATE) flips XACT_ABORT back ON
                    -- so the audit/UPDATE pair stays atomic.
                    SET XACT_ABORT OFF;
                    DECLARE @excluded        bigint = 0;
                    DECLARE @priceCapped     bigint = 0;
                    DECLARE @deactivated     bigint = 0;
                    DECLARE @deptDeactivated bigint = 0;
                    DECLARE @t0 datetime2(3);
                    DECLARE @ms1 int = 0, @ms2 int = 0, @ms3 int = 0, @ms4 int = 0,
                            @ms5 int = 0, @ms6 int = 0, @ms7 int = 0;
                    -- 1.14.21: per-rule rowcount + reusable @msg buffer for
                    -- mid-batch progress messages. RAISERROR(@msg, 0, 1) WITH
                    -- NOWAIT fires the InfoMessage event on the client BEFORE
                    -- the batch finishes — so a 5-min build can stream
                    -- Rule-N completion messages (e.g. 35200ms, 1250 matches)
                    -- to the user while subsequent rules are still running.
                    DECLARE @msg nvarchar(400);
                    DECLARE @r1Rows int = 0, @r2Rows int = 0, @r3Rows int = 0, @r4Rows int = 0,
                            @r5Rows int = 0, @r6Rows int = 0, @r7Rows int = 0;
                    -- Per-rule error capture — TRY/CATCH inside each rule writes
                    -- ERROR_MESSAGE() here. Returned to C# alongside the counts
                    -- so the build banner can show e.g. 'R5 SKIPPED (Hodata
                    -- access denied)' while still applying rules 1-4, 6, 7.
                    DECLARE @r1Error nvarchar(2000) = NULL,
                            @r2Error nvarchar(2000) = NULL,
                            @r3Error nvarchar(2000) = NULL,
                            @r4Error nvarchar(2000) = NULL,
                            @r5Error nvarchar(2000) = NULL,
                            @r6Error nvarchar(2000) = NULL,
                            @r7Error nvarchar(2000) = NULL;

                    -- ---------- PHASE 1: Setup (no transaction) ----------
                    -- Snapshot + temp tables. Errors here are fatal — the whole
                    -- exclusions phase is meaningless without #SkuSnap.
                    IF OBJECT_ID('tempdb..#SkuSnap')          IS NOT NULL DROP TABLE #SkuSnap;
                    IF OBJECT_ID('tempdb..#ExcludeMatches')   IS NOT NULL DROP TABLE #ExcludeMatches;
                    IF OBJECT_ID('tempdb..#PriceCapMatches')  IS NOT NULL DROP TABLE #PriceCapMatches;
                    IF OBJECT_ID('tempdb..#DeactMatches')     IS NOT NULL DROP TABLE #DeactMatches;
                    IF OBJECT_ID('tempdb..#DeptDeactMatches') IS NOT NULL DROP TABLE #DeptDeactMatches;

                    -- #SkuSnap now sources from the in-memory staging
                    -- (#NewSnap) instead of dbo.LPM_SimItemSkuMax. The
                    -- delta-apply phase at the end of this method writes
                    -- #NewSnap's final (post-override) values to the user
                    -- table; the rules below operate ON the staging and
                    -- modify SKUMax there, NOT directly on the target.
                    -- Faster (temp-to-temp) and lets us track per-rule
                    -- audit + per-row diff in a single coordinated pass.
                    --
                    -- 1.14.8: Shopname column added via OUTER APPLY to
                    -- DataSettings. LPM's StoreID is hyphenated
                    -- (BFL-DXD, LFL-MCT) while every legacy exclusion
                    -- table uses concatenated Shopname (BFLAVENUES,
                    -- EX2KUWAIT). DataSettings.(StoreID, Shopname)
                    -- bridges the two — pre-resolving here once means
                    -- every rule below joins on snap.Shopname directly
                    -- instead of repeating the lookup.
                    SELECT n.StoreID, n.ItemCode, n.Season, n.DivCode, n.SKUMax AS OrigSku,
                           ds.Shopname
                      INTO #SkuSnap
                      FROM dbo.LPM_SimItemSkuMax_Staging n
                      OUTER APPLY (
                          SELECT TOP 1 Shopname
                            FROM dbo.DataSettings d
                           WHERE d.StoreID = n.StoreID
                             AND (d.SIMCountry = @country OR d.SIMCountry IS NULL)
                           ORDER BY CASE WHEN d.SIMCountry = @country THEN 0 ELSE 1 END
                      ) ds
                     WHERE n.Country = @country AND n.SKUMax > 0;
                    CREATE CLUSTERED INDEX IX_SkuSnap ON #SkuSnap (StoreID, ItemCode);

                    CREATE TABLE #ExcludeMatches (
                        StoreID     varchar(25)  NOT NULL,
                        ItemCode    varchar(30)  NOT NULL,
                        Season      char(1)      NULL,
                        DivCode     int          NULL,
                        OrigSku     int          NOT NULL,
                        SourceTable varchar(120) NOT NULL,
                        Reason      varchar(160) NOT NULL,
                        MatchedKey  varchar(120) NULL
                    );

                    CREATE TABLE #PriceCapMatches (
                        StoreID     varchar(25)  NOT NULL,
                        ItemCode    varchar(30)  NOT NULL,
                        Season      char(1)      NULL,
                        DivCode     int          NULL,
                        OrigSku     int          NOT NULL,
                        NewSku      int          NOT NULL,
                        MatchedKey  varchar(160) NULL
                    );

                    CREATE TABLE #DeactMatches (
                        StoreID     varchar(25)  NOT NULL,
                        ItemCode    varchar(30)  NOT NULL,
                        Season      char(1)      NULL,
                        DivCode     int          NULL,
                        OrigSku     int          NOT NULL,
                        MatchedKey  varchar(120) NULL
                    );

                    CREATE TABLE #DeptDeactMatches (
                        StoreID     varchar(25)  NOT NULL,
                        ItemCode    varchar(30)  NOT NULL,
                        Season      char(1)      NULL,
                        DivCode     int          NULL,
                        OrigSku     int          NOT NULL,
                        MatchedKey  varchar(160) NULL
                    );

                    -- ---------- PHASE 2: Rules (per-rule TRY/CATCH) ----------
                    -- Each rule's INSERT runs in its own TRY block. A failure
                    -- (column-name drift, missing linked server, schema change
                    -- in an external DB) sets the rule's @rNError and leaves
                    -- the rule's match table empty. The other 6 rules still
                    -- run on the same #SkuSnap.
                    --
                    -- INSERT is atomic per statement in SQL Server — a rule that
                    -- fails leaves zero rows in its match table (not partial),
                    -- so there's no risk of half-applied rules in the apply
                    -- phase below.

                    -- Rule 1 — usa.dbo.ExcludeExport_Planning.
                    -- 1.14.8: snap.Shopname (DataSettings-bridged) joins ep.Shopname,
                    -- not snap.StoreID (hyphenated 'BFL-DXD' vs concatenated 'BFLAVENUES').
                    -- 1.14.19: business rules layered on top of the join:
                    --   • Only rows with Active = 'Y' contribute.
                    --   • Duration = 'Temporary' ⇒ today must fall between
                    --     BlockFrom and BlockTo (inclusive, date-only compare so
                    --     time-of-day on smalldatetime doesn't trip us up).
                    --     Anything else (Permanent / empty / NULL) ⇒ no date check.
                    --   • ItemCode set ⇒ block that specific item (rule 1a).
                    --   • GroupCode set + ItemCode empty ⇒ block every item whose
                    --     Hodata.dbo.ItemMaster.GroupCode matches (rule 1b).
                    --   • Business convention: a row has ItemCode XOR GroupCode,
                    --     never both. 1b is gated on ItemCode being empty so that
                    --     if a row ever does carry both, the item-level rule wins
                    --     (item-level is more specific).
                    --   • NULL BlockFrom or NULL BlockTo on a Temporary row ⇒
                    --     BETWEEN evaluates UNKNOWN ⇒ block does not apply (fail-safe).
                    RAISERROR(N'Rule 1 of 7 (usa.dbo.ExcludeExport_Planning)…', 0, 1) WITH NOWAIT;
                    SET @t0 = SYSDATETIME();
                    BEGIN TRY
                        ;WITH ActiveExcludes AS (
                            SELECT ep.Shopname, ep.ItemCode, ep.GroupCode
                              FROM usa.dbo.ExcludeExport_Planning ep
                             WHERE UPPER(ISNULL(ep.Active, '')) = 'Y'
                               AND ( UPPER(ISNULL(ep.Duration, '')) <> 'TEMPORARY'
                                  OR (    UPPER(ISNULL(ep.Duration, '')) = 'TEMPORARY'
                                      AND CAST(GETDATE() AS date)
                                          BETWEEN CAST(ep.BlockFrom AS date)
                                              AND CAST(ep.BlockTo   AS date)) )
                        )
                        INSERT INTO #ExcludeMatches
                               (StoreID, ItemCode, Season, DivCode, OrigSku, SourceTable, Reason, MatchedKey)
                        -- 1a: item-level block (row carries a non-empty ItemCode)
                        SELECT snap.StoreID, snap.ItemCode, snap.Season, snap.DivCode, snap.OrigSku,
                               'usa.dbo.ExcludeExport_Planning',
                               'Active item-level block (Shopname x ItemCode)',
                               NULL
                          FROM #SkuSnap snap
                          INNER JOIN ActiveExcludes ae
                                  ON ae.Shopname = snap.Shopname
                                 AND ae.ItemCode = snap.ItemCode
                         WHERE snap.Shopname IS NOT NULL
                           AND LTRIM(RTRIM(ISNULL(ae.ItemCode, ''))) <> ''

                        UNION ALL

                        -- 1b: group-level block — row carries non-empty GroupCode
                        -- with empty ItemCode; items resolved via Hodata.dbo.ItemMaster.
                        SELECT snap.StoreID, snap.ItemCode, snap.Season, snap.DivCode, snap.OrigSku,
                               'usa.dbo.ExcludeExport_Planning',
                               'Active group-level block (Shopname x GroupCode via Hodata.dbo.ItemMaster)',
                               CONCAT('GroupCode=', ae.GroupCode)
                          FROM #SkuSnap snap
                          INNER JOIN ActiveExcludes ae
                                  ON ae.Shopname = snap.Shopname
                          INNER JOIN Hodata.dbo.ItemMaster im
                                  ON im.GroupCode = ae.GroupCode
                                 AND im.ItemCode  = snap.ItemCode
                         WHERE snap.Shopname IS NOT NULL
                           AND LTRIM(RTRIM(ISNULL(ae.GroupCode, ''))) <> ''
                           AND LTRIM(RTRIM(ISNULL(ae.ItemCode,  ''))) =  '';
                        SET @r1Rows = @@ROWCOUNT;
                    END TRY
                    BEGIN CATCH SET @r1Error = ERROR_MESSAGE(); END CATCH;
                    SET @ms1 = DATEDIFF(MILLISECOND, @t0, SYSDATETIME());
                    SET @msg = CONCAT(N'  ↳ Rule 1 done in ', @ms1, N'ms (', @r1Rows, N' matches)',
                        CASE WHEN @r1Error IS NOT NULL THEN N' — FAILED: ' + LEFT(@r1Error, 120) ELSE N'' END);
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;

                    -- Rule 2 — usa.dbo.ExcludeSubclass (Shop x MH4ID; Inactive='N')
                    -- ExcludeSubclass uses the legacy column name Shop (NOT
                    -- Shopname like the other 3 exclusion tables). Verified
                    -- live schema: USA.dbo.ExcludeSubclass(Subclass, Shop,
                    -- Trndate, Remarks, UserId, Inactive, MH4ID).
                    -- 1.14.8: now joins es.Shop = snap.Shopname (the
                    -- DataSettings-bridged value), was snap.StoreID which
                    -- never matched. MH4ID join unchanged.
                    RAISERROR(N'Rule 2 of 7 (usa.dbo.ExcludeSubclass)…', 0, 1) WITH NOWAIT;
                    SET @t0 = SYSDATETIME();
                    BEGIN TRY
                        INSERT INTO #ExcludeMatches
                               (StoreID, ItemCode, Season, DivCode, OrigSku, SourceTable, Reason, MatchedKey)
                        SELECT snap.StoreID, snap.ItemCode, snap.Season, snap.DivCode, snap.OrigSku,
                               'usa.dbo.ExcludeSubclass',
                               'Shop x MH4ID active in ExcludeSubclass (Inactive=N)',
                               CONCAT('MH4ID=', us.MH4ID)
                          FROM #SkuSnap snap
                          INNER JOIN Datareporting.dbo.upc_subclass us
                                  ON us.itemcode = snap.ItemCode
                          INNER JOIN usa.dbo.ExcludeSubclass es
                                  ON es.Shop  = snap.Shopname
                                 AND es.mh4id = us.MH4ID
                         WHERE es.Inactive = 'N'
                           AND snap.Shopname IS NOT NULL;
                        SET @r2Rows = @@ROWCOUNT;
                    END TRY
                    BEGIN CATCH SET @r2Error = ERROR_MESSAGE(); END CATCH;
                    SET @ms2 = DATEDIFF(MILLISECOND, @t0, SYSDATETIME());
                    SET @msg = CONCAT(N'  ↳ Rule 2 done in ', @ms2, N'ms (', @r2Rows, N' matches)',
                        CASE WHEN @r2Error IS NOT NULL THEN N' — FAILED: ' + LEFT(@r2Error, 120) ELSE N'' END);
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;

                    -- Rule 3 — bfldata.dbo.RemoveItemsFromTransfer (Itemcode x Shopname; trndate >= 2025-09-01)
                    -- 1.14.8: was joining rt.shopname = snap.StoreID; flipped
                    -- to snap.Shopname (DataSettings-bridged).
                    RAISERROR(N'Rule 3 of 7 (bfldata.dbo.RemoveItemsFromTransfer)…', 0, 1) WITH NOWAIT;
                    SET @t0 = SYSDATETIME();
                    BEGIN TRY
                        INSERT INTO #ExcludeMatches
                               (StoreID, ItemCode, Season, DivCode, OrigSku, SourceTable, Reason, MatchedKey)
                        SELECT snap.StoreID, snap.ItemCode, snap.Season, snap.DivCode, snap.OrigSku,
                               'bfldata.dbo.RemoveItemsFromTransfer',
                               'Item x Shopname removed from transfer since 2025-09-01',
                               NULL
                          FROM #SkuSnap snap
                          INNER JOIN bfldata.dbo.RemoveItemsFromTransfer rt
                                  ON rt.itemcode = snap.ItemCode
                                 AND rt.shopname = snap.Shopname
                         WHERE rt.trndate >= '2025-09-01'
                           AND snap.Shopname IS NOT NULL;
                        SET @r3Rows = @@ROWCOUNT;
                    END TRY
                    BEGIN CATCH SET @r3Error = ERROR_MESSAGE(); END CATCH;
                    SET @ms3 = DATEDIFF(MILLISECOND, @t0, SYSDATETIME());
                    SET @msg = CONCAT(N'  ↳ Rule 3 done in ', @ms3, N'ms (', @r3Rows, N' matches)',
                        CASE WHEN @r3Error IS NOT NULL THEN N' — FAILED: ' + LEFT(@r3Error, 120) ELSE N'' END);
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;

                    -- Rule 4 — usa.dbo.ExcludeItemsMFCS (HSCode x Shopname).
                    --
                    -- 1.14.8: reverted to the HSCode-based join per the
                    -- original migration spec (034_lpm_sim_skumax_exclusions.sql).
                    -- A previous refactor switched to a direct Itemcode
                    -- join for perf, but production data shows
                    -- ExcludeItemsMFCS is keyed by HSCode — the Itemcode
                    -- column on the table is sparse / not the canonical
                    -- match field. Without the HSCode lookup, Rule 4
                    -- produced 0 audit rows on every build.
                    --
                    -- Perf concern: the previous HSCode implementation
                    -- joined #SkuSnap × all of upcbarcodes (18M rows) and
                    -- hung for 15+ min. Avoided here by pre-filtering
                    -- upcbarcodes to ONLY the HSCodes that appear in
                    -- ExcludeItemsMFCS (≤198 rows). The relevant upc
                    -- subset is normally tiny, so the SkuSnap × upcbarcodes
                    -- multiplication never blows up.
                    RAISERROR(N'Rule 4 of 7 (usa.dbo.ExcludeItemsMFCS via upcbarcodes)…', 0, 1) WITH NOWAIT;
                    SET @t0 = SYSDATETIME();
                    BEGIN TRY
                        ;WITH ExclHSCodes AS (
                            SELECT DISTINCT HSCode
                              FROM usa.dbo.ExcludeItemsMFCS
                             WHERE HSCode IS NOT NULL AND LTRIM(RTRIM(HSCode)) <> ''
                        ),
                        RelevantUPC AS (
                            -- Pre-filter upcbarcodes to the (itemcode, HSCode)
                            -- pairs whose HSCode is actually in the exclusion
                            -- table. Keeps the upcbarcodes side tiny.
                            SELECT DISTINCT b.itemcode, b.HSCode
                              FROM usa.dbo.upcbarcodes b
                              INNER JOIN ExclHSCodes hs ON hs.HSCode = b.HSCode
                        )
                        INSERT INTO #ExcludeMatches
                               (StoreID, ItemCode, Season, DivCode, OrigSku, SourceTable, Reason, MatchedKey)
                        SELECT snap.StoreID, snap.ItemCode, snap.Season, snap.DivCode, snap.OrigSku,
                               'usa.dbo.ExcludeItemsMFCS',
                               'HSCode x Shopname excluded in ExcludeItemsMFCS',
                               CONCAT('HSCode=', ru.HSCode)
                          FROM #SkuSnap snap
                          INNER JOIN RelevantUPC ru ON ru.itemcode = snap.ItemCode
                          INNER JOIN usa.dbo.ExcludeItemsMFCS e
                                  ON e.HSCode   = ru.HSCode
                                 AND e.Shopname = snap.Shopname
                         WHERE snap.Shopname IS NOT NULL;
                        SET @r4Rows = @@ROWCOUNT;
                    END TRY
                    BEGIN CATCH SET @r4Error = ERROR_MESSAGE(); END CATCH;
                    SET @ms4 = DATEDIFF(MILLISECOND, @t0, SYSDATETIME());
                    SET @msg = CONCAT(N'  ↳ Rule 4 done in ', @ms4, N'ms (', @r4Rows, N' matches)',
                        CASE WHEN @r4Error IS NOT NULL THEN N' — FAILED: ' + LEFT(@r4Error, 120) ELSE N'' END);
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;

                    -- Rule 5 — usa.dbo.DeptPriceMaxQty_MH4 (price-band cap → REPLACE)
                    -- Joins Hodata.dbo.SalesPrice (latest SalesRate per item for
                    -- CostCode='001') + subclassmaster (Department) + Division.
                    -- Most external joins of any rule, so the most likely to
                    -- fail when a downstream DB is unavailable.
                    -- 1.14.10 perf: previous implementation was ~14 minutes
                    -- because (a) Hodata.dbo.SalesPrice was scanned twice (once
                    -- in LatestPriceDt, once in ItemPrice) over its full size,
                    -- and (b) ItemAttr/ItemPrice CTEs got expanded against the
                    -- 15.7M-row #SkuSnap rather than being pre-aggregated.
                    -- Materialised into indexed temp tables filtered to ONLY
                    -- items in #SkuSnap, with a single ROW_NUMBER() pass for
                    -- the latest price per item. Expected: 14m → 1-3m.
                    RAISERROR(N'Rule 5 of 7 (usa.dbo.DeptPriceMaxQty_MH4 + Hodata.SalesPrice)…', 0, 1) WITH NOWAIT;
                    SET @t0 = SYSDATETIME();
                    BEGIN TRY
                        IF OBJECT_ID('tempdb..#SkuItemsR5')  IS NOT NULL DROP TABLE #SkuItemsR5;
                        IF OBJECT_ID('tempdb..#ItemAttrR5')  IS NOT NULL DROP TABLE #ItemAttrR5;
                        IF OBJECT_ID('tempdb..#ItemPriceR5') IS NOT NULL DROP TABLE #ItemPriceR5;

                        -- (1) Distinct items in #SkuSnap — the universe Rule 5
                        --     needs lookups for. Anything outside this set can
                        --     be safely ignored from the heavy joins below.
                        SELECT DISTINCT ItemCode
                          INTO #SkuItemsR5
                          FROM #SkuSnap;
                        CREATE CLUSTERED INDEX IX_SkuItemsR5 ON #SkuItemsR5 (ItemCode);

                        -- (2) Item Div + Dept attributes — same logic as the
                        --     old ItemAttr CTE, but joined to #SkuItemsR5 so
                        --     upc_subclass × subclassmaster × Division only
                        --     processes the items we'll actually use.
                        SELECT u.itemcode AS ItemCode,
                               MIN(d.DivCode)      AS DivCode,
                               MAX(sm.Department)  AS Department
                          INTO #ItemAttrR5
                          FROM #SkuItemsR5 si
                          INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = si.ItemCode
                          INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID  = u.MH4ID
                          INNER JOIN dbo.Division                     d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                         GROUP BY u.itemcode;
                        CREATE CLUSTERED INDEX IX_ItemAttrR5 ON #ItemAttrR5 (ItemCode);

                        -- (3) Latest price per item — one pass of SalesPrice
                        --     with ROW_NUMBER() instead of the old two-pass
                        --     MAX-then-rejoin pattern. Pre-filtered to
                        --     #SkuItemsR5 so the SalesPrice scan only touches
                        --     rows we care about.
                        SELECT ItemCode, Price
                          INTO #ItemPriceR5
                          FROM (
                              SELECT sp.ItemCode,
                                     CAST(sp.SalesRate AS int) AS Price,
                                     ROW_NUMBER() OVER (PARTITION BY sp.ItemCode ORDER BY sp.TrnDate DESC) AS rn
                                FROM Hodata.dbo.SalesPrice sp
                                INNER JOIN #SkuItemsR5 si ON si.ItemCode = sp.ItemCode
                               WHERE sp.CostCode = '001'
                          ) x
                         WHERE x.rn = 1;
                        CREATE CLUSTERED INDEX IX_ItemPriceR5 ON #ItemPriceR5 (ItemCode);

                        -- (4) Main rule body — now joins against tiny indexed
                        --     temp tables instead of CTE expansions.
                        INSERT INTO #PriceCapMatches
                               (StoreID, ItemCode, Season, DivCode, OrigSku, NewSku, MatchedKey)
                        SELECT snap.StoreID, snap.ItemCode, snap.Season, snap.DivCode, snap.OrigSku,
                               bnd.maxqty,
                               CONCAT('Div=', ia.DivCode,
                                      ' / Dept=', ia.Department,
                                      ' / Price=', ip.Price,
                                      ' (band ', bnd.PriceF, '-', bnd.PriceT, ')',
                                      ' / maxqty=', bnd.maxqty)
                          FROM #SkuSnap snap
                          INNER JOIN #ItemAttrR5  ia ON ia.ItemCode = snap.ItemCode
                          INNER JOIN #ItemPriceR5 ip ON ip.ItemCode = snap.ItemCode
                          CROSS APPLY (
                              -- 1.14.8: was dp.shopname = snap.StoreID;
                              -- flipped to snap.Shopname (DataSettings-bridged).
                              SELECT TOP 1 dp.maxqty, dp.PriceF, dp.PriceT
                                FROM usa.dbo.DeptPriceMaxQty_MH4 dp
                               WHERE dp.shopname   = snap.Shopname
                                 AND dp.DivCode    = ia.DivCode
                                 AND dp.DEPARTMENT = ia.Department
                                 AND ip.Price BETWEEN dp.PriceF AND dp.PriceT
                               ORDER BY dp.PriceF
                          ) bnd
                         WHERE snap.Shopname IS NOT NULL;
                        SET @r5Rows = @@ROWCOUNT;
                    END TRY
                    BEGIN CATCH SET @r5Error = ERROR_MESSAGE(); END CATCH;
                    SET @ms5 = DATEDIFF(MILLISECOND, @t0, SYSDATETIME());
                    SET @msg = CONCAT(N'  ↳ Rule 5 done in ', @ms5, N'ms (', @r5Rows, N' price-capped)',
                        CASE WHEN @r5Error IS NOT NULL THEN N' — FAILED: ' + LEFT(@r5Error, 120) ELSE N'' END);
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;

                    -- Rule 6 — dbo.LPM_StoreDivAccess (deactivation; #Deact already prebuilt)
                    RAISERROR(N'Rule 6 of 7 (dbo.LPM_StoreDivAccess)…', 0, 1) WITH NOWAIT;
                    SET @t0 = SYSDATETIME();
                    BEGIN TRY
                        INSERT INTO #DeactMatches
                               (StoreID, ItemCode, Season, DivCode, OrigSku, MatchedKey)
                        SELECT snap.StoreID, snap.ItemCode, snap.Season, snap.DivCode, snap.OrigSku,
                               CONCAT('Div=', snap.DivCode)
                          FROM #SkuSnap snap
                          INNER JOIN #Deact d
                                  ON d.StoreID = snap.StoreID
                                 AND d.DivCode = snap.DivCode;
                        SET @r6Rows = @@ROWCOUNT;
                    END TRY
                    BEGIN CATCH SET @r6Error = ERROR_MESSAGE(); END CATCH;
                    SET @ms6 = DATEDIFF(MILLISECOND, @t0, SYSDATETIME());
                    SET @msg = CONCAT(N'  ↳ Rule 6 done in ', @ms6, N'ms (', @r6Rows, N' div-deactivated)',
                        CASE WHEN @r6Error IS NOT NULL THEN N' — FAILED: ' + LEFT(@r6Error, 120) ELSE N'' END);
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;

                    -- Rule 7 — dbo.LPM_StoreDeptAccess (Store × Department deactivation)
                    RAISERROR(N'Rule 7 of 7 (dbo.LPM_StoreDeptAccess)…', 0, 1) WITH NOWAIT;
                    SET @t0 = SYSDATETIME();
                    BEGIN TRY
                        WITH ItemDept AS (
                            SELECT u.itemcode,
                                   MAX(sm.Department) AS Department
                              FROM Datareporting.dbo.upc_subclass    u
                              INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                             WHERE sm.Department IS NOT NULL
                               AND LTRIM(RTRIM(sm.Department)) <> ''
                             GROUP BY u.itemcode
                        )
                        INSERT INTO #DeptDeactMatches
                               (StoreID, ItemCode, Season, DivCode, OrigSku, MatchedKey)
                        SELECT snap.StoreID, snap.ItemCode, snap.Season, snap.DivCode, snap.OrigSku,
                               CONCAT('Div=', snap.DivCode, ' / Dept=', id.Department)
                          FROM #SkuSnap snap
                          INNER JOIN ItemDept id ON id.itemcode = snap.ItemCode
                          INNER JOIN dbo.LPM_StoreDeptAccess sda
                                  ON sda.Country    = @country
                                 AND sda.StoreID    = snap.StoreID
                                 AND sda.DivCode    = snap.DivCode
                                 AND sda.Department = id.Department
                                 AND sda.IsActive   = 0;
                        SET @r7Rows = @@ROWCOUNT;
                    END TRY
                    BEGIN CATCH SET @r7Error = ERROR_MESSAGE(); END CATCH;
                    SET @ms7 = DATEDIFF(MILLISECOND, @t0, SYSDATETIME());
                    SET @msg = CONCAT(N'  ↳ Rule 7 done in ', @ms7, N'ms (', @r7Rows, N' dept-deactivated)',
                        CASE WHEN @r7Error IS NOT NULL THEN N' — FAILED: ' + LEFT(@r7Error, 120) ELSE N'' END);
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    RAISERROR(N'Rules complete — applying audit + UPDATE…', 0, 1) WITH NOWAIT;

                    -- ---------- PHASE 3: Apply (atomic) ----------
                    -- Audit + UPDATE wrapped in a single transaction. If this
                    -- fails, ROLLBACK preserves the prior audit + LPM_SimItemSkuMax
                    -- state (the prior build's overrides survive). The DELETE
                    -- of the period's audit rows is INSIDE the transaction so
                    -- a failed apply doesn't leave the table empty.

                    -- 1.14.32 — pre-materialise three lookup temp tables that
                    -- enrich the audit INSERTs with DivisionName / Brand /
                    -- GroupCode for the planner-facing report (columns added
                    -- in migration 048). Done ONCE before the transaction:
                    --   • #DivLookup  — DivCode → Division.Name (small)
                    --   • #BrandLookup — ItemCode → upcbarcodes.Vendor (TOP 1
                    --                    per item because upcbarcodes can
                    --                    have multiple rows per item; we
                    --                    pre-filter to items in the 4 match
                    --                    tables so the 18M-row scan stays
                    --                    cheap — same pattern as 1.14.10's
                    --                    Rule 5 perf fix).
                    --   • #GrpLookup  — ItemCode → ItemMaster.GroupCode
                    -- All three are LEFT-JOINed into the 4 INSERTs below so
                    -- rows with no lookup match still land in the audit (the
                    -- new column is just NULL for those).
                    IF OBJECT_ID('tempdb..#DivLookup')   IS NOT NULL DROP TABLE #DivLookup;
                    IF OBJECT_ID('tempdb..#BrandLookup') IS NOT NULL DROP TABLE #BrandLookup;
                    IF OBJECT_ID('tempdb..#GrpLookup')   IS NOT NULL DROP TABLE #GrpLookup;

                    SELECT d.DivCode, CAST(d.Division AS varchar(80)) AS DivisionName
                      INTO #DivLookup
                      FROM dbo.Division d;
                    CREATE CLUSTERED INDEX IX_DivLookup ON #DivLookup (DivCode);

                    -- Distinct items across all 4 match tables — keeps the
                    -- upcbarcodes / ItemMaster scans tight.
                    IF OBJECT_ID('tempdb..#AuditItems') IS NOT NULL DROP TABLE #AuditItems;
                    SELECT DISTINCT ItemCode
                      INTO #AuditItems
                      FROM (
                          SELECT ItemCode FROM #ExcludeMatches
                          UNION ALL SELECT ItemCode FROM #PriceCapMatches
                          UNION ALL SELECT ItemCode FROM #DeactMatches
                          UNION ALL SELECT ItemCode FROM #DeptDeactMatches
                      ) u
                     WHERE ItemCode IS NOT NULL;
                    CREATE CLUSTERED INDEX IX_AuditItems ON #AuditItems (ItemCode);

                    -- Brand from usa.dbo.upcbarcodes.Vendor — TOP 1 per item
                    -- via ROW_NUMBER() so the brand assignment is stable
                    -- (deterministic order) when an item has multiple
                    -- barcode rows. Pre-filtered to items actually in the
                    -- audit set so we never scan all 18M rows.
                    SELECT ItemCode, Vendor AS Brand
                      INTO #BrandLookup
                      FROM (
                          SELECT b.itemcode AS ItemCode,
                                 CAST(b.Vendor AS varchar(80)) AS Vendor,
                                 ROW_NUMBER() OVER (PARTITION BY b.itemcode
                                                    ORDER BY b.Vendor) AS rn
                            FROM usa.dbo.upcbarcodes b
                            INNER JOIN #AuditItems ai ON ai.ItemCode = b.itemcode
                           WHERE b.Vendor IS NOT NULL
                             AND LTRIM(RTRIM(b.Vendor)) <> ''
                      ) x
                     WHERE x.rn = 1;
                    CREATE CLUSTERED INDEX IX_BrandLookup ON #BrandLookup (ItemCode);

                    -- GroupCode from Hodata.dbo.ItemMaster — TOP 1 per item
                    -- defensively, though ItemMaster typically has one row
                    -- per ItemCode.
                    SELECT ItemCode, GroupCode
                      INTO #GrpLookup
                      FROM (
                          SELECT im.ItemCode,
                                 CAST(im.GroupCode AS varchar(50)) AS GroupCode,
                                 ROW_NUMBER() OVER (PARTITION BY im.ItemCode
                                                    ORDER BY im.GroupCode) AS rn
                            FROM Hodata.dbo.ItemMaster im
                            INNER JOIN #AuditItems ai ON ai.ItemCode = im.ItemCode
                           WHERE im.GroupCode IS NOT NULL
                             AND LTRIM(RTRIM(im.GroupCode)) <> ''
                      ) x
                     WHERE x.rn = 1;
                    CREATE CLUSTERED INDEX IX_GrpLookup ON #GrpLookup (ItemCode);

                    SET XACT_ABORT ON;
                    BEGIN TRY
                        BEGIN TRAN;

                        DELETE FROM dbo.LPM_SimItemSkuMaxExcluded
                         WHERE Country = @country AND Year1 = @y AND Month1 = @m;

                        INSERT INTO dbo.LPM_SimItemSkuMaxExcluded
                               (Country, Year1, Month1, StoreID, ItemCode, Season, DivCode,
                                PriorSKUMax, SourceTable, Reason, MatchedKey, CreateTS,
                                DivisionName, Brand, GroupCode)
                        SELECT @country, @y, @m, em.StoreID, em.ItemCode, em.Season, em.DivCode,
                               em.OrigSku, em.SourceTable, em.Reason, em.MatchedKey, SYSDATETIME(),
                               dl.DivisionName, bl.Brand, gl.GroupCode
                          FROM #ExcludeMatches em
                          LEFT JOIN #DivLookup   dl ON dl.DivCode  = em.DivCode
                          LEFT JOIN #BrandLookup bl ON bl.ItemCode = em.ItemCode
                          LEFT JOIN #GrpLookup   gl ON gl.ItemCode = em.ItemCode;
                        SET @excluded = @@ROWCOUNT;

                        INSERT INTO dbo.LPM_SimItemSkuMaxExcluded
                               (Country, Year1, Month1, StoreID, ItemCode, Season, DivCode,
                                PriorSKUMax, SourceTable, Reason, MatchedKey, CreateTS,
                                DivisionName, Brand, GroupCode)
                        SELECT @country, @y, @m, pc.StoreID, pc.ItemCode, pc.Season, pc.DivCode,
                               pc.OrigSku,
                               'usa.dbo.DeptPriceMaxQty_MH4',
                               'Price-band cap from DeptPriceMaxQty_MH4 (REPLACE)',
                               pc.MatchedKey, SYSDATETIME(),
                               dl.DivisionName, bl.Brand, gl.GroupCode
                          FROM #PriceCapMatches pc
                          LEFT JOIN #DivLookup   dl ON dl.DivCode  = pc.DivCode
                          LEFT JOIN #BrandLookup bl ON bl.ItemCode = pc.ItemCode
                          LEFT JOIN #GrpLookup   gl ON gl.ItemCode = pc.ItemCode;
                        SET @priceCapped = @@ROWCOUNT;

                        INSERT INTO dbo.LPM_SimItemSkuMaxExcluded
                               (Country, Year1, Month1, StoreID, ItemCode, Season, DivCode,
                                PriorSKUMax, SourceTable, Reason, MatchedKey, CreateTS,
                                DivisionName, Brand, GroupCode)
                        SELECT @country, @y, @m, dm.StoreID, dm.ItemCode, dm.Season, dm.DivCode,
                               dm.OrigSku,
                               'dbo.LPM_StoreDivAccess',
                               'Store-Div deactivated in LPM_StoreDivAccess (IsActive=0)',
                               dm.MatchedKey, SYSDATETIME(),
                               dl.DivisionName, bl.Brand, gl.GroupCode
                          FROM #DeactMatches dm
                          LEFT JOIN #DivLookup   dl ON dl.DivCode  = dm.DivCode
                          LEFT JOIN #BrandLookup bl ON bl.ItemCode = dm.ItemCode
                          LEFT JOIN #GrpLookup   gl ON gl.ItemCode = dm.ItemCode;
                        SET @deactivated = @@ROWCOUNT;

                        INSERT INTO dbo.LPM_SimItemSkuMaxExcluded
                               (Country, Year1, Month1, StoreID, ItemCode, Season, DivCode,
                                PriorSKUMax, SourceTable, Reason, MatchedKey, CreateTS,
                                DivisionName, Brand, GroupCode)
                        SELECT @country, @y, @m, dd.StoreID, dd.ItemCode, dd.Season, dd.DivCode,
                               dd.OrigSku,
                               'dbo.LPM_StoreDeptAccess',
                               'Store-Dept deactivated in LPM_StoreDeptAccess (IsActive=0)',
                               dd.MatchedKey, SYSDATETIME(),
                               dl.DivisionName, bl.Brand, gl.GroupCode
                          FROM #DeptDeactMatches dd
                          LEFT JOIN #DivLookup   dl ON dl.DivCode  = dd.DivCode
                          LEFT JOIN #BrandLookup bl ON bl.ItemCode = dd.ItemCode
                          LEFT JOIN #GrpLookup   gl ON gl.ItemCode = dd.ItemCode;
                        SET @deptDeactivated = @@ROWCOUNT;

                        CREATE NONCLUSTERED INDEX IX_ExcludeMatches_SI    ON #ExcludeMatches    (StoreID, ItemCode);
                        CREATE NONCLUSTERED INDEX IX_PriceCapMatches_SI   ON #PriceCapMatches   (StoreID, ItemCode);
                        CREATE NONCLUSTERED INDEX IX_DeactMatches_SI      ON #DeactMatches      (StoreID, ItemCode);
                        CREATE NONCLUSTERED INDEX IX_DeptDeactMatches_SI  ON #DeptDeactMatches  (StoreID, ItemCode);

                        -- Apply price cap FIRST, zero-outs SECOND. When an item
                        -- matches both, zero wins — exclusions/deactivations
                        -- are absolute and trump price caps.
                        --
                        -- These UPDATEs target #NewSnap (the staging table)
                        -- instead of dbo.LPM_SimItemSkuMax. The delta-apply
                        -- phase that runs AFTER this transaction commits will
                        -- propagate the final post-override SKUMax values to
                        -- the user table via DELETE/UPDATE/INSERT (only rows
                        -- that actually changed get touched).
                        UPDATE n
                           SET n.SKUMax = pc.NewSku
                          FROM dbo.LPM_SimItemSkuMax_Staging n
                          INNER JOIN #PriceCapMatches pc
                                  ON pc.StoreID  = n.StoreID
                                 AND pc.ItemCode = n.ItemCode
                         WHERE n.Country = @country;

                        UPDATE n
                           SET n.SKUMax = 0
                          FROM dbo.LPM_SimItemSkuMax_Staging n
                         WHERE n.Country = @country
                           AND (
                               EXISTS (SELECT 1 FROM #ExcludeMatches em
                                        WHERE em.StoreID  = n.StoreID
                                          AND em.ItemCode = n.ItemCode)
                            OR EXISTS (SELECT 1 FROM #DeactMatches dm
                                        WHERE dm.StoreID  = n.StoreID
                                          AND dm.ItemCode = n.ItemCode)
                            OR EXISTS (SELECT 1 FROM #DeptDeactMatches dd
                                        WHERE dd.StoreID  = n.StoreID
                                          AND dd.ItemCode = n.ItemCode)
                           );

                        COMMIT;
                    END TRY
                    BEGIN CATCH
                        IF @@TRANCOUNT > 0 ROLLBACK;
                        THROW;
                    END CATCH;

                    DROP TABLE IF EXISTS #SkuSnap;
                    DROP TABLE IF EXISTS #ExcludeMatches;
                    DROP TABLE IF EXISTS #PriceCapMatches;
                    DROP TABLE IF EXISTS #DeactMatches;
                    DROP TABLE IF EXISTS #DeptDeactMatches;

                    SELECT @excluded        AS Excluded,
                           @priceCapped     AS PriceCapped,
                           @deactivated     AS Deactivated,
                           @deptDeactivated AS DeptDeactivated,
                           ISNULL(@ms1, 0) AS Ms1, ISNULL(@ms2, 0) AS Ms2,
                           ISNULL(@ms3, 0) AS Ms3, ISNULL(@ms4, 0) AS Ms4,
                           ISNULL(@ms5, 0) AS Ms5, ISNULL(@ms6, 0) AS Ms6,
                           ISNULL(@ms7, 0) AS Ms7,
                           @r1Error AS R1Error, @r2Error AS R2Error,
                           @r3Error AS R3Error, @r4Error AS R4Error,
                           @r5Error AS R5Error, @r6Error AS R6Error,
                           @r7Error AS R7Error;";
                excl.Parameters.Add(new SqlParameter("@country", country));
                excl.Parameters.Add(new SqlParameter("@y", year));
                excl.Parameters.Add(new SqlParameter("@m", month));
                excl.CommandTimeout = 1800;
                // 1.14.21 — hook InfoMessage to stream per-rule progress.
                // RAISERROR(@msg, 0, 1) WITH NOWAIT inside the SQL fires
                // these mid-batch (severity 0 ⇒ Class = 0 on SqlError).
                // The handler is detached at the end of the using-block
                // below so it never leaks onto the pooled SqlConnection.
                SqlInfoMessageEventHandler infoHandler = (sender, e) =>
                {
                    foreach (SqlError err in e.Errors)
                    {
                        if (err.Class == 0 && !string.IsNullOrWhiteSpace(err.Message))
                            progress?.Report(err.Message);
                    }
                };
                var sqlConn = (SqlConnection)conn;
                sqlConn.InfoMessage += infoHandler;
                try
                {
                    using var rdr2 = await excl.ExecuteReaderAsync(ct);
                    if (await rdr2.ReadAsync(ct))
                    {
                        excluded         = rdr2.IsDBNull(0)  ? 0L : Convert.ToInt64(rdr2.GetValue(0));
                        priceCapped      = rdr2.IsDBNull(1)  ? 0L : Convert.ToInt64(rdr2.GetValue(1));
                        deactivated      = rdr2.IsDBNull(2)  ? 0L : Convert.ToInt64(rdr2.GetValue(2));
                        deptDeactivated  = rdr2.IsDBNull(3)  ? 0L : Convert.ToInt64(rdr2.GetValue(3));
                        msRule1          = rdr2.IsDBNull(4)  ? 0  : Convert.ToInt32(rdr2.GetValue(4));
                        msRule2          = rdr2.IsDBNull(5)  ? 0  : Convert.ToInt32(rdr2.GetValue(5));
                        msRule3          = rdr2.IsDBNull(6)  ? 0  : Convert.ToInt32(rdr2.GetValue(6));
                        msRule4          = rdr2.IsDBNull(7)  ? 0  : Convert.ToInt32(rdr2.GetValue(7));
                        msRule5          = rdr2.IsDBNull(8)  ? 0  : Convert.ToInt32(rdr2.GetValue(8));
                        msRule6          = rdr2.IsDBNull(9)  ? 0  : Convert.ToInt32(rdr2.GetValue(9));
                        msRule7          = rdr2.IsDBNull(10) ? 0  : Convert.ToInt32(rdr2.GetValue(10));
                        ruleErrors[1]    = rdr2.IsDBNull(11) ? null : rdr2.GetString(11);
                        ruleErrors[2]    = rdr2.IsDBNull(12) ? null : rdr2.GetString(12);
                        ruleErrors[3]    = rdr2.IsDBNull(13) ? null : rdr2.GetString(13);
                        ruleErrors[4]    = rdr2.IsDBNull(14) ? null : rdr2.GetString(14);
                        ruleErrors[5]    = rdr2.IsDBNull(15) ? null : rdr2.GetString(15);
                        ruleErrors[6]    = rdr2.IsDBNull(16) ? null : rdr2.GetString(16);
                        ruleErrors[7]    = rdr2.IsDBNull(17) ? null : rdr2.GetString(17);
                    }
                }
                finally
                {
                    sqlConn.InfoMessage -= infoHandler;
                }
            }
        }
        catch (SqlException ex) when (!ct.IsCancellationRequested)
        {
            // Schema mismatch (Invalid column name 'Shop' / 'Shopname' / etc.)
            // — the snapshot rebuild already committed; we just lose exclusions.
            // Surface a one-line warning so the planner sees what happened
            // without the build banner flipping to "Failed".
            exclusionWarning = ex.Message.Length > 200
                ? ex.Message[..200] + "…"
                : ex.Message;
            progress?.Report($"Exclusions skipped (snapshot kept): {exclusionWarning}");
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
        {
            exclusionWarning = ex.Message;
            progress?.Report($"Exclusions skipped (snapshot kept): {exclusionWarning}");
        }
        var msInsert = sw.ElapsedMilliseconds; sw.Restart();

        // ============================================================
        // 6.5) DELTA APPLY (#NewSnap → dbo.LPM_SimItemSkuMax)
        // ============================================================
        // The staging snapshot in #NewSnap now has the FINAL SKUMax for
        // every (Store × Item × Season), with all 7 override rules
        // applied. Propagate ONLY the changed rows to the user table:
        //
        //   • DELETE  rows in target (for this period) not in #NewSnap.
        //             Scoped to in-#ItemWh items when scope != All.
        //   • UPDATE  rows whose (SKUMax, WHBoxQty, VolumeGroup, DivCode)
        //             differ from #NewSnap (option b — full-row diff).
        //   • INSERT  rows in #NewSnap not yet in target.
        //
        // 3 nonclustered indexes are dropped before the writes and
        // recreated after — minimises log + index-maintenance overhead
        // on what's still a 15M+-row table. The CREATE INDEX statements
        // run in parallel where possible (SQL Server decides).
        //
        // SKIP entirely when exclusionWarning is set — that means the
        // exclusions phase failed catastrophically and #NewSnap may not
        // reflect the desired overrides. Better to keep the prior build's
        // data than overwrite with incomplete overrides.
        if (exclusionWarning is null)
        {
            progress?.Report("Applying delta to LPM_SimItemSkuMax…");
            using (var apply = conn.CreateCommand())
            {
                apply.CommandText = $@"
                    SET XACT_ABORT ON;
                    DECLARE @deleted bigint = 0, @updated bigint = 0, @inserted bigint = 0;

                    -- Phase A: drop the 3 NCIs (DDL — happens outside the
                    -- DML transaction so a delta-rollback doesn't have to
                    -- rebuild the NCIs as part of undo).
                    IF EXISTS (SELECT 1 FROM sys.indexes
                                WHERE name = 'IX_LPM_SimItemSkuMax_Lookup'
                                  AND object_id = OBJECT_ID('dbo.LPM_SimItemSkuMax'))
                        DROP INDEX IX_LPM_SimItemSkuMax_Lookup ON dbo.LPM_SimItemSkuMax;
                    IF EXISTS (SELECT 1 FROM sys.indexes
                                WHERE name = 'IX_LPM_SimItemSkuMax_Item'
                                  AND object_id = OBJECT_ID('dbo.LPM_SimItemSkuMax'))
                        DROP INDEX IX_LPM_SimItemSkuMax_Item ON dbo.LPM_SimItemSkuMax;
                    IF EXISTS (SELECT 1 FROM sys.indexes
                                WHERE name = 'IX_LPM_SimItemSkuMax_Div'
                                  AND object_id = OBJECT_ID('dbo.LPM_SimItemSkuMax'))
                        DROP INDEX IX_LPM_SimItemSkuMax_Div ON dbo.LPM_SimItemSkuMax;

                    -- Phase B: DELETE / UPDATE / INSERT delta — atomic.
                    BEGIN TRY
                        BEGIN TRAN;

                        -- DELETE rows in target not in the staging table
                        -- (scoped to the period; LpmOnly/NonLpmOnly preserve
                        -- out-of-scope rows via the deleteScopeFilter).
                        -- 1.14.43 — #NewSnap → dbo.LPM_SimItemSkuMax_Staging.
                        -- Country filter added to the EXISTS sub-query so
                        -- only this build's staging rows participate (other
                        -- countries' rows might sit in the table from
                        -- concurrent or prior builds).
                        DELETE tgt
                          FROM dbo.LPM_SimItemSkuMax tgt
                         WHERE tgt.Country = @country AND tgt.Year1 = @y AND tgt.Month1 = @m
                           {deleteScopeFilter}
                           AND NOT EXISTS (
                               SELECT 1 FROM dbo.LPM_SimItemSkuMax_Staging s
                                WHERE s.Country  = @country
                                  AND s.StoreID  = tgt.StoreID
                                  AND s.ItemCode = tgt.ItemCode
                                  AND s.Season   = tgt.Season
                           );
                        SET @deleted = @@ROWCOUNT;

                        -- UPDATE rows where any of the 4 cols differ.
                        -- (Skip-if-everything-unchanged — option b.)
                        --
                        -- 1.14.83 — Added `tgt.CreatedBy = @user`. Previously
                        -- this UPDATE bumped CreateTS to the current build's
                        -- timestamp but left CreatedBy carrying the PREVIOUS
                        -- builder's name. The ''TOP 1 CreatedBy ORDER BY
                        -- CreateTS DESC'' lookup in GetLastSkuMaxBuildAsync
                        -- therefore returned the prior user's name alongside
                        -- the new build's timestamp — a real bug. The primary
                        -- display now reads BuiltBy from
                        -- LPM_SimItemSkuMaxBuild (per-period header), but
                        -- keeping the per-row CreatedBy in sync stops the
                        -- audit trail from also drifting.
                        UPDATE tgt
                           SET tgt.SKUMax      = s.SKUMax,
                               tgt.WHBoxQty    = s.WHBoxQty,
                               tgt.VolumeGroup = s.VolumeGroup,
                               tgt.DivCode     = s.DivCode,
                               tgt.SOH         = s.SOH,            -- 1.14.35
                               tgt.CreateTS    = @now,
                               tgt.CreatedBy   = @user             -- 1.14.83
                          FROM dbo.LPM_SimItemSkuMax tgt
                          INNER JOIN dbo.LPM_SimItemSkuMax_Staging s
                                  ON s.Country  = @country
                                 AND s.StoreID  = tgt.StoreID
                                 AND s.ItemCode = tgt.ItemCode
                                 AND s.Season   = tgt.Season
                         WHERE tgt.Country = @country AND tgt.Year1 = @y AND tgt.Month1 = @m
                           AND (
                                tgt.SKUMax      <> s.SKUMax
                             OR tgt.WHBoxQty    <> s.WHBoxQty
                             OR ISNULL(tgt.VolumeGroup, '') <> ISNULL(s.VolumeGroup, '')
                             OR tgt.DivCode     <> s.DivCode
                             OR tgt.SOH         <> s.SOH           -- 1.14.35
                           );
                        SET @updated = @@ROWCOUNT;

                        -- INSERT staging rows not yet in target.
                        INSERT INTO dbo.LPM_SimItemSkuMax WITH (TABLOCK)
                               (Country, Year1, Month1, StoreID, ItemCode, Season,
                                DivCode, WHBoxQty, VolumeGroup, SKUMax, SOH, CreateTS, CreatedBy)
                        SELECT @country, @y, @m, s.StoreID, s.ItemCode, s.Season,
                               s.DivCode, s.WHBoxQty, s.VolumeGroup, s.SKUMax, s.SOH, @now, @user
                          FROM dbo.LPM_SimItemSkuMax_Staging s
                         WHERE s.Country = @country
                           AND NOT EXISTS (
                             SELECT 1 FROM dbo.LPM_SimItemSkuMax tgt
                              WHERE tgt.Country  = @country
                                AND tgt.Year1    = @y
                                AND tgt.Month1   = @m
                                AND tgt.StoreID  = s.StoreID
                                AND tgt.ItemCode = s.ItemCode
                                AND tgt.Season   = s.Season
                         );
                        SET @inserted = @@ROWCOUNT;

                        COMMIT;
                    END TRY
                    BEGIN CATCH
                        IF @@TRANCOUNT > 0 ROLLBACK;
                        THROW;
                    END CATCH;

                    -- Phase C: recreate the 3 NCIs.
                    CREATE INDEX IX_LPM_SimItemSkuMax_Lookup
                        ON dbo.LPM_SimItemSkuMax (Country, Year1, Month1, StoreID, Season)
                        INCLUDE (ItemCode, SKUMax, WHBoxQty, DivCode, VolumeGroup);
                    CREATE INDEX IX_LPM_SimItemSkuMax_Item
                        ON dbo.LPM_SimItemSkuMax (Country, Year1, Month1, ItemCode)
                        INCLUDE (StoreID, Season, SKUMax);
                    CREATE INDEX IX_LPM_SimItemSkuMax_Div
                        ON dbo.LPM_SimItemSkuMax (Country, Year1, Month1, DivCode)
                        INCLUDE (StoreID, ItemCode, Season, SKUMax, WHBoxQty);

                    SELECT @deleted AS Deleted, @updated AS Updated, @inserted AS Inserted;";
                apply.Parameters.Add(new SqlParameter("@country", country));
                apply.Parameters.Add(new SqlParameter("@y", year));
                apply.Parameters.Add(new SqlParameter("@m", month));
                apply.Parameters.Add(new SqlParameter("@now", DateTime.Now));
                apply.Parameters.Add(new SqlParameter("@user",
                    string.IsNullOrEmpty(user) ? (object)DBNull.Value : user));
                apply.CommandTimeout = 1800;
                using var rdr3 = await apply.ExecuteReaderAsync(ct);
                if (await rdr3.ReadAsync(ct))
                {
                    deltaDeleted  = rdr3.IsDBNull(0) ? 0L : Convert.ToInt64(rdr3.GetValue(0));
                    deltaUpdated  = rdr3.IsDBNull(1) ? 0L : Convert.ToInt64(rdr3.GetValue(1));
                    deltaInserted = rdr3.IsDBNull(2) ? 0L : Convert.ToInt64(rdr3.GetValue(2));
                }
            }
        }
        msDelta = sw.ElapsedMilliseconds; sw.Restart();

        // 6b) 1.14.18 — Archive-and-purge older periods for this country.
        //
        // Keeps dbo.LPM_SimItemSkuMax small (one period per country in
        // production) while preserving history in dbo.LPM_SimItemSkuMax_Backup.
        // "Strictly older" (Year1 < @y OR (Year1 = @y AND Month1 < @m)) so a
        // user rebuilding an OLDER period while a NEWER one already exists
        // doesn't accidentally demote the newer one to the archive.
        //
        // Wrapped in TRY/CATCH so a missing backup table (migration 045 not
        // applied yet) or transient archive failure doesn't fail the build —
        // the user-visible SKU Max snapshot for the just-built period is
        // already committed. Archive will retry on the next build.
        //
        // Only runs when the delta apply itself ran (exclusionWarning is null).
        if (exclusionWarning is null)
        {
            progress?.Report("Archiving older periods…");
            try
            {
                using var arch = conn.CreateCommand();
                arch.CommandText = @"
                    SET XACT_ABORT ON;
                    BEGIN TRAN;

                    -- 1.14.35: SOH carried into the backup so archived
                    -- periods preserve the same shape. ToFillQty is a
                    -- computed PERSISTED column on both tables — recomputed
                    -- automatically from the inserted SOH/SKUMax, so it's
                    -- not listed in the column lists below.
                    INSERT INTO dbo.LPM_SimItemSkuMax_Backup
                           (Country, Year1, Month1, StoreID, ItemCode, Season,
                            DivCode, WHBoxQty, VolumeGroup, SKUMax, SOH,
                            CreateTS, CreatedBy, BackupTS, BackupBy)
                    SELECT Country, Year1, Month1, StoreID, ItemCode, Season,
                           DivCode, WHBoxQty, VolumeGroup, SKUMax, SOH,
                           CreateTS, CreatedBy, SYSDATETIME(), @user
                      FROM dbo.LPM_SimItemSkuMax
                     WHERE Country = @c
                       AND (Year1 < @y OR (Year1 = @y AND Month1 < @m));

                    DELETE FROM dbo.LPM_SimItemSkuMax
                     WHERE Country = @c
                       AND (Year1 < @y OR (Year1 = @y AND Month1 < @m));

                    COMMIT;";
                arch.Parameters.Add(new SqlParameter("@c", country));
                arch.Parameters.Add(new SqlParameter("@y", year));
                arch.Parameters.Add(new SqlParameter("@m", month));
                arch.Parameters.Add(new SqlParameter("@user",
                    string.IsNullOrEmpty(user) ? (object)DBNull.Value : user));
                arch.CommandTimeout = 600;
                await arch.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) when (ex.Number == 208 /* invalid object — backup table missing */)
            {
                // Non-fatal — migration 045 hasn't been applied yet. Build
                // succeeded; archive will run on the next build after the
                // operator applies the migration.
                progress?.Report("Archive skipped (backup table missing — apply migration 045).");
            }
            catch (Exception)
            {
                // Non-fatal — archive failure shouldn't undo a successful
                // build. The next build retries. Log via progress only.
                progress?.Report("Archive failed (non-fatal — will retry next build).");
            }
        }

        // 7) Drop temp tables (also auto-released when the session ends, but
        // explicit cleanup keeps tempdb tidy when the same connection runs
        // multiple builds back-to-back from the connection pool).
        progress?.Report("Cleaning up…");
        using (var ddl = conn.CreateCommand())
        {
            // 1.14.43 — #NewSnap removed from this drop list. It's no
            // longer a temp table; the persistent staging table
            // dbo.LPM_SimItemSkuMax_Staging holds the post-override
            // snapshot and is cleared on the NEXT build's first step
            // (DELETE WHERE Country = @country). Leaving rows in place
            // between builds lets debug queries inspect what the
            // exclusions phase actually wrote.
            ddl.CommandText = @"
                DROP TABLE IF EXISTS #ItemWh;
                DROP TABLE IF EXISTS #Stores;
                DROP TABLE IF EXISTS #Rules;
                DROP TABLE IF EXISTS #Deact;";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        // 8) Persist the build-history header row. The page reads this back
        // to show "Built in 3m 42s" in the SKU Max status row even after a
        // server restart wipes SkuMaxBuildJobManager's in-memory state.
        var totalMs = msDelete + msItemWh + msInputs + msInsert + msDelta;
        // 1.14.21 — per-rule timings are now ALWAYS shown (was previously
        // hidden when override counts were all zero, which made it hard to
        // see why the build was slow when most rules contributed 0
        // matches but each still scanned its source). Helps the planner
        // spot bottlenecks: usually Rule 4 (upcbarcodes) or Rule 5
        // (Hodata.SalesPrice + DeptPriceMaxQty_MH4).
        var ruleBreakdown =
            $" [R1 {FormatMs(msRule1)} · R2 {FormatMs(msRule2)} · R3 {FormatMs(msRule3)} · "
          + $"R4 {FormatMs(msRule4)} · R5 {FormatMs(msRule5)} · R6 {FormatMs(msRule6)} · R7 {FormatMs(msRule7)}]";
        // Per-rule failures (e.g. Hodata access denied on Rule 5) are now
        // surfaced rule-by-rule rather than killing the whole batch — only
        // the rules that threw lose their effects; the rest still apply.
        var failedRules = ruleErrors.Where(kv => kv.Value is not null).ToList();
        var ruleFailureTag = failedRules.Count == 0
            ? ""
            : " · " + string.Join(" | ",
                failedRules.Select(kv => $"R{kv.Key} SKIPPED ({Truncate(kv.Value!, 80)})"));
        var exclusionTag = exclusionWarning is null
            ? $"{excluded:N0} excluded · {priceCapped:N0} price-capped · {deactivated:N0} div-deact · {deptDeactivated:N0} dept-deact{ruleBreakdown}{ruleFailureTag}"
            : $"overrides SKIPPED ({exclusionWarning})";
        // Delta breakdown — shows how much actually changed on the user
        // table. On a fresh period the inserted count = staging size;
        // on rebuilds with unchanged inputs it's all zeros (staging built
        // for nothing, but the heavy writes are skipped).
        var unchanged = Math.Max(0L, inserted - deltaInserted - deltaUpdated);
        var deltaTag = exclusionWarning is null
            ? $"Delta {FormatMs(msDelta)} · {deltaInserted:N0} ins · {deltaUpdated:N0} upd · {deltaDeleted:N0} del · {unchanged:N0} unchanged"
            : "Delta SKIPPED (exclusions failed — target unchanged)";
        // 1.14.21 — total time added at the front; phase times use
        // FormatMs (e.g. "5m 19s" not "319000ms") so the breakdown is
        // readable at a glance.
        var stageDetail =
            $"Done in {FormatMs((int)totalMs)} · {itemsInScope:N0} items in scope ({droppedNoMaster:N0} no-master) · " +
            $"Setup {FormatMs(msDelete)} · ItemWh {FormatMs(msItemWh)} · Inputs {FormatMs(msInputs)} · " +
            $"Excl+Insert {FormatMs(msInsert)} · {inserted:N0} staged · {deltaTag} · {exclusionTag}";
        var buildEnd = DateTime.Now;
        var buildStart = buildEnd.AddMilliseconds(-totalMs);
        using (var hdr = conn.CreateCommand())
        {
            hdr.CommandText = @"
                MERGE dbo.LPM_SimItemSkuMaxBuild AS tgt
                USING (SELECT @c AS Country, @y AS Year1, @m AS Month1) AS src
                   ON tgt.Country = src.Country
                  AND tgt.Year1   = src.Year1
                  AND tgt.Month1  = src.Month1
                WHEN MATCHED THEN UPDATE SET
                       BuildStart  = @start,
                       BuildEnd    = @end,
                       DurationMs  = @ms,
                       [RowCount]  = @rows,
                       BuiltBy     = @user,
                       StageDetail = @stage
                WHEN NOT MATCHED THEN INSERT
                       (Country, Year1, Month1, BuildStart, BuildEnd, DurationMs, [RowCount], BuiltBy, StageDetail)
                  VALUES (@c, @y, @m, @start, @end, @ms, @rows, @user, @stage);";
            hdr.Parameters.Add(new SqlParameter("@c", country));
            hdr.Parameters.Add(new SqlParameter("@y", year));
            hdr.Parameters.Add(new SqlParameter("@m", month));
            hdr.Parameters.Add(new SqlParameter("@start", buildStart));
            hdr.Parameters.Add(new SqlParameter("@end", buildEnd));
            hdr.Parameters.Add(new SqlParameter("@ms", totalMs));
            hdr.Parameters.Add(new SqlParameter("@rows", inserted));
            hdr.Parameters.Add(new SqlParameter("@user",
                string.IsNullOrEmpty(user) ? (object)DBNull.Value : user));
            hdr.Parameters.Add(new SqlParameter("@stage", stageDetail));
            hdr.CommandTimeout = 30;
            try { await hdr.ExecuteNonQueryAsync(ct); }
            catch (SqlException ex) when (ex.Number == 208 /* invalid object */)
            {
                // Migration 032 hasn't been applied yet — non-fatal,
                // duration just won't be persisted until the migration runs.
            }
        }

        // Final stage banner — summary times for the user. Keeps the job's
        // last-known StatusMessage useful even after JobChanged fires.
        progress?.Report(stageDetail);
        return inserted;
    }

    // (BulkCopyItemSkuMax + TupleIntStringComparer removed in Phase G — the
    // SQL-side INSERT … SELECT in BuildItemSkuMaxAsync no longer needs a
    // C# cross-join, so the DataTable bulk-copy helper and the in-memory
    // rules-band comparer are no longer used. Git history has the prior
    // implementation if we ever need to revert.)

    /// <summary>
    /// Loads <c>LPM_SimItemSkuMax</c> rows for the run period into an in-memory
    /// dictionary keyed by <c>(StoreID, ItemCode)</c>. When an item has rows
    /// in both seasons, the MAX SKUMax wins — most items live in only one
    /// season anyway, so this rarely matters in practice and avoids the
    /// alloc loop having to track a per-box-season cap (cumulative qty per
    /// (Store, Item) is single-valued in the engine's bookkeeping).
    /// </summary>
    // 1.14.78 — `scopeCountries` argument added. When the run's primary
    // country has linked children (e.g. UAE → OMAN), the SkuMax read widens
    // to include all countries in scope so the allocator has SKU Max for
    // both parent's and children's stores. Defaults to a single-element
    // list with just `req.Country` for runs with no linked children.
    private static async Task<Dictionary<(string Store, string Item), int>> LoadItemSkuMaxAsync(
        SqlConnection conn, LpmSimGenerateRequest req,
        IReadOnlyList<string> scopeCountries,
        CancellationToken ct)
    {
        // 1.14.58 — Filter SKUMax > 0 at the SQL level. The full LPM_SimItemSkuMax
        // snapshot is ~13.8M rows for UAE (~150 stores × ~90K items), and the
        // typical row distribution is dominated by SKUMax = 0. Loading all 13.8M
        // into a Dictionary needed roughly 2 GB steady / 4 GB peak during
        // Resize() — which OOM'd the Azure App Service process (~1.75 GB
        // available) on the first run after BuildSkuMax stopped silently
        // truncating the staging table.
        //
        // 1.14.59 — Also restrict to items that appear in at least one
        // eligible box. "Eligible" mirrors every filter the allocator's
        // ReadBoxesAsync applies:
        //
        //   • Box Source       (LPM / Non-LPM / Both)   ← req.Sources
        //   • Season           (Summer / Winter / Both) ← req.Seasons (pt.Season)
        //   • LPM Months       (specific months)        ← req.LpmMonths
        //   • Pallet Categories (e.g. ELIGIBLE)         ← req.PalletCategories
        //   • Warehouses       (e.g. JAFZA, TECHNO)     ← req.Warehouses
        //   • Purchased / Non-Purchased (ShopEligible)  ← req.IncludePurchasedBoxes
        //
        // Any item the allocator could not possibly allocate (because every
        // box containing it is filtered out at the box stage) is now also
        // dropped from the SKUMax dictionary. The filter is built as a
        // CTE-resolved DISTINCT itemcode set, INNER-JOINed to LPM_SimItemSkuMax.
        //
        // Filtering is semantically a no-op against the allocator's contract:
        // every read path uses GetValueOrDefault((store, item), 0) and gates
        // on `> 0`, so a missing key and a key mapping to 0 are treated
        // identically. Items dropped by the EligibleItems CTE would have
        // been skipped at the box-loop level anyway. The LPMSIM_StoreItemBalance
        // write path only iterates (Store, Item) pairs that actually received
        // allocation in some phase — those by definition came from an eligible
        // box AND had SKUMax > 0 — so the snapshot is never affected.
        //
        // Expected post-filter cardinality for UAE:
        //   • All sources, no filters (legacy):  ~13.8M entries, ~2.2 GB → OOM
        //   • SKUMax > 0 alone (1.14.58):        ~2–3M entries, ~500 MB
        //   • + Eligible box filter (1.14.59):
        //       LPM-only / ELIGIBLE / Summer:    ~0.4–0.8M entries, ~100–200 MB
        //       Non-LPM-only / ELIGIBLE / Summer:~0.8–1.2M entries, ~200–300 MB
        //       Both / ELIGIBLE / Summer:        ~1.2–2.0M entries, ~250–450 MB

        var country = req.Country;
        var year    = req.RunYear;
        var month   = req.RunMonth;

        bool lpmIncluded    = req.Sources.HasFlag(LpmSimSourceFlags.LpmBoxes);
        bool nonLpmIncluded = req.Sources.HasFlag(LpmSimSourceFlags.NonLpmBoxes);

        var dict = new Dictionary<(string, string), int>(StoreItemComparer.Instance);

        // Defensive — no sources picked, nothing the allocator can do; return
        // an empty dict rather than a stale "all items" cache.
        if (!lpmIncluded && !nonLpmIncluded) return dict;

        // Resolve country-aware whboxitems source — same helper the rest of
        // the file uses (UAE → racks.dbo.whboxitems; others → [<DataName>].dbo.WHBoxItemsExport).
        var whSrc = await WhBoxItemsSource.ResolveAsync(conn, country, ct);

        // Build the same clauses ReadBoxesAsync builds. Param names are local
        // to this command so no collision risk with other queries.
        var seasonClause = req.Seasons switch
        {
            LpmSimSeasonFlags.Summer => "AND ISNULL(pt.Season, '') <> 'W'",
            LpmSimSeasonFlags.Winter => "AND ISNULL(pt.Season, '') = 'W'",
            _                        => "",     // Both / None → no season filter
        };

        var (palletClause, palletParams) = BuildPalletCategoryClause(req.PalletCategories);
        var (whClause,     whParams)     = BuildWarehouseClause(req.Warehouses);
        var shopEligibleClause = req.IncludePurchasedBoxes
            ? ""
            : "AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')";

        var lpmMonthParams = new List<SqlParameter>();

        // Build the LPM-source half of the date clause (used when LPM is in
        // scope). Mirrors ReadBoxesAsync exactly.
        string BuildLpmHalf()
        {
            if (req.LpmMonths is { Count: > 0 })
            {
                var ors = new List<string>();
                for (int i = 0; i < req.LpmMonths.Count; i++)
                {
                    var ms = new DateTime(req.LpmMonths[i].Year, req.LpmMonths[i].Month, 1);
                    var me = ms.AddMonths(1);
                    ors.Add($"(w.LPMDt >= @lm{i}_s AND w.LPMDt < @lm{i}_e)");
                    lpmMonthParams.Add(new SqlParameter($"@lm{i}_s", ms));
                    lpmMonthParams.Add(new SqlParameter($"@lm{i}_e", me));
                }
                return "(w.LPMDt IS NOT NULL AND (" + string.Join(" OR ", ors) + "))";
            }
            return "(w.LPMDt IS NOT NULL AND w.LPMDt < @endExclusive)";
        }

        string lpmDtClause;
        if (lpmIncluded && nonLpmIncluded)
        {
            // Both — LPM matches month filter OR Non-LPM (which has no
            // month filter because LPMDt IS NULL by definition).
            lpmDtClause = $"AND ({BuildLpmHalf()} OR w.LPMDt IS NULL)";
        }
        else if (lpmIncluded)
        {
            lpmDtClause = $"AND {BuildLpmHalf()}";
        }
        else // nonLpmIncluded only
        {
            lpmDtClause = "AND w.LPMDt IS NULL";
        }

        // Only JOIN to pallettype when the Season filter actually uses it.
        // For Both seasons we save a multi-million-row JOIN entirely.
        var palletTypeJoin = string.IsNullOrEmpty(seasonClause)
            ? ""
            : "INNER JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType";

        // 1.14.78 — `skuMaxCountryClause` widens the filter so parent +
        // children SkuMax rows are both loaded (UAE+OMAN for a UAE run).
        // Built BEFORE the SQL string interpolation so the `$@` interpolator
        // can splice the literal "IN (@sc0, @sc1)" fragment into the SQL.
        var (skuMaxCountryClause, skuMaxCountryParams) = BuildCountryInClause(scopeCountries, "@sc");

        // The CTE narrows to DISTINCT eligible itemcodes; the outer SELECT
        // then INNER-JOINs LPM_SimItemSkuMax for the country(ies) + period
        // and returns only rows whose item passed eligibility AND has
        // SKUMax > 0.
        var sql = $@"
            ;WITH EligibleItems AS (
                SELECT DISTINCT w.ItemCode
                  FROM {whSrc} w
                  {palletTypeJoin}
                 WHERE w.ItemCode IS NOT NULL AND w.ItemCode <> ''
                   {palletClause}
                   {shopEligibleClause}
                   {seasonClause}
                   {whClause}
                   {lpmDtClause}
            )
            SELECT sm.StoreID, sm.ItemCode, sm.SKUMax
              FROM dbo.LPM_SimItemSkuMax sm
              INNER JOIN EligibleItems ei ON ei.ItemCode = sm.ItemCode
             WHERE sm.Country IN {skuMaxCountryClause}
               AND sm.Year1 = @y AND sm.Month1 = @m
               AND sm.SKUMax > 0;";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@y", year));
        cmd.Parameters.Add(new SqlParameter("@m", month));
        foreach (var p in skuMaxCountryParams) cmd.Parameters.Add(p);
        // @endExclusive is referenced only when LpmMonths is empty AND LPM is
        // in scope. Adding it unconditionally is fine — SQL Server ignores
        // unused params. Same convention as ReadBoxesAsync.
        cmd.Parameters.Add(new SqlParameter("@endExclusive",
            new DateTime(year, month, 1).AddMonths(1)));
        foreach (var p in palletParams)    cmd.Parameters.Add(p);
        foreach (var p in whParams)        cmd.Parameters.Add(p);
        foreach (var p in lpmMonthParams)  cmd.Parameters.Add(p);
        cmd.CommandTimeout = 600;

        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var key = (rdr.GetString(0), rdr.GetString(1));
            var sku = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
            // Same item with rows in Summer + Winter → keep the MAX (defensive).
            if (!dict.TryGetValue(key, out var prev) || sku > prev)
                dict[key] = sku;
        }
        return dict;
    }

    // 1.14.18: BoxQty added so the allocator can stamp it on every output row
    // (LPMSIM_Output.BoxQty column, migration 044). Value comes from
    // SUM(w.Qty) OVER (PARTITION BY w.BoxNo) which the SELECT already
    // projects — previously used only for ORDER BY, now also read.
    private record BoxItem(string BoxNo, string? PalletNo, DateTime? LPMDt, string ItemCode, int Qty, string Season, long BoxQty);
    // 1.14.26 — Pre-filter snapshot of eligible boxes for the
    // LPMSIM_UnallocatedDiagnostic write at the end of GenerateAsync.
    // BoxQty is replicated from BoxItem.BoxQty (same value for every line
    // of the same BoxNo). BoxKind is "LPM" if LPMDt is set, else "Non-LPM".
    private record EligibleBoxSummary(string? PalletNo, DateTime? LPMDt, string BoxKind, long BoxQty);

    /// <summary>
    /// 1.14.70 — Per-box meta for closed boxes that were filtered out of the
    /// allocator. Populated by <see cref="ReadBoxesAsync"/> when the closed-box
    /// EXISTS check fires (UAE: USA..upcboxhead.Closed='Y'; non-UAE:
    /// Exclude_Transfers_Sim.Trfno or CloseR1Pallet.palletno). Passed to
    /// <see cref="BuildAndInsertUnallocatedDiagnosticAsync"/> so the planner
    /// sees one CLOSED_BOX row per excluded box in the Gap list.
    /// </summary>
    private record ClosedBoxMeta(string? PalletNo, DateTime? LPMDt, string BoxKind, long BoxQty);
    // 1.14.78 — `CountryPriority` added so the allocator can rank parent's
    // stores above child countries' stores within each phase. Parent (the
    // run's primary country) = 0; children = 1, 2, … in alphabetical order
    // of their country code. Defaults to 0 — for runs with no linked
    // children (the common case), every store gets CountryPriority=0 and
    // the sort behaves exactly as it did pre-1.14.78.
    // 1.14.87 — MerchNeedWeek → MerchNeedMonth (the allocator's only Merch Need cap
    // now; Week-per-store split dropped). Grade added so 1b/2b RR can fill in
    // grade-tier order (Diamond → Platinum → Gold → Silver). Stores without a
    // recognised grade are excluded from RR entirely.
    private record EomStore(string StoreID, int DivCode, int SKUMax, decimal TargetEOM,
                            decimal PriorityRank, decimal WtAvgSold, string VolumeGroup,
                            decimal MerchNeedMonth,
                            string Grade,
                            int CountryPriority = 0);

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

    // 1.14.67 — `tx` threads the outer persist-phase SqlTransaction through to
    // the bulk copy. When non-null, every batch this SqlBulkCopy writes is
    // enrolled in `tx` instead of auto-committing per-batch — so a ROLLBACK at
    // the outer level erases all rows this method wrote. When null (legacy
    // callers / tests), each batch still auto-commits.
    private static async Task BulkInsertOutputAsync(SqlConnection conn, List<LpmSimOutput> rows, CancellationToken ct, SqlTransaction? tx = null)
    {
        using var dt = new System.Data.DataTable();
        dt.Columns.Add("LPMBatchNo",   typeof(long));
        dt.Columns.Add("BoxNo",        typeof(string));
        dt.Columns.Add("PalletNo",     typeof(string));        // 1.14.12 — migration 041
        dt.Columns.Add("LPMDt",        typeof(DateTime));
        dt.Columns.Add("Itemcode",     typeof(string));
        dt.Columns.Add("Qty",          typeof(int));
        dt.Columns.Add("StoreID",      typeof(string));
        dt.Columns.Add("CreateTS",     typeof(DateTime));
        dt.Columns.Add("CreatedBy",    typeof(string));
        dt.Columns.Add("Phase",        typeof(string));
        dt.Columns.Add("IsRoundRobin", typeof(bool));
        dt.Columns.Add("IsOverride",   typeof(bool));
        dt.Columns.Add("Day",          typeof(int));
        // 1.14.18 — migration 044 columns.
        dt.Columns.Add("Season",       typeof(string));
        dt.Columns.Add("BoxQty",       typeof(long));
        dt.Columns.Add("BoxItemQty",   typeof(int));
        dt.Columns.Add("UsabilityPct", typeof(decimal));
        dt.Columns.Add("DivCode",      typeof(int));
        dt.Columns.Add("SKUMax",       typeof(int));

        dt.BeginLoadData();
        foreach (var r in rows)
        {
            dt.Rows.Add(
                r.LPMBatchNo,
                r.BoxNo,
                (object?)r.PalletNo ?? DBNull.Value,        // 1.14.12
                (object?)r.LPMDt    ?? DBNull.Value,
                r.Itemcode,
                r.Qty,
                r.StoreID,
                r.CreateTS,
                r.CreatedBy,
                r.Phase,
                r.IsRoundRobin,
                r.IsOverride,
                (object?)r.Day ?? DBNull.Value,
                // 1.14.18
                (object?)r.Season       ?? DBNull.Value,
                (object?)r.BoxQty       ?? DBNull.Value,
                (object?)r.BoxItemQty   ?? DBNull.Value,
                (object?)r.UsabilityPct ?? DBNull.Value,
                (object?)r.DivCode      ?? DBNull.Value,
                (object?)r.SKUMax       ?? DBNull.Value);
        }
        dt.EndLoadData();

        // Phase F perf fix — streaming + larger batch.
        // 1.14.67 — TableLock REMOVED. The BU (Bulk Update) table-level lock
        // SqlBulkCopyOptions.TableLock acquires used to block (and be blocked
        // by) any concurrent reader on dbo.LPMSIM_Output — the Reports page
        // reads this table on every batch flip, so two users (one Generating,
        // one viewing Reports) could collide and the 600 s BulkCopyTimeout
        // would fire as "Execution Timeout Expired" (which is what we saw
        // for UAE 2026-05-20). Without TableLock, SqlBulkCopy uses row-level
        // X locks; the bulk insert is slightly slower in isolation but no
        // longer fights concurrent reads. Each Generate run only writes its
        // own LPMBatchNo rows, so there's no risk of two generators
        // interfering with each other's data.
        // 1.14.67 — `tx` parameter enrols this bulk copy in the outer
        // persist-phase transaction so partial writes ROLLBACK on failure.
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = "dbo.LPMSIM_Output",
            BatchSize            = 50_000,
            BulkCopyTimeout      = 600,
            EnableStreaming      = true,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    /// <summary>
    /// 1.14.26 — Build the per-eligible-box gap diagnostic for the just-
    /// completed batch and bulk-insert it into
    /// <c>dbo.LPMSIM_UnallocatedDiagnostic</c>. One row per eligible BoxNo
    /// where <c>RemainingQty &gt; 0</c>. Fully-allocated boxes are omitted.
    ///
    /// Reason taxonomy:
    ///   • <c>FILTERED_SEASON</c> — eligible per SQL filter but every line
    ///     got dropped by the 1.14.18 per-item Season filter; box never
    ///     reached the allocator.
    ///   • <c>SKIP_NO_DIV</c> / <c>SKIP_NO_EOM</c> — most common skip
    ///     decision in the trace for this box (these two are always logged
    ///     regardless of VerboseTrace).
    ///   • <c>EXCLUDED_BY_RULE</c> (1.14.28) — box reached the allocator
    ///     but every item in it has <c>SKUMax = 0</c> at every eligible
    ///     store (Rules 1-7 blocked it). Distinct from CAP because the
    ///     reason is an explicit business exclusion rather than capacity
    ///     saturation. Caller passes the pre-computed
    ///     <paramref name="excludedByRuleBoxes"/> set.
    ///   • <c>CAP</c> — deduced when the box reached the allocator and has
    ///     <c>RemainingQty &gt; 0</c> but no SKIP_NO_* trace rows AND is
    ///     not in <paramref name="excludedByRuleBoxes"/>. With non-verbose
    ///     trace (the default), SKIP_SKUMAX / SKIP_TARGET rows are dropped
    ///     — so any remaining qty after the deductible reasons are cleared
    ///     must be due to per-store cap saturation.
    ///   • <c>UNKNOWN</c> — defensive fallback. Shouldn't happen.
    /// </summary>
    // 1.14.67 — `tx` enrols every internal SqlCommand + SqlBulkCopy in the
    // outer persist transaction so the diagnostic's final UnallocatedDiagnostic
    // INSERT also rolls back if the outer Generate fails. The diagnostic IS
    // best-effort (caller wraps it in try/catch), but if it succeeds it must
    // commit/rollback in lockstep with Output/Trace/Balances to keep the per-
    // batch view consistent.
    // 1.14.70 — `closedBoxes` carries the meta for every BoxNo that
    // ReadBoxesAsync filtered out due to the closed-box exclusion rules. They
    // never reached the allocator, so they're not in `eligibleBoxes` —
    // this method emits a separate CLOSED_BOX diagnostic row per closed BoxNo
    // so the planner can see "shipped 0, reason: box marked closed" in the Gap
    // list. The closed-box row uses SimQty=0 and BoxQty from the meta.
    private static async Task BuildAndInsertUnallocatedDiagnosticAsync(
        SqlConnection conn,
        long batchNo,
        DateTime createTs,
        string country,
        int year,
        int month,
        Dictionary<string, EligibleBoxSummary> eligibleBoxes,
        HashSet<string> filteredOutBoxes,
        HashSet<string> excludedByRuleBoxes,
        Dictionary<string, ClosedBoxMeta> closedBoxes,
        List<LpmSimOutput> output,
        List<LpmSimAllocTrace> trace,
        CancellationToken ct,
        SqlTransaction? tx = null)
    {
        if (eligibleBoxes.Count == 0) return;

        // Per-box allocated qty (sum across all output lines for the box).
        // Boxes absent from this dict had zero allocation.
        var simQtyByBox = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in output)
        {
            simQtyByBox.TryGetValue(o.BoxNo, out var prev);
            simQtyByBox[o.BoxNo] = prev + o.Qty;
        }

        // Per-(box, skip-decision) trace row counts. We only need the SKIP_*
        // variants; ALLOC* rows tell us the box reached the allocator but
        // are otherwise not informative for the gap reason.
        // Also remember whether the box has any ALLOC* trace at all — used
        // below to distinguish CAP from UNKNOWN.
        var skipCountsByBox = new Dictionary<(string Box, string Reason), int>(BoxReasonComparer.Instance);
        var sawAllocByBox = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in trace)
        {
            if (string.IsNullOrEmpty(t.Decision)) continue;
            if (t.Decision.StartsWith("ALLOC", StringComparison.Ordinal))
            {
                sawAllocByBox.Add(t.BoxNo);
                continue;
            }
            if (!t.Decision.StartsWith("SKIP_", StringComparison.Ordinal)) continue;
            var key = (t.BoxNo, t.Decision);
            skipCountsByBox.TryGetValue(key, out var prev);
            skipCountsByBox[key] = prev + 1;
        }

        // 1.14.44 — "Box considered by allocator but no trace emitted" check.
        // In non-verbose-trace runs the allocator drops SKIP_SKUMAX /
        // SKIP_TARGET rows. A box whose every item × eligible store had
        // SOH ≥ SKUMax leaves no trace at all (no ALLOC, no SKIP). The
        // pre-1.14.44 classifier then fell back to UNKNOWN, which is
        // misleading — the gap really was CAP saturation. Detect this
        // case by querying once per build: which boxes have AT LEAST one
        // item with a SKUMax > 0 row for ANY store in this period?
        // Those are boxes the allocator definitely iterated over — if
        // they show no trace + no output, the gap is CAP, not UNKNOWN.
        var boxesWithViableLines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Stage the candidate box list into tempdb so the EXISTS join
            // can use it efficiently. Only boxes with remaining > 0 are
            // candidates for UNKNOWN — fully-allocated boxes don't end up
            // in the diagnostic anyway.
            using (var ddl = conn.CreateCommand())
            {
                ddl.Transaction = tx;       // 1.14.67 — enlist in outer persist transaction
                ddl.CommandText = @"
                    IF OBJECT_ID('tempdb..#DiagBoxes') IS NOT NULL DROP TABLE #DiagBoxes;
                    CREATE TABLE #DiagBoxes (BoxNo varchar(100) NOT NULL PRIMARY KEY);";
                await ddl.ExecuteNonQueryAsync(ct);
            }

            using (var diagBulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "#DiagBoxes" })
            {
                using var dtBoxes = new System.Data.DataTable();
                dtBoxes.Columns.Add("BoxNo", typeof(string));
                foreach (var boxNo in eligibleBoxes.Keys) dtBoxes.Rows.Add(boxNo);
                await diagBulk.WriteToServerAsync(dtBoxes, ct);
            }

            // 1.14.61 — Country-aware whboxitems source.
            var diagWhSrc = await WhBoxItemsSource.ResolveAsync(conn, country, ct);
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;         // 1.14.67 — enlist in outer persist transaction
                q.CommandText = $@"
                    SELECT DISTINCT w.BoxNo
                      FROM {diagWhSrc} w
                      INNER JOIN #DiagBoxes b ON b.BoxNo = w.BoxNo
                     WHERE EXISTS (
                          SELECT 1 FROM dbo.LPM_SimItemSkuMax sm
                           WHERE sm.Country  = @country
                             AND sm.Year1    = @y
                             AND sm.Month1   = @m
                             AND sm.ItemCode = w.itemcode
                             AND sm.SKUMax   > 0);";
                q.Parameters.Add(new SqlParameter("@country", country));
                q.Parameters.Add(new SqlParameter("@y", year));
                q.Parameters.Add(new SqlParameter("@m", month));
                q.CommandTimeout = 120;
                using var rdr = await q.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    if (rdr.IsDBNull(0)) continue;
                    boxesWithViableLines.Add(rdr.GetString(0));
                }
            }

            using (var cleanup = conn.CreateCommand())
            {
                cleanup.Transaction = tx;   // 1.14.67 — enlist in outer persist transaction
                cleanup.CommandText = "DROP TABLE IF EXISTS #DiagBoxes;";
                await cleanup.ExecuteNonQueryAsync(ct);
            }
        }
        catch
        {
            // Best-effort. If the viability query fails, classifier falls
            // back to the old behaviour (UNKNOWN for "no trace, no output").
            boxesWithViableLines.Clear();
        }

        using var dt = new System.Data.DataTable();
        dt.Columns.Add("LPMBatchNo", typeof(long));
        dt.Columns.Add("BoxNo",      typeof(string));
        dt.Columns.Add("PalletNo",   typeof(string));
        dt.Columns.Add("LPMDt",      typeof(DateTime));
        dt.Columns.Add("BoxKind",    typeof(string));
        dt.Columns.Add("BoxQty",     typeof(long));
        dt.Columns.Add("SimQty",     typeof(long));
        dt.Columns.Add("TopReason",  typeof(string));
        dt.Columns.Add("Reasons",    typeof(string));
        dt.Columns.Add("CreateTS",   typeof(DateTime));

        dt.BeginLoadData();
        foreach (var (boxNo, summary) in eligibleBoxes)
        {
            simQtyByBox.TryGetValue(boxNo, out var simQty);
            var remaining = summary.BoxQty - simQty;
            if (remaining <= 0) continue;   // fully allocated — not in diagnostic

            string topReason;
            string reasons;

            if (filteredOutBoxes.Contains(boxNo))
            {
                topReason = "FILTERED_SEASON";
                reasons   = "FILTERED_SEASON (all items dropped by per-item Season filter)";
            }
            else if (excludedByRuleBoxes.Contains(boxNo))
            {
                // 1.14.28 — Pre-allocator analysis says every item in this
                // box has SKUMax = 0 at every store eligible for the item's
                // division. That's an explicit Rule 1-7 exclusion, not a
                // capacity cap — distinguish so the planner can tell
                // "blocked on purpose" from "no headroom this period". Check
                // ranks ABOVE the trace check because SKIP_NO_DIV / NO_EOM
                // can co-fire with exclusions (e.g. an excluded item also
                // happens to lack a division mapping); the more specific
                // signal wins.
                topReason = "EXCLUDED_BY_RULE";
                reasons   = $"EXCLUDED_BY_RULE — {remaining} qty unallocated; every item in this box has SKUMax = 0 at every store eligible for its division. Set by SKU Max Rules 1-7 (Exclude/Subclass/Transfer/MFCS/PriceMaxQty=0/StoreDivAccess/StoreDeptAccess).";
            }
            else
            {
                // Aggregate skip counts for this box.
                var perBox = skipCountsByBox
                    .Where(kv => string.Equals(kv.Key.Box, boxNo, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key.Reason, kv => kv.Value);

                if (perBox.Count > 0)
                {
                    var ordered = perBox.OrderByDescending(kv => kv.Value).ToList();
                    topReason = ordered[0].Key;     // SKIP_NO_DIV or SKIP_NO_EOM in practice
                    reasons   = string.Join(" · ",
                        ordered.Select(kv => $"{kv.Key} ({kv.Value})"));
                }
                else if (sawAllocByBox.Contains(boxNo) || simQty > 0)
                {
                    // Box reached allocator (or got partial allocation) but
                    // the trace has no SKIP_* rows — almost certainly because
                    // VerboseTrace=false dropped SKIP_SKUMAX / SKIP_TARGET.
                    // Attribute the gap to CAP.
                    topReason = "CAP";
                    reasons   = $"CAP — {remaining} qty unallocated; SKU Max or EOM Merch Need (Week) cap saturated at every eligible store. Enable VerboseTrace on the run to see the precise SKU vs Target split in the Allocation Trace tab.";
                }
                else if (boxesWithViableLines.Contains(boxNo))
                {
                    // 1.14.44 — Box has items with SKUMax > 0 in this period's
                    // snapshot. The allocator definitely iterated this box's
                    // lines but found zero headroom at every (Store, Item)
                    // combination — likely because the live SOH at allocation
                    // time exceeded SKUMax at every eligible store (the
                    // SkuMax build's snapshot was stale relative to the
                    // current LocStock). In non-verbose mode the SKIP_SKUMAX
                    // rows get dropped, leaving zero trace for the box.
                    // Pre-1.14.44 this fell to UNKNOWN; now correctly CAP.
                    topReason = "CAP";
                    reasons   = $"CAP — {remaining} qty unallocated; box's items have viable SkuMax rows but the allocator found 0 headroom at every eligible store (likely SOH ≥ SKUMax at allocation time after the build's snapshot was taken). Enable VerboseTrace for the per-store SKU Balance / Target Remain split.";
                }
                else
                {
                    // Box was in the eligible set, not filtered out, but
                    // didn't surface in output OR trace at all, AND none of
                    // its items have a SKUMax > 0 row for this period.
                    // Genuinely no viable allocation pathway — true UNKNOWN.
                    topReason = "UNKNOWN";
                    reasons   = "UNKNOWN — box was eligible but produced no output, no trace rows, and no item in the box has a SKUMax > 0 row in this period's snapshot. Investigate the SKU Max build coverage.";
                }
            }

            // Truncate Reasons to nvarchar(400) limit defensively.
            if (reasons.Length > 400) reasons = reasons[..397] + "…";

            dt.Rows.Add(
                batchNo,
                boxNo,
                (object?)summary.PalletNo  ?? DBNull.Value,
                (object?)summary.LPMDt     ?? DBNull.Value,
                summary.BoxKind,
                summary.BoxQty,
                simQty,
                topReason,
                reasons,
                createTs);
        }
        // 1.14.70 — Emit one CLOSED_BOX row per closed box. These never entered
        // the allocator (ReadBoxesAsync filtered them out), so they're absent
        // from `eligibleBoxes`. We still want them in the Gap list so a planner
        // checking "why didn't this box ship?" sees a clear "marked closed in
        // [source]" answer instead of "no record found". SimQty is always 0 for
        // closed boxes; BoxQty + PalletNo + LPMDt come from the meta captured
        // in ReadBoxesAsync's IsClosed branch. The country-specific source
        // text in `reasons` lets ops trace back to the controlling table.
        var closedReasonSource = string.Equals(country, "UAE", StringComparison.OrdinalIgnoreCase)
            ? "USA.dbo.upcboxhead.Closed = 'Y'"
            : "[<DataName>].dbo.Exclude_Transfers_Sim.Trfno or [<DataName>].dbo.CloseR1Pallet.palletno";
        foreach (var (boxNo, meta) in closedBoxes)
        {
            var closedReasons = $"CLOSED_BOX — {meta.BoxQty} qty not shipped; box is flagged as closed in {closedReasonSource}. The SIM allocator skipped it entirely so the closed-box flag is honoured downstream.";
            if (closedReasons.Length > 400) closedReasons = closedReasons[..397] + "…";
            dt.Rows.Add(
                batchNo,
                boxNo,
                (object?)meta.PalletNo ?? DBNull.Value,
                (object?)meta.LPMDt    ?? DBNull.Value,
                meta.BoxKind,
                meta.BoxQty,
                0L,                                // SimQty — closed boxes are never allocated
                "CLOSED_BOX",
                closedReasons,
                createTs);
        }
        dt.EndLoadData();

        if (dt.Rows.Count == 0) return;    // every eligible box fully allocated — nothing to write

        // 1.14.67 — `tx` enrols the diagnostic insert in the outer persist
        // transaction. TableLock is preserved here — UnallocatedDiagnostic is
        // small (one row per box, typically <10K rows) and is NOT read by any
        // concurrent UI path during Generate, so the BU lock is fine.
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, tx)
        {
            DestinationTableName = "dbo.LPMSIM_UnallocatedDiagnostic",
            BatchSize            = 50_000,
            BulkCopyTimeout      = 300,
            EnableStreaming      = true,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    /// <summary>Comparer for the (BoxNo, SkipReason) dictionary used inside
    /// <see cref="BuildAndInsertUnallocatedDiagnosticAsync"/>. BoxNo is
    /// case-insensitive (same as the other dictionaries); Reason is
    /// ordinal (decision codes are stable case-sensitive literals).</summary>
    private sealed class BoxReasonComparer : IEqualityComparer<(string Box, string Reason)>
    {
        public static readonly BoxReasonComparer Instance = new();
        public bool Equals((string Box, string Reason) x, (string Box, string Reason) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Box, y.Box)
            && StringComparer.Ordinal.Equals(x.Reason, y.Reason);
        public int GetHashCode((string Box, string Reason) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Box    ?? ""),
                StringComparer.Ordinal.GetHashCode(obj.Reason ?? ""));
    }

    // 1.14.67 — `tx` enrols the bulk copy in the outer persist transaction.
    private static async Task BulkInsertTraceAsync(SqlConnection conn, List<LpmSimAllocTrace> rows, CancellationToken ct, SqlTransaction? tx = null)
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
        dt.Columns.Add("IsOverride",       typeof(bool));
        dt.Columns.Add("CreateTS",         typeof(DateTime));

        dt.BeginLoadData();
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
                r.IsOverride,
                r.CreateTS);
        }
        dt.EndLoadData();

        // Phase F perf fix — streaming + larger batch.
        // 1.14.67 — TableLock REMOVED (same reason as BulkInsertOutputAsync
        // above — see the longer comment there). dbo.LPMSIM_AllocTrace is
        // read by the Allocation Trace tab in SIM Reports and the
        // BuildAndInsertUnallocatedDiagnosticAsync read-back; without
        // TableLock those queries can read past the inserter without
        // waiting for the bulk copy to finish.
        // 1.14.67 — `tx` enrols this bulk copy in the outer persist-phase
        // transaction so all trace rows for the batch ROLLBACK together if
        // any persist step fails.
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = "dbo.LPMSIM_AllocTrace",
            BatchSize            = 50_000,
            BulkCopyTimeout      = 600,
            EnableStreaming      = true,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    // 1.14.67 — `tx` enrols the bulk copy in the outer persist transaction.
    private static async Task BulkInsertStoreItemBalancesAsync(
        SqlConnection conn, long batchNo, DateTime createTs,
        Dictionary<(string, string), int> sohMap,
        Dictionary<(string Store, string Item), int> skuMaxByStoreItem,
        Dictionary<string, int> itemDiv,
        Dictionary<(string Store, string Item), int> p1n,
        Dictionary<(string Store, string Item), int> p1r,
        Dictionary<(string Store, string Item), int> p2n,
        Dictionary<(string Store, string Item), int> p2r,
        CancellationToken ct,
        SqlTransaction? tx = null)
    {
        // Collect every (Store,Item) that appears in any phase totals — case-insensitive
        // so duplicates that differ only in case are merged (matches SQL PK collation).
        var keys = new HashSet<(string, string)>(StoreItemComparer.Instance);
        keys.UnionWith(p1n.Keys);
        keys.UnionWith(p1r.Keys);
        keys.UnionWith(p2n.Keys);
        keys.UnionWith(p2r.Keys);
        if (keys.Count == 0) return;

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
            // Per-(Store, Item) SKU Max from LPM_SimItemSkuMax — captured for
            // the snapshot so this table reads consistently with allocation.
            int? skuMax = skuMaxByStoreItem.TryGetValue((store, item), out var sm) ? sm : (int?)null;
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

        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = "dbo.LPMSIM_StoreItemBalance",
            BatchSize            = 5000,
            BulkCopyTimeout      = 600,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    // 1.14.67 — `tx` enrols the bulk copy in the outer persist transaction.
    private static async Task BulkInsertStoreDivBalancesAsync(
        SqlConnection conn, long batchNo, DateTime createTs,
        Dictionary<(string Store, int Div), int> sohByStoreDiv,
        Dictionary<int, List<EomStore>> eomByDiv,
        Dictionary<(string Store, int Div), int> p1n,
        Dictionary<(string Store, int Div), int> p1r,
        Dictionary<(string Store, int Div), int> p2n,
        Dictionary<(string Store, int Div), int> p2r,
        CancellationToken ct,
        SqlTransaction? tx = null)
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

        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
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
    // 1.14.67 — `tx` enrols the bulk copy in the outer persist transaction.
    private static async Task BulkInsertBoxBalancesAsync(
        SqlConnection conn, long batchNo, DateTime createTs,
        List<BoxItem> lpmBoxes, List<BoxItem> nonLpmBoxes,
        Dictionary<(string Box, string Phase), long> boxAllocByPhase,
        CancellationToken ct,
        SqlTransaction? tx = null)
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

        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
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
