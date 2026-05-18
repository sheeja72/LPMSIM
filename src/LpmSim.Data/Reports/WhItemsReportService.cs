using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using LpmSim.Data.Warehouse;

namespace LpmSim.Data.Reports;

/// <summary>
/// Page filters for the WH Items report (Reports → WH Items).
/// </summary>
/// <param name="Country">SIMCountry — drives the whboxitems source table (UAE
///   → racks.dbo.whboxitems; other countries → [&lt;DataName&gt;].dbo.WHBoxItemsExport)
///   AND the store filter for the Stores SOH column.</param>
/// <param name="PalletCategories">Optional pallet-category filter (multi-select).
///   Null / empty list = no filter (all categories included).</param>
public record WhItemsReportFilter(
    string Country,
    IReadOnlyList<string>? PalletCategories);

/// <summary>
/// One row of the WH Items report. One row per ItemCode that has at least
/// one whboxitems entry matching the page filters.
/// </summary>
public record WhItemsReportRow(
    string ItemCode,
    string ItemName,
    string Division,
    string Department,
    string Brand,
    long   WhQty,
    long   StoresSoh,
    long   SkuMax,
    long   ToFillQty);

/// <summary>
/// Data access for Reports → WH Items. Lists every (warehouse-resident) item
/// with its descriptive metadata and the four quantity columns that drive a
/// planner's "do we have this and can we ship it" question.
///
/// Universe: itemcodes with at least one row in the country's whboxitems
/// source. Stores SOH / SKU Max / To Fill Qty are LEFT-joined per item, so
/// items present only in WH (no store SkuMax row, no LocStock row) still
/// show up with zeros in the aggregate columns.
///
/// All the descriptive lookups (item description, division, department,
/// brand) follow the same shape used by other reports / migrations 048
/// (DivisionName / Brand / GroupCode on LPM_SimItemSkuMaxExcluded): TOP-1
/// reductions via ROW_NUMBER OVER (PARTITION BY itemcode ORDER BY …) so
/// items with multiple matching rows produce exactly one displayed value.
/// </summary>
public sealed class WhItemsReportService
{
    private readonly string _connStr;

    public WhItemsReportService(IConfiguration cfg)
    {
        _connStr = cfg.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is missing.");
    }

    /// <summary>
    /// Run the WH Items report for the given filters. Returns one row per
    /// itemcode present in the country's whboxitems source.
    /// </summary>
    public async Task<List<WhItemsReportRow>> GetAsync(WhItemsReportFilter filter, CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Resolve the country-aware whboxitems source (same helper used by
        // the other warehouse-side reports). UAE → racks.dbo.whboxitems;
        // others → [<DataName>].dbo.WHBoxItemsExport.
        var whSrc = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);

        // Optional pallet-category filter (multi-select). Empty = no filter.
        var palletFrag = "";
        var palletParms = new List<SqlParameter>();
        if (filter.PalletCategories is { Count: > 0 })
        {
            var sb = new StringBuilder(" AND w.PalletCategory IN (");
            for (int i = 0; i < filter.PalletCategories.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var name = $"@pc{i}";
                sb.Append(name);
                palletParms.Add(new SqlParameter(name, filter.PalletCategories[i]));
            }
            sb.Append(')');
            palletFrag = sb.ToString();
        }

        var sql = $@"
            SET NOCOUNT ON;

            -- Drop any prior temp-table residue from this session.
            IF OBJECT_ID('tempdb..#WhItemsAgg')   IS NOT NULL DROP TABLE #WhItemsAgg;
            IF OBJECT_ID('tempdb..#WhItemsCodes') IS NOT NULL DROP TABLE #WhItemsCodes;
            IF OBJECT_ID('tempdb..#WhItemsSoh')   IS NOT NULL DROP TABLE #WhItemsSoh;
            IF OBJECT_ID('tempdb..#WhItemsSkuMax')IS NOT NULL DROP TABLE #WhItemsSkuMax;
            IF OBJECT_ID('tempdb..#WhItemsDesc')  IS NOT NULL DROP TABLE #WhItemsDesc;
            IF OBJECT_ID('tempdb..#WhItemsDiv')   IS NOT NULL DROP TABLE #WhItemsDiv;
            IF OBJECT_ID('tempdb..#WhItemsBrand') IS NOT NULL DROP TABLE #WhItemsBrand;

            -- 1) WH Qty per itemcode. This defines the row universe — any
            --    itemcode in the country's warehouse, filtered by pallet
            --    category. Excludes purchased boxes (ShopEligible = 'E')
            --    so the totals match the other reports.
            SELECT w.itemcode,
                   WhQty = SUM(CAST(ISNULL(w.Qty, 0) AS bigint))
              INTO #WhItemsAgg
              FROM {whSrc} w
             WHERE w.itemcode IS NOT NULL AND w.itemcode <> ''
               AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
               {palletFrag}
             GROUP BY w.itemcode;
            CREATE CLUSTERED INDEX IX_WhItemsAgg ON #WhItemsAgg (itemcode);

