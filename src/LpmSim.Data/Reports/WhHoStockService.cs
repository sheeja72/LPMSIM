using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using LpmSim.Data.Warehouse;

namespace LpmSim.Data.Reports;

/// <summary>
/// Page-level Season filter for the WH/HO Stock report. <see cref="All"/>
/// applies no season predicate (sums both summer + winter on both sides).
/// Summer/Winter are inclusive single-season views — for the HO side the
/// season comes from <c>UPCBarcodes.Itemtype</c> ('W' → Winter, else
/// Summer); for the WH side it comes from <c>whboxitems.Season</c> using
/// the same convention.
/// </summary>
public enum WhHoSeason
{
    All = 0,
    Summer = 1,
    Winter = 2,
}

/// <summary>
/// Page filters for the WH/HO Stock report.
/// </summary>
/// <param name="Country">SIMCountry — drives the whboxitems source AND the
///   HO-storeids lookup. UAE uses literal <c>storeid='HODATA'</c>; other
///   countries resolve storeids from
///   <c>bfldata..DataSettings WHERE country=&lt;dataname&gt; AND ExportWH='Y'</c>.</param>
/// <param name="Divisions">Optional Division filter (multi-select). Null/empty = all divisions.</param>
/// <param name="Season">Page-level season filter — see <see cref="WhHoSeason"/>.</param>
public record WhHoStockFilter(
    string Country,
    IReadOnlyList<string>? Divisions,
    WhHoSeason Season);

/// <summary>
/// One row of the WH/HO Stock report — one per Division. All quantity
/// columns are signed bigints so they can carry the sums verbatim from
/// SQL Server's bigint aggregates.
/// </summary>
/// <param name="Division">Division name (from subclassmaster).</param>
/// <param name="HoStock">Σ <c>LPM_LocStock.SOH</c> for the HO storeids
///   resolved for the country, filtered to the page Season via
///   <c>UPCBarcodes.Itemtype</c>.</param>
/// <param name="WhStock">Σ <c>whboxitems.Qty</c> for boxes that satisfy
///   the universal WH rule: <c>ShopEligible &lt;&gt; 'E'</c> AND
///   <c>PalletCategory NOT IN ('NON ELIGIBLE', 'ECOM')</c>.</param>
/// <param name="Variance"><c>HoStock − WhStock</c>. Pre-computed in SQL so
///   sort/export behave consistently.</param>
/// <param name="ReservedStock">Σ Qty where PalletCategory='RESERVED' AND
///   <c>ShopEligible &lt;&gt; 'E'</c>.</param>
/// <param name="SeasonalStock">Σ Qty where PalletCategory='SEASONAL' AND
///   <c>ShopEligible &lt;&gt; 'E'</c>.</param>
/// <param name="OnHoldStock">Σ Qty where PalletCategory='ON HOLD' AND
///   <c>ShopEligible &lt;&gt; 'E'</c>.</param>
/// <param name="EligibleStock">Σ Qty where PalletCategory='ELIGIBLE' AND
///   <c>ShopEligible &lt;&gt; 'E'</c>.</param>
/// <param name="NonLpmStock">Σ Qty where LPM is NULL or empty AND
///   satisfies the universal WH rule (1.13.2 — was previously
///   unrestricted; tightened so reconcile with raw SSMS query works).</param>
/// <param name="LpmStock">Σ Qty where LPM is set AND satisfies the
///   universal WH rule.</param>
/// <param name="LpmEligibleStock">1.14.72 — Σ Qty where LPM is set AND
///   <c>PalletCategory='ELIGIBLE'</c> AND <c>ShopEligible &lt;&gt; 'E'</c>.
///   Intersection of <see cref="EligibleStock"/> and <see cref="LpmStock"/>.</param>
/// <param name="NonLpmEligibleStock">1.14.72 — Σ Qty where LPM is NULL or
///   empty AND <c>PalletCategory='ELIGIBLE'</c> AND
///   <c>ShopEligible &lt;&gt; 'E'</c>. Intersection of <see cref="EligibleStock"/>
///   and <see cref="NonLpmStock"/>.</param>
public record WhHoStockRow(
    string Division,
    long HoStock,
    long WhStock,
    long Variance,
    long ReservedStock,
    long SeasonalStock,
    long OnHoldStock,
    long EligibleStock,
    long NonLpmStock,
    long LpmStock,
    long LpmEligibleStock,
    long NonLpmEligibleStock);

