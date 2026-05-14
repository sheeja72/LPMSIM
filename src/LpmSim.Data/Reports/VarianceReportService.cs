using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using LpmSim.Data.Warehouse;

namespace LpmSim.Data.Reports;

/// <summary>
/// Page filters for the Item-level Variance Report. Same shape as
/// <see cref="WhHoStockFilter"/> so the planner gets a consistent filter
/// bar across the two Reports pages.
/// </summary>
public record VarianceFilter(
    string Country,
    IReadOnlyList<string>? Divisions,
    WhHoSeason Season,
    /// <summary>Optional Itemcode contains-match (case-insensitive). Free-text narrowing.</summary>
    string? ItemSearch);

/// <summary>One row of the Variance Report — one per (Item × Division).</summary>
/// <param name="ItemCode">Item code (raw key, may have leading zeros).</param>
/// <param name="ItemName">Description from <c>HODATA.dbo.Itemmaster</c> (may be empty if the item exists in stock but is missing from the master).</param>
/// <param name="Division">Division name (or <c>"(no division)"</c> when the item has no subclass→division mapping).</param>
/// <param name="HoStock">Σ <c>LPM_LocStock.SOH</c> for the HO storeids resolved for the country.</param>
/// <param name="WhStock">Σ <c>whboxitems.Qty</c> applying the universal WH rule:
///   <c>ShopEligible &lt;&gt; 'E' AND PalletCategory NOT IN ('NON ELIGIBLE', 'ECOM')</c>.</param>
/// <param name="Variance"><c>HoStock − WhStock</c>. Always non-zero in the
///   result set — items where HO and WH match exactly are filtered out
///   by the SQL (per spec: "only items which have Variance").</param>
public record VarianceRow(
    string ItemCode,
    string ItemName,
    string Division,
    long HoStock,
    long WhStock,
    long Variance);

/// <summary>
/// Item-level variance report. Same data sources as the Division-level
/// "WH Stock Position" report (<see cref="WhHoStockService"/>) but
/// aggregated per (ItemCode × Division) and filtered to rows where
/// HO ≠ WH so the planner can drill into the source of any
/// division-level variance.
/// </summary>
public class VarianceReportService(IConfiguration cfg)
{
    private readonly string _connStr =
        cfg.GetConnectionString("Warehouse")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:Warehouse (set via User Secrets).");

    public async Task<List<VarianceRow>> GetAsync(VarianceFilter filter, CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Country-aware whboxitems source (same helper as WH Stock Position
        // / Warehouse Boxes — single point of UAE↔non-UAE switching).
        var whSrc = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);

        // HO storeids — UAE uses the literal 'HODATA' marker; other
        // countries pull every storeid where ExportWH='Y' from
        // bfldata..DataSettings, exactly like WH Stock Position.
        var hoStoreIds = await ResolveHoStoreIdsAsync(conn, filter.Country, ct);
        if (hoStoreIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"No HO storeids found for country '{filter.Country}'. " +
                "UAE expects the literal 'HODATA'; other countries need at least one " +
                "DataSettings row with ExportWH='Y'.");
        }

        // Build dynamic IN-clause for HO storeids.
        var hoSb = new StringBuilder();
        var parms = new List<SqlParameter>();
        for (int i = 0; i < hoStoreIds.Count; i++)
        {
            if (i > 0) hoSb.Append(", ");
            var name = $"@ho{i}";
            hoSb.Append(name);
            parms.Add(new SqlParameter(name, hoStoreIds[i]));
        }

        // Optional Division filter (multi-select).
        var divFilterFrag = "";
        if (filter.Divisions is { Count: > 0 })
        {
            var divSb = new StringBuilder(" AND (");
            for (int i = 0; i < filter.Divisions.Count; i++)
            {
                if (i > 0) divSb.Append(" OR ");
                var name = $"@div{i}";
                divSb.Append("id.Division = ").Append(name);
                parms.Add(new SqlParameter(name, filter.Divisions[i]));
            }
            divSb.Append(')');
            divFilterFrag = divSb.ToString();
        }

        // Optional Itemcode contains-match (free text).
        var itemSearchFrag = "";
        if (!string.IsNullOrWhiteSpace(filter.ItemSearch))
        {
            // Applied to BOTH CTEs — must match the same item set on each side.
            itemSearchFrag = " AND CHARINDEX(@itemSearch, ItemCode_Col) > 0";
            parms.Add(new SqlParameter("@itemSearch", filter.ItemSearch.Trim()));
        }