            -- 2) Just the itemcodes — used by every downstream lookup so the
            --    JOIN cost stays bounded by the number of items in scope.
            SELECT itemcode INTO #WhItemsCodes FROM #WhItemsAgg;
            CREATE CLUSTERED INDEX IX_WhItemsCodes ON #WhItemsCodes (itemcode);

            -- 3) Stores SOH per itemcode. Sum LPM_LocStock.SOH across every
            --    active store for the country (DataSettings.SIMCountry +
            --    ActiveStore = 'Y'). HO storeids (no SIMCountry) are
            --    naturally excluded since they have no SIMCountry row.
            SELECT ls.Itemcode,
                   StoresSoh = SUM(CAST(ISNULL(ls.SOH, 0) AS bigint))
              INTO #WhItemsSoh
              FROM racks.dbo.LPM_LocStock ls
              INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
              INNER JOIN #WhItemsCodes wc    ON wc.itemcode = ls.Itemcode
             WHERE ds.SIMCountry = @country
               AND ds.ActiveStore = 'Y'
               AND ls.StoreID IS NOT NULL AND ls.StoreID <> ''
             GROUP BY ls.Itemcode;
            CREATE CLUSTERED INDEX IX_WhItemsSoh ON #WhItemsSoh (Itemcode);

            -- 4) SKU Max + ToFillQty per itemcode for the LATEST period in
            --    LPM_SimItemSkuMax for this country. Latest = MAX (Year1,
            --    Month1). If no rows for the country, both totals are 0.
            DECLARE @y int, @m int;
            SELECT TOP 1 @y = Year1, @m = Month1
              FROM dbo.LPM_SimItemSkuMax
             WHERE Country = @country
             ORDER BY Year1 DESC, Month1 DESC;

            SELECT sm.ItemCode,
                   SkuMax    = SUM(CAST(ISNULL(sm.SKUMax,    0) AS bigint)),
                   ToFillQty = SUM(CAST(ISNULL(sm.ToFillQty, 0) AS bigint))
              INTO #WhItemsSkuMax
              FROM dbo.LPM_SimItemSkuMax sm
              INNER JOIN #WhItemsCodes wc ON wc.itemcode = sm.ItemCode
             WHERE sm.Country = @country
               AND sm.Year1   = @y
               AND sm.Month1  = @m
             GROUP BY sm.ItemCode;
            CREATE CLUSTERED INDEX IX_WhItemsSkuMax ON #WhItemsSkuMax (ItemCode);

            -- 5) Item description from HODATA.dbo.Itemmaster.description.
            --    Cast Itemcode to nvarchar(64) to match the column type used
            --    by VarianceReportService (consistency with the existing
            --    code path that exercises this lookup).
            SELECT  CAST(im.Itemcode AS nvarchar(64)) AS Itemcode,
                    ItemName = ISNULL(im.description, '')
              INTO  #WhItemsDesc
              FROM  HODATA.dbo.Itemmaster im
              INNER JOIN #WhItemsCodes wc ON wc.itemcode = CAST(im.Itemcode AS nvarchar(64));
            CREATE CLUSTERED INDEX IX_WhItemsDesc ON #WhItemsDesc (Itemcode);

            -- 6) Division + Department per itemcode via upc_subclass ×
            --    subclassmaster. TOP-1 reduction via ROW_NUMBER so an item
            --    with multiple subclass rows produces exactly one displayed
            --    (Division, Department) pair.
            WITH ItemDeptRanked AS (
                SELECT u.itemcode,
                       Division   = ISNULL(sm.Division,   ''),
                       Department = ISNULL(sm.Department, ''),
                       rn = ROW_NUMBER() OVER (PARTITION BY u.itemcode
                                                   ORDER BY sm.Division, sm.Department)
                  FROM Datareporting.dbo.upc_subclass    u
                  INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                  INNER JOIN #WhItemsCodes wc            ON wc.itemcode = u.itemcode
            )
            SELECT itemcode, Division, Department
              INTO #WhItemsDiv
              FROM ItemDeptRanked
             WHERE rn = 1;
            CREATE CLUSTERED INDEX IX_WhItemsDiv ON #WhItemsDiv (itemcode);

            -- 7) Brand per itemcode from usa.dbo.upcbarcodes.Vendor. Same
            --    TOP-1 reduction (multiple barcode rows can exist per item).
            WITH ItemBrandRanked AS (
                SELECT b.itemcode,
                       Brand = ISNULL(b.Vendor, ''),
                       rn = ROW_NUMBER() OVER (PARTITION BY b.itemcode ORDER BY b.Vendor)
                  FROM usa.dbo.upcbarcodes b
                  INNER JOIN #WhItemsCodes wc ON wc.itemcode = b.itemcode
                 WHERE b.Vendor IS NOT NULL AND LTRIM(RTRIM(b.Vendor)) <> ''
            )
            SELECT itemcode, Brand
              INTO #WhItemsBrand
              FROM ItemBrandRanked
             WHERE rn = 1;
            CREATE CLUSTERED INDEX IX_WhItemsBrand ON #WhItemsBrand (itemcode);

            -- 8) Final result. LEFT JOIN every enrichment so items missing
            --    from the lookup tables still produce a row (with empty
            --    metadata / zero quantity for the missing dimension).
            SELECT  ItemCode  = wa.itemcode,
                    ItemName  = ISNULL(wd.ItemName,   ''),
                    Division  = ISNULL(wdiv.Division,    ''),
                    Department= ISNULL(wdiv.Department,  ''),
                    Brand     = ISNULL(wb.Brand,      ''),
                    WhQty     = wa.WhQty,
                    StoresSoh = ISNULL(ws.StoresSoh,  0),
                    SkuMax    = ISNULL(wsm.SkuMax,    0),
                    ToFillQty = ISNULL(wsm.ToFillQty, 0)
              FROM  #WhItemsAgg   wa
              LEFT  JOIN #WhItemsDesc   wd   ON wd.Itemcode  = wa.itemcode
              LEFT  JOIN #WhItemsDiv    wdiv ON wdiv.itemcode= wa.itemcode
              LEFT  JOIN #WhItemsBrand  wb   ON wb.itemcode  = wa.itemcode
              LEFT  JOIN #WhItemsSoh    ws   ON ws.Itemcode  = wa.itemcode
              LEFT  JOIN #WhItemsSkuMax wsm  ON wsm.ItemCode = wa.itemcode
              ORDER BY Division, ItemCode;
        ";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.Parameters.Add(new SqlParameter("@country", filter.Country));
        foreach (var p in palletParms) cmd.Parameters.Add(p);

        var rows = new List<WhItemsReportRow>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new WhItemsReportRow(
                ItemCode:   rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                ItemName:   rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                Division:   rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                Department: rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                Brand:      rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                WhQty:      rdr.IsDBNull(5) ? 0L : rdr.GetInt64(5),
                StoresSoh:  rdr.IsDBNull(6) ? 0L : rdr.GetInt64(6),
                SkuMax:     rdr.IsDBNull(7) ? 0L : rdr.GetInt64(7),
                ToFillQty:  rdr.IsDBNull(8) ? 0L : rdr.GetInt64(8)));
        }
        return rows;
    }
}
