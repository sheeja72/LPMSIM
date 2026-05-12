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
/// <param name="WhStock">Σ <c>whboxitems.Qty</c> for purchased boxes whose
///   pallet category is anything OTHER than 'NON ELIGIBLE'.</param>
/// <param name="Variance"><c>HoStock − WhStock</c>. Pre-computed in SQL so
///   sort/export behave consistently.</param>
/// <param name="ReservedStock">Σ Qty where PalletCategory='RESERVED' AND purchased.</param>
/// <param name="SeasonalStock">Σ Qty where PalletCategory='SEASONAL' AND purchased.</param>
/// <param name="OnHoldStock">Σ Qty where PalletCategory='ON HOLD' AND purchased.</param>
/// <param name="EligibleStock">Σ Qty where PalletCategory='ELIGIBLE' AND purchased.</param>
/// <param name="NonLpmStock">Σ Qty where LPM is NULL or empty — pure
///   LPM-column check, no pallet category or ShopEligible restriction
///   (per user spec).</param>
/// <param name="LpmStock">Σ Qty where LPM is set — pure LPM-column check,
///   no pallet category or ShopEligible restriction.</param>
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
    long LpmStock);

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

        // Optional Division filter (multi-select). Applied identically to
        // BOTH the HO CTE and the WH CTE so the totals stay aligned.
        var divFilterFrag = "";
        var divParms = new List<SqlParameter>();
        if (filter.Divisions is { Count: > 0 })
        {
            var sb = new StringBuilder(" AND (");
            for (int i = 0; i < filter.Divisions.Count; i++)
            {
                if (i > 0) sb.Append(" OR ");
                var name = $"@div{i}";
                sb.Append("sm.Division = ").Append(name);
                divParms.Add(new SqlParameter(name, filter.Divisions[i]));
            }
            sb.Append(')');
            divFilterFrag = sb.ToString();
        }

        // Season filter — encoded as a single @season param the SQL inspects.
        // 'A' = All (no filter); 'W' = Winter; 'S' = Summer. WH side reads
        // whboxitems.Season directly (per user spec — distinct from the
        // existing EOM code which uses pallettype.Season); HO side derives
        // season from UPCBarcodes.Itemtype.
        var seasonCode = filter.Season switch
        {
            WhHoSeason.Summer => "S",
            WhHoSeason.Winter => "W",
            _                 => "A",
        };

        var sql = $@"
            ;WITH ItemDiv AS (
                -- One Division per item. An item that maps to multiple
                -- subclasses keeps the alphabetically-first Division so
                -- the join below is single-valued.
                SELECT u.itemcode, MIN(sm.Division) AS Division
                  FROM Datareporting.dbo.upc_subclass    u
                  INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                 WHERE u.itemcode IS NOT NULL AND u.itemcode <> ''
                 GROUP BY u.itemcode
            ),
            ItemSeason AS (
                -- HO season per item: 'W' if any barcode is W, else 'S'.
                -- Items with no barcode row default to 'S' (via the LEFT
                -- JOIN + ISNULL in HOByDiv below).
                SELECT b.itemcode,
                       MAX(CASE WHEN UPPER(LTRIM(RTRIM(b.Itemtype))) = 'W' THEN 'W' ELSE 'S' END) AS Season
                  FROM usa.dbo.upcbarcodes b
                 WHERE b.itemcode IS NOT NULL AND b.itemcode <> ''
                 GROUP BY b.itemcode
            ),
            HOByDiv AS (
                -- HO Stock per Division. storeid filter is dynamic — UAE
                -- gets the literal 'HODATA'; other countries get every
                -- storeid where DataSettings.ExportWH='Y' for the country.
                SELECT sm.Division,
                       SUM(CAST(ISNULL(ls.SOH, 0) AS bigint)) AS HOStock
                  FROM racks.dbo.LPM_LocStock ls
                  INNER JOIN ItemDiv id ON id.itemcode = ls.ItemCode
                  -- Re-join subclass→division to apply the optional
                  -- Division filter without losing the row → Division mapping.
                  INNER JOIN (
                      SELECT itemcode, Division
                        FROM ItemDiv
                  ) sm ON sm.itemcode = ls.ItemCode
                  LEFT  JOIN ItemSeason its ON its.itemcode = ls.ItemCode
                 WHERE ls.storeid IN ({hoSb})
                   AND ls.ItemCode IS NOT NULL AND ls.ItemCode <> ''
                   AND (@season = 'A' OR ISNULL(its.Season, 'S') = @season)
                   {divFilterFrag}
                 GROUP BY sm.Division
            ),
            WHByDiv AS (
                -- WH side. Season filter reads w.Season directly (per user
                -- spec — values follow the same 'W' / else convention).
                -- All category columns require purchased = ShopEligible
                -- IS NULL OR <> 'E'. WH Stock excludes 'NON ELIGIBLE'.
                -- Non-LPM / LPM Stock have NO category or purchased
                -- restriction — pure LPM-column check.
                SELECT sm.Division,
                       SUM(CASE WHEN UPPER(ISNULL(pt.PalletCategory, '')) <> 'NON ELIGIBLE'
                                 AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS WHStock,
                       SUM(CASE WHEN UPPER(ISNULL(pt.PalletCategory, '')) = 'RESERVED'
                                 AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS ReservedStock,
                       SUM(CASE WHEN UPPER(ISNULL(pt.PalletCategory, '')) = 'SEASONAL'
                                 AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS SeasonalStock,
                       SUM(CASE WHEN UPPER(ISNULL(pt.PalletCategory, '')) = 'ON HOLD'
                                 AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS OnHoldStock,
                       SUM(CASE WHEN UPPER(ISNULL(pt.PalletCategory, '')) = 'ELIGIBLE'
                                 AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS EligibleStock,
                       SUM(CASE WHEN w.LPM IS NULL OR w.LPM = ''
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS NonLpmStock,
                       SUM(CASE WHEN w.LPM IS NOT NULL AND w.LPM <> ''
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LpmStock
                  FROM {whSrc} w
                  INNER JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
                  OUTER APPLY (
                      SELECT TOP 1 sm0.Division
                        FROM Datareporting.dbo.upc_subclass    u
                        INNER JOIN Datareporting.dbo.subclassmaster sm0 ON sm0.MH4ID = u.MH4ID
                       WHERE u.itemcode = w.ItemCode
                       ORDER BY sm0.Division
                  ) sm
                 WHERE sm.Division IS NOT NULL
                   AND (@season = 'A'
                        OR (@season = 'W' AND UPPER(ISNULL(w.Season, '')) = 'W')
                        OR (@season = 'S' AND UPPER(ISNULL(w.Season, '')) <> 'W'))
                   {divFilterFrag}
                 GROUP BY sm.Division
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
                ISNULL(w.LpmStock,           0)    AS LpmStock
              FROM HOByDiv h
              FULL OUTER JOIN WHByDiv w ON w.Division = h.Division
             WHERE COALESCE(h.Division, w.Division) IS NOT NULL
             ORDER BY Division;";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        foreach (var p in hoParms)  cmd.Parameters.Add(p);
        foreach (var p in divParms) cmd.Parameters.Add(p);
        cmd.Parameters.Add(new SqlParameter("@season", seasonCode));

        var rows = new List<WhHoStockRow>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new WhHoStockRow(
                Division:      rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                HoStock:       rdr.IsDBNull(1) ? 0L : rdr.GetInt64(1),
                WhStock:       rdr.IsDBNull(2) ? 0L : rdr.GetInt64(2),
                Variance:      rdr.IsDBNull(3) ? 0L : rdr.GetInt64(3),
                ReservedStock: rdr.IsDBNull(4) ? 0L : rdr.GetInt64(4),
                SeasonalStock: rdr.IsDBNull(5) ? 0L : rdr.GetInt64(5),
                OnHoldStock:   rdr.IsDBNull(6) ? 0L : rdr.GetInt64(6),
                EligibleStock: rdr.IsDBNull(7) ? 0L : rdr.GetInt64(7),
                NonLpmStock:   rdr.IsDBNull(8) ? 0L : rdr.GetInt64(8),
                LpmStock:      rdr.IsDBNull(9) ? 0L : rdr.GetInt64(9)));
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