        // Season filter — same code as WH Stock Position.
        var seasonCode = filter.Season switch
        {
            WhHoSeason.Summer => "S",
            WhHoSeason.Winter => "W",
            _                 => "A",
        };

        // The two ItemCode_Col placeholders below are substituted per-CTE
        // — HO uses ls.ItemCode, WH uses w.ItemCode. Keeps the search
        // fragment definition in one place.
        var hoSearch = itemSearchFrag.Replace("ItemCode_Col", "ls.ItemCode");
        var whSearch = itemSearchFrag.Replace("ItemCode_Col", "w.ItemCode");

        var sql = $@"
            SET NOCOUNT ON;

            -- 1.14.12 perf: same materialization pattern as WhHoStockService.
            -- ItemDiv and ItemSeason go into indexed temp tables once so the
            -- HO/WH side joins are simple index seeks instead of CTE
            -- expansions per row.
            IF OBJECT_ID('tempdb..#VarRptItemDiv')    IS NOT NULL DROP TABLE #VarRptItemDiv;
            IF OBJECT_ID('tempdb..#VarRptItemSeason') IS NOT NULL DROP TABLE #VarRptItemSeason;

            SELECT u.itemcode, MIN(sm.Division) AS Division
              INTO #VarRptItemDiv
              FROM Datareporting.dbo.upc_subclass    u
              INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
             WHERE u.itemcode IS NOT NULL AND u.itemcode <> ''
             GROUP BY u.itemcode;
            CREATE CLUSTERED INDEX IX_VarRptItemDiv ON #VarRptItemDiv (itemcode);

            SELECT b.itemcode,
                   MAX(CASE WHEN UPPER(LTRIM(RTRIM(b.Itemtype))) = 'W' THEN 'W' ELSE 'S' END) AS Season
              INTO #VarRptItemSeason
              FROM usa.dbo.upcbarcodes b
             WHERE b.itemcode IS NOT NULL AND b.itemcode <> ''
             GROUP BY b.itemcode;
            CREATE CLUSTERED INDEX IX_VarRptItemSeason ON #VarRptItemSeason (itemcode);