/// <summary>
/// Data access for the Reports → WH/HO Stock page. Builds one Division ×
/// (HO, WH, category breakdowns) row using a single batched query so the
/// network round-trip stays low even when many divisions match. Country
/// drives both the whboxitems source table (UAE → racks.dbo.whboxitems;
/// others → [&lt;DataName&gt;].dbo.WHBoxItemsExport) and the HO storeid
/// list (UAE → literal 'HODATA'; others → query DataSettings).
/// </summary>
public class WhHoStockService(IConfiguration cfg)
{
    private readonly string _connStr =
        cfg.GetConnectionString("Warehouse")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:Warehouse (set via User Secrets).");

    /// <summary>
    /// Run the WH/HO Stock report for the given filters. Returns one row
    /// per Division that has activity on either side (HO or WH) — divisions
    /// with all-zero values across the board are still included via the
    /// FULL OUTER JOIN so the planner can see "0/0" cells.
    /// </summary>
    public async Task<List<WhHoStockRow>> GetAsync(WhHoStockFilter filter, CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Resolve country-aware whboxitems source (same helper used by the
        // Warehouse Boxes page — keeps the UAE/non-UAE switch in one place).
        var whSrc = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);

        // Resolve HO storeids per country. UAE uses the literal 'HODATA'
        // marker; other countries pull every storeid where ExportWH='Y'
        // from bfldata..DataSettings. The values flow into an IN-clause
        // built below so a country with multiple WH storeids sums them.
        var hoStoreIds = await ResolveHoStoreIdsAsync(conn, filter.Country, ct);
        if (hoStoreIds.Count == 0)
        {
            // No HO storeids configured = HO side returns nothing. Rather
            // than show empty divisions misleadingly, surface the issue.
            throw new InvalidOperationException(
                $"No HO storeids found for country '{filter.Country}'. " +
                "UAE expects the literal 'HODATA'; other countries need at least one " +
                "DataSettings row with ExportWH='Y'.");
        }

        // Build the dynamic IN-clause for HO storeids.
        var hoSb = new StringBuilder();
        var hoParms = new List<SqlParameter>();
        for (int i = 0; i < hoStoreIds.Count; i++)
        {
            if (i > 0) hoSb.Append(", ");
            var name = $"@ho{i}";
            hoSb.Append(name);
            hoParms.Add(new SqlParameter(name, hoStoreIds[i]));
        }

        // Optional Division filter (multi-select). Single fragment now —
        // both CTEs reference the same id.Division alias from the
        // LEFT JOIN ItemDiv (1.13.2 simplified the WH side to match the HO
        // side, dropping the per-row OUTER APPLY in favour of a JOIN to
        // the pre-aggregated CTE). Filter never picks the '(no division)'
        // bucket, so it's naturally excluded when specific divisions are
        // chosen — the unmapped row only appears when no Division filter
        // is active.
        var hoDivFilterFrag = "";
        var divParms = new List<SqlParameter>();
        if (filter.Divisions is { Count: > 0 })
        {
            var divSb = new StringBuilder(" AND (");
            for (int i = 0; i < filter.Divisions.Count; i++)
            {
                if (i > 0) divSb.Append(" OR ");
                var name = $"@div{i}";
                divSb.Append("id.Division = ").Append(name);
                divParms.Add(new SqlParameter(name, filter.Divisions[i]));
            }
            divSb.Append(')');
            hoDivFilterFrag = divSb.ToString();
        }

        // Season filter encoded for inline SQL fragments (1.14.73 perf —
        // see comment block below). 'A' = All (no filter); 'W' = Winter;
        // 'S' = Summer. WH side reads whboxitems.Season directly (per user
        // spec — distinct from the existing EOM code which uses
        // pallettype.Season); HO side derives season from UPCBarcodes.Itemtype.
        var seasonCode = filter.Season switch
        {
            WhHoSeason.Summer => "S",
            WhHoSeason.Winter => "W",
            _                 => "A",
        };

        // 1.14.73 perf — Inline season fragments instead of `(@season = 'A'
        // OR …)` OR-chains. The previous parameterised pattern made SQL
        // Server's optimizer guess at cardinality (parameter sniffing) and
        // sometimes pick poor plans on Winter / Summer runs. With inline
        // SQL the predicate is either present or completely absent, so
        // the plan reflects the actual filter.
        var whSeasonFilter = seasonCode switch
        {
            "W" => "AND UPPER(ISNULL(w.Season, '')) = 'W'",
            "S" => "AND UPPER(ISNULL(w.Season, '')) <> 'W'",
            _   => "",   // 'A' = no filter
        };
        var hoSeasonFilter = seasonCode switch
        {
            "W" => "AND ISNULL(its.Season, 'S') = 'W'",
            "S" => "AND ISNULL(its.Season, 'S') = 'S'",
            _   => "",   // 'A' = no filter
        };
        // 1.14.73 perf — When season = All, the #WhRptItemSeason table is
        // never consulted; skip both the build (a full scan of
        // usa.dbo.upcbarcodes) and the LEFT JOIN to it.
        var buildItemSeasonTable = seasonCode != "A";
        var hoSeasonJoinClause   = buildItemSeasonTable
            ? "LEFT  JOIN #WhRptItemSeason its ON its.itemcode = ls.ItemCode"
            : "";

        // The two universal WH-rule predicates that, pre-1.14.73, were
        // duplicated inside every SUM CASE on the WH side. Now hoisted into
        // the WHByDiv WHERE so SQL Server can short-circuit ~50-80% of
        // whboxitems rows before any CASE / aggregate runs.
        const string WhUniversalRule =
            "AND w.ShopEligible <> 'E' " +
            "AND UPPER(ISNULL(w.PalletCategory, '')) NOT IN ('NON ELIGIBLE', 'ECOM')";

        var sql = $@"
            SET NOCOUNT ON;

            -- 1.14.12 perf: ItemDiv and ItemSeason used to be CTEs; the
            -- optimizer often re-evaluated them across the HOByDiv/WHByDiv
            -- joins (especially with whboxitems on the WH side). Now
            -- materialized into indexed temp tables once, then the main
            -- query joins them as index seeks. Same data, single
            -- evaluation, typically 2-5x faster.
            -- 1.14.73 perf: #WhRptItemSeason is now built only when the
            -- Season filter is Winter or Summer (the All path doesn't
            -- consult it at all — see hoSeasonJoinClause / hoSeasonFilter).
            IF OBJECT_ID('tempdb..#WhRptItemDiv')    IS NOT NULL DROP TABLE #WhRptItemDiv;
            IF OBJECT_ID('tempdb..#WhRptItemSeason') IS NOT NULL DROP TABLE #WhRptItemSeason;

            -- (1) One Division per item from upc_subclass × subclassmaster.
            SELECT u.itemcode, MIN(sm.Division) AS Division
              INTO #WhRptItemDiv
              FROM Datareporting.dbo.upc_subclass    u
              INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
             WHERE u.itemcode IS NOT NULL AND u.itemcode <> ''
             GROUP BY u.itemcode;
            CREATE CLUSTERED INDEX IX_WhRptItemDiv ON #WhRptItemDiv (itemcode);

            {(buildItemSeasonTable ? @"
            -- (2) HO season per item: W if any barcode is W, else S.
            --     Items with no barcode row default to S (via LEFT JOIN +
            --     ISNULL in HOByDiv below — no row here = LEFT-join NULL).
            SELECT b.itemcode,
                   MAX(CASE WHEN UPPER(LTRIM(RTRIM(b.Itemtype))) = 'W' THEN 'W' ELSE 'S' END) AS Season
              INTO #WhRptItemSeason
              FROM usa.dbo.upcbarcodes b
             WHERE b.itemcode IS NOT NULL AND b.itemcode <> ''
             GROUP BY b.itemcode;
            CREATE CLUSTERED INDEX IX_WhRptItemSeason ON #WhRptItemSeason (itemcode);" : "")}

            ;WITH HOByDiv AS (
                -- HO Stock per Division. storeid filter is dynamic — UAE
                -- gets the literal 'HODATA'; other countries get every
                -- storeid where DataSettings.ExportWH='Y' for the country.
                --
                -- 1.13.1 fix: LEFT JOIN ItemDiv (was INNER) so items with no
                -- subclass→division mapping still contribute to the page
                -- total, bucketed as '(no division)'. This makes the page
                -- HO-total reconcile with SELECT SUM(SOH) FROM LPM_LocStock
                -- WHERE storeid IN (...).
                -- Also dropped the previous ItemCode IS NOT NULL guard so a
                -- direct sum matches exactly — null-itemcode rows still fall
                -- into '(no division)' via the LEFT JOIN.
                SELECT ISNULL(id.Division, '(no division)') AS Division,
                       SUM(CAST(ISNULL(ls.SOH, 0) AS bigint)) AS HOStock
                  FROM racks.dbo.LPM_LocStock ls
                  LEFT  JOIN #WhRptItemDiv id ON id.itemcode = ls.ItemCode
                  {hoSeasonJoinClause}
                 WHERE ls.storeid IN ({hoSb})
                   {hoSeasonFilter}
                   {hoDivFilterFrag}
                 GROUP BY ISNULL(id.Division, '(no division)')
            ),
            WHByDiv AS (
                -- WH side. Season filter reads w.Season directly (per spec).
                -- 1.13.2 perf: the previous OUTER APPLY ran a per-row subquery
                -- against upc_subclass × subclassmaster to find each box's
                -- division — N+1 against a multi-million-row whboxitems
                -- scanned every page Load. Replaced with a single LEFT JOIN
                -- to the ItemDiv CTE (already aggregated to one row per
                -- itemcode above). Same semantics, much faster — the
                -- planner now waits seconds instead of tens of seconds.
                --
                -- 1.13.2 rule: ALL whboxitems-sourced columns apply the same
                -- eligibility rule as raw SSMS queries:
                --     ShopEligible <> 'E'    (excludes 'E' AND NULL — matches the planner's reference query)
                --     AND PalletCategory NOT IN ('NON ELIGIBLE', 'ECOM')
                --
                -- 1.14.73 perf: Hoisted the universal rule above into the
                -- WHERE clause (was previously duplicated inside every
                -- SUM CASE). SQL Server now short-circuits ~50-80% of
                -- whboxitems rows before any CASE / aggregate evaluates,
                -- so the remaining SUM CASEs only have to weigh the
                -- specific category / LPM-presence filter that's unique to
                -- each column. WHStock collapses to a plain SUM since
                -- every surviving row counts. Reserved / Seasonal / On Hold
                -- / Eligible keep their category-specific CASE; LPM /
                -- Non-LPM / *Eligible keep their LPM-presence CASE.
                SELECT ISNULL(id.Division, '(no division)') AS Division,
                       SUM(CAST(ISNULL(w.Qty, 0) AS bigint)) AS WHStock,
                       SUM(CASE WHEN UPPER(ISNULL(w.PalletCategory, '')) = 'RESERVED'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS ReservedStock,
                       SUM(CASE WHEN UPPER(ISNULL(w.PalletCategory, '')) = 'SEASONAL'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS SeasonalStock,
                       SUM(CASE WHEN UPPER(ISNULL(w.PalletCategory, '')) = 'ON HOLD'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS OnHoldStock,
                       SUM(CASE WHEN UPPER(ISNULL(w.PalletCategory, '')) = 'ELIGIBLE'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS EligibleStock,
                       SUM(CASE WHEN (w.LPM IS NULL OR w.LPM = '')
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS NonLpmStock,
                       SUM(CASE WHEN w.LPM IS NOT NULL AND w.LPM <> ''
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LpmStock,
                       -- 1.14.72 — Intersection of LPM AND Eligible.
                       SUM(CASE WHEN w.LPM IS NOT NULL AND w.LPM <> ''
                                 AND UPPER(ISNULL(w.PalletCategory, '')) = 'ELIGIBLE'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LpmEligibleStock,
                       -- 1.14.72 — Intersection of Non-LPM AND Eligible.
                       SUM(CASE WHEN (w.LPM IS NULL OR w.LPM = '')
                                 AND UPPER(ISNULL(w.PalletCategory, '')) = 'ELIGIBLE'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS NonLpmEligibleStock
                  FROM {whSrc} w
                  LEFT JOIN #WhRptItemDiv id ON id.itemcode = w.ItemCode
                 WHERE 1 = 1
                   {WhUniversalRule}
                   {whSeasonFilter}
                   {hoDivFilterFrag}
                 GROUP BY ISNULL(id.Division, '(no division)')
            )
            SELECT
                COALESCE(h.Division, w.Division)   AS Division,
                ISNULL(h.HOStock,            0)    AS HOStock,
                ISNULL(w.WHStock,            0)    AS WHStock,
                ISNULL(h.HOStock,            0) - ISNULL(w.WHStock, 0) AS Variance,
                ISNULL(w.ReservedStock,      0)    AS ReservedStock,
                ISNULL(w.SeasonalStock,      0)    AS SeasonalStock,
                ISNULL(w.OnHoldStock,        0)    AS OnHoldStock,
                ISNULL(w.EligibleStock,      0)    AS EligibleStock,
                ISNULL(w.NonLpmStock,        0)    AS NonLpmStock,
                ISNULL(w.LpmStock,           0)    AS LpmStock,
                ISNULL(w.LpmEligibleStock,   0)    AS LpmEligibleStock,    -- 1.14.72
                ISNULL(w.NonLpmEligibleStock, 0)   AS NonLpmEligibleStock  -- 1.14.72
              FROM HOByDiv h
              FULL OUTER JOIN WHByDiv w ON w.Division = h.Division
             WHERE COALESCE(h.Division, w.Division) IS NOT NULL
             ORDER BY Division;";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        foreach (var p in hoParms)  cmd.Parameters.Add(p);
        foreach (var p in divParms) cmd.Parameters.Add(p);
        // 1.14.73 — @season parameter removed; the SQL now uses inline
        // conditional fragments built from seasonCode at C# string-build
        // time (whSeasonFilter / hoSeasonFilter / hoSeasonJoinClause).

        var rows = new List<WhHoStockRow>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new WhHoStockRow(
                Division:           rdr.IsDBNull(0)  ? "" : rdr.GetString(0),
                HoStock:            rdr.IsDBNull(1)  ? 0L : rdr.GetInt64(1),
                WhStock:            rdr.IsDBNull(2)  ? 0L : rdr.GetInt64(2),
                Variance:           rdr.IsDBNull(3)  ? 0L : rdr.GetInt64(3),
                ReservedStock:      rdr.IsDBNull(4)  ? 0L : rdr.GetInt64(4),
                SeasonalStock:      rdr.IsDBNull(5)  ? 0L : rdr.GetInt64(5),
                OnHoldStock:        rdr.IsDBNull(6)  ? 0L : rdr.GetInt64(6),
                EligibleStock:      rdr.IsDBNull(7)  ? 0L : rdr.GetInt64(7),
                NonLpmStock:        rdr.IsDBNull(8)  ? 0L : rdr.GetInt64(8),
                LpmStock:           rdr.IsDBNull(9)  ? 0L : rdr.GetInt64(9),
                // 1.14.72 — new intersections.
                LpmEligibleStock:    rdr.IsDBNull(10) ? 0L : rdr.GetInt64(10),
                NonLpmEligibleStock: rdr.IsDBNull(11) ? 0L : rdr.GetInt64(11)));
        }
        return rows;
    }

    /// <summary>
    /// Resolve the list of HO storeids for the given country.
    /// UAE → literal "HODATA". Other countries → every storeid from
    /// bfldata..DataSettings where the SIMCountry matches AND ExportWH='Y'.
    /// </summary>
    private static async Task<List<string>> ResolveHoStoreIdsAsync(
        SqlConnection conn, string country, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(country) ||
            country.Equals("UAE", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "HODATA" };
        }

        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT storeid
              FROM bfldata.dbo.DataSettings
             WHERE SIMCountry = @c
               AND ExportWH = 'Y'
               AND storeid IS NOT NULL
               AND LTRIM(RTRIM(storeid)) <> '';";
        var p = cmd.CreateParameter();
        p.ParameterName = "@c";
        p.Value         = country;
        cmd.Parameters.Add(p);
        cmd.CommandTimeout = 30;
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            if (!rdr.IsDBNull(0))
            {
                var s = rdr.GetString(0).Trim();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
        }
        return result;
    }
}