            ;WITH HOByItem AS (
                -- HO Stock per (ItemCode × Division). LEFT JOIN ItemDiv
                -- so items with no subclass mapping still appear and
                -- bucket as '(no division)' — same pattern as
                -- WH Stock Position so totals reconcile across reports.
                SELECT ls.ItemCode,
                       ISNULL(id.Division, '(no division)') AS Division,
                       SUM(CAST(ISNULL(ls.SOH, 0) AS bigint)) AS HOStock
                  FROM racks.dbo.LPM_LocStock ls
                  LEFT  JOIN #VarRptItemDiv id ON id.itemcode = ls.ItemCode
                  LEFT  JOIN #VarRptItemSeason its ON its.itemcode = ls.ItemCode
                 WHERE ls.storeid IN ({hoSb})
                   AND (@season = 'A' OR ISNULL(its.Season, 'S') = @season)
                   {divFilterFrag}
                   {hoSearch}
                 GROUP BY ls.ItemCode, ISNULL(id.Division, '(no division)')
            ),
            WHByItem AS (
                -- WH Stock per (ItemCode × Division) applying the universal
                -- WH rule: ShopEligible <> 'E' AND PalletCategory NOT IN
                -- ('NON ELIGIBLE', 'ECOM'). Same as WH Stock Position so
                -- the variance number lines up exactly with that report.
                SELECT w.ItemCode,
                       ISNULL(id.Division, '(no division)') AS Division,
                       SUM(CASE WHEN UPPER(ISNULL(w.PalletCategory, '')) NOT IN ('NON ELIGIBLE', 'ECOM')
                                 AND w.ShopEligible <> 'E'
                                THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS WHStock
                  FROM {whSrc} w
                  LEFT JOIN #VarRptItemDiv id ON id.itemcode = w.ItemCode
                 WHERE (@season = 'A'
                        OR (@season = 'W' AND UPPER(ISNULL(w.Season, '')) = 'W')
                        OR (@season = 'S' AND UPPER(ISNULL(w.Season, '')) <> 'W'))
                   {divFilterFrag}
                   {whSearch}
                 GROUP BY w.ItemCode, ISNULL(id.Division, '(no division)')
            )
            -- 1.14.2: cross-DB Itemmaster JOIN moved to a separate C# round-
            -- trip below. Keeping it inline was either (a) blowing up the
            -- query plan and causing very slow runs, or (b) silently
            -- filtering rows when ItemCode types differed across DBs.
            -- Variance-only filter keeps the result set bounded in normal
            -- use; planner can narrow via Division / Itemcode search.
            SELECT COALESCE(h.ItemCode, w.ItemCode)        AS ItemCode,
                   COALESCE(h.Division, w.Division)        AS Division,
                   ISNULL(h.HOStock, 0)                    AS HOStock,
                   ISNULL(w.WHStock, 0)                    AS WHStock,
                   ISNULL(h.HOStock, 0) - ISNULL(w.WHStock, 0) AS Variance
              FROM HOByItem h
              FULL OUTER JOIN WHByItem w
                       ON w.ItemCode = h.ItemCode
                      AND w.Division = h.Division
             WHERE COALESCE(h.ItemCode, w.ItemCode) IS NOT NULL
               -- Per spec: ""only items which has Variance"". Drops items
               -- where HO and WH match exactly (including 0=0 rows).
               AND ISNULL(h.HOStock, 0) <> ISNULL(w.WHStock, 0)
             ORDER BY ABS(ISNULL(h.HOStock, 0) - ISNULL(w.WHStock, 0)) DESC,
                      COALESCE(h.ItemCode, w.ItemCode);";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        foreach (var p in parms) cmd.Parameters.Add(p);
        cmd.Parameters.Add(new SqlParameter("@season", seasonCode));

        // Pass 1: pull the variance rows without descriptions.
        var rows = new List<VarianceRow>();
        var itemCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var rdr = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rdr.ReadAsync(ct))
            {
                var ic = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                if (!string.IsNullOrEmpty(ic)) itemCodes.Add(ic);
                rows.Add(new VarianceRow(
                    ItemCode: ic,
                    ItemName: "",                                       // filled in pass 2
                    Division: rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                    HoStock:  rdr.IsDBNull(2) ? 0L : rdr.GetInt64(2),
                    WhStock:  rdr.IsDBNull(3) ? 0L : rdr.GetInt64(3),
                    Variance: rdr.IsDBNull(4) ? 0L : rdr.GetInt64(4)));
            }
        }

        // Pass 2: fetch descriptions for the distinct itemcodes that came
        // back. Non-fatal if it fails — empty descriptions are a tolerable
        // degradation, the variance numbers are the important part.
        if (itemCodes.Count > 0)
        {
            try
            {
                var descs = await FetchItemDescriptionsAsync(conn, itemCodes, ct);
                for (int i = 0; i < rows.Count; i++)
                {
                    if (descs.TryGetValue(rows[i].ItemCode, out var d))
                        rows[i] = rows[i] with { ItemName = d };
                }
            }
            catch
            {
                // Swallow — descriptions are nice-to-have, not load-blocking.
            }
        }
        return rows;
    }

    /// <summary>
    /// Fetch <c>HODATA.dbo.Itemmaster.description</c> for the given set of
    /// itemcodes. Uses a parameterised IN-clause and treats itemcode as
    /// nvarchar to avoid implicit-type-conversion surprises (which is what
    /// most likely killed the inline JOIN in 1.14.0/1.14.1).
    /// </summary>
    private static async Task<Dictionary<string, string>> FetchItemDescriptionsAsync(
        SqlConnection conn, HashSet<string> itemCodes, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var parms = new List<SqlParameter>();
        int i = 0;
        foreach (var ic in itemCodes)
        {
            if (i > 0) sb.Append(", ");
            var name = $"@ic{i}";
            sb.Append(name);
            parms.Add(new SqlParameter(name, ic));
            i++;
        }
        var sql = $@"
            SELECT CAST(Itemcode AS nvarchar(64)) AS Itemcode,
                   ISNULL(description, '')        AS Description
              FROM HODATA.dbo.Itemmaster
             WHERE CAST(Itemcode AS nvarchar(64)) IN ({sb});";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        foreach (var p in parms) cmd.Parameters.Add(p);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            if (rdr.IsDBNull(0)) continue;
            var key = rdr.GetString(0);
            var val = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
            dict[key] = val;
        }
        return dict;
    }

    /// <summary>
    /// Resolve the list of HO storeids for the given country — duplicated
    /// from WhHoStockService for now (would be lifted to a shared helper
    /// later if a 3rd report needs the same lookup).
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
