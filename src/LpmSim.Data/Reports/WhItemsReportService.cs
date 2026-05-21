using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using LpmSim.Data.Warehouse;

namespace LpmSim.Data.Reports;

/// <summary>
/// Box-source filter for the WH SKU Investigation report (renamed from
/// "WH Items" in 1.14.54). 1.14.50 — separates LPM-tagged
/// boxes (whboxitems.LPMDt IS NOT NULL) from Non-LPM boxes (LPMDt IS NULL).
/// </summary>
public enum WhItemsBoxSource
{
    All        = 0,
    LpmOnly    = 1,
    NonLpmOnly = 2,
}

/// <summary>
/// Page filters for the WH SKU Investigation report (Reports → WH SKU Investigation).
/// </summary>
/// <param name="Country">SIMCountry — drives the whboxitems source table (UAE
///   → racks.dbo.whboxitems; other countries → [&lt;DataName&gt;].dbo.WHBoxItemsExport)
///   AND the store filter for the Stores SOH column.</param>
/// <param name="PalletCategories">Optional pallet-category filter (multi-select).
///   Null / empty list = no filter (all categories included).</param>
/// <param name="Season">Page-level season filter. <see cref="WhHoSeason.All"/>
///   = no filter; Summer/Winter narrow whboxitems.Season directly. 1.14.50.</param>
/// <param name="BoxSource">LPM vs Non-LPM. <see cref="WhItemsBoxSource.All"/>
///   = no filter; LpmOnly → LPMDt NOT NULL; NonLpmOnly → LPMDt IS NULL. 1.14.50.</param>
/// <param name="LpmMonths">Optional set of specific LPM months to include
///   (matches whboxitems.LPMDt within the given month). Only meaningful when
///   <paramref name="BoxSource"/> is <see cref="WhItemsBoxSource.LpmOnly"/>;
///   ignored for All / NonLpmOnly because LPMDt = NULL for those. Null /
///   empty list = no month restriction. 1.14.50.</param>
/// <param name="Divisions">Optional Division filter (multi-select). Applied
///   to the displayed Division (TOP-1 reduction over
///   <c>upc_subclass × subclassmaster</c>) — so an item appears only if its
///   shown Division is in the set. Null / empty = no division restriction.
///   1.14.52.</param>
public record WhItemsReportFilter(
    string Country,
    IReadOnlyList<string>? PalletCategories,
    WhHoSeason Season,
    WhItemsBoxSource BoxSource,
    IReadOnlyList<DateTime>? LpmMonths,
    IReadOnlyList<string>? Divisions);

/// <summary>
/// One row of the WH SKU Investigation report. One row per ItemCode that has at least
/// one whboxitems entry matching the page filters.
/// </summary>
/// <param name="SkuMax">Total SKU Max — SUM(SKUMax) across all stores with a
///   rule for this item in the latest period.</param>
/// <param name="AvgSkuMax">Average SKU Max per store, across stores that have
///   a rule for this item (denominator = count of (Country, StoreID) rows in
///   LPM_SimItemSkuMax for this item × latest period). Stores without a rule
///   are excluded from the denominator. 1.14.52.</param>
public record WhItemsReportRow(
    string  ItemCode,
    string  ItemName,
    string  Division,
    string  Department,
    string  Brand,
    long    WhQty,
    long    StoresSoh,
    long    SkuMax,
    decimal AvgSkuMax,
    long    ToFillQty,
    // 1.14.82 — Four new columns surfaced from whboxitems / WHBoxItemsExport
    // (HOPrice, Slashed) and from the SKU Max exclusion audit table
    // (LPM_SimItemSkuMaxExcluded) for the selected country, latest period.
    /// <summary>HO price for the item — MAX(HOPrice) across the in-scope whboxitems
    /// rows. Per-pallet value; MAX keeps the result deterministic when pallets
    /// of the same item have differing prices (rare but possible).</summary>
    decimal HoPrice,
    /// <summary>Sum of whboxitems.Slashed across the in-scope rows for the item.</summary>
    long    SlashedQty,
    /// <summary>SUM of PriorSKUMax from LPM_SimItemSkuMaxExcluded for the
    /// selected country, latest (Year1, Month1). Deduped to one row per
    /// (Store, Item) before summing so items matching multiple exclusion
    /// rules aren't double-counted.</summary>
    long    BlockedQty,
    /// <summary>COUNT(DISTINCT StoreID) from LPM_SimItemSkuMaxExcluded for the
    /// selected country, latest period.</summary>
    int     BlockedStores);

/// <summary>
/// Data access for Reports → WH SKU Investigation (renamed from "WH Items" in
/// 1.14.54). Lists every (warehouse-resident) item
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
        // 1.14.48 — Use the "Warehouse" connection-string name to match
        // WhHoStockService / VarianceReportService. The original 1.14.47
        // service mistakenly looked for "Default" which doesn't exist in
        // the Azure App Service config → constructor threw on first
        // request to /lpm/reports/wh-items.
        _connStr = cfg.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Connection string 'Warehouse' is missing.");
    }

    /// <summary>
    /// Run the WH SKU Investigation report for the given filters. Returns one row per
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

        // 1.14.50 — Season filter. Encoded as a single @season param so the
        // SQL stays parameterised. 'A' = All; 'S' = Summer; 'W' = Winter.
        var seasonCode = filter.Season switch
        {
            WhHoSeason.Summer => "S",
            WhHoSeason.Winter => "W",
            _                 => "A",
        };

        // 1.14.50 — Box source filter (LPM vs Non-LPM). Encoded as a single
        // @boxSource param. 'A' = All; 'L' = LpmOnly (LPMDt NOT NULL);
        // 'N' = NonLpmOnly (LPMDt IS NULL).
        var boxSourceCode = filter.BoxSource switch
        {
            WhItemsBoxSource.LpmOnly    => "L",
            WhItemsBoxSource.NonLpmOnly => "N",
            _                           => "A",
        };

        // 1.14.50 — LPM Months filter. Only meaningful when BoxSource = LpmOnly
        // (Non-LPM has LPMDt = NULL so months don't apply). Builds an OR-chain
        // of (LPMDt >= month_start AND LPMDt < next_month_start) ranges.
        // Empty/null = no month restriction.
        var lpmMonthsFrag = "";
        var lpmMonthsParms = new List<SqlParameter>();
        if (filter.BoxSource == WhItemsBoxSource.LpmOnly
            && filter.LpmMonths is { Count: > 0 })
        {
            var sb = new StringBuilder(" AND (");
            for (int i = 0; i < filter.LpmMonths.Count; i++)
            {
                if (i > 0) sb.Append(" OR ");
                var ms = new DateTime(filter.LpmMonths[i].Year, filter.LpmMonths[i].Month, 1);
                var me = ms.AddMonths(1);
                sb.Append("(w.LPMDt >= @lm").Append(i).Append("_s AND w.LPMDt < @lm").Append(i).Append("_e)");
                lpmMonthsParms.Add(new SqlParameter($"@lm{i}_s", ms));
                lpmMonthsParms.Add(new SqlParameter($"@lm{i}_e", me));
            }
            sb.Append(')');
            lpmMonthsFrag = sb.ToString();
        }

        // 1.14.52 — Division filter (multi-select). Empty = no filter. Applied
        // at the final SELECT against the displayed Division (TOP-1 from
        // #WhItemsDiv) so what the user picks matches what they see.
        var divFrag = "";
        var divParms = new List<SqlParameter>();
        if (filter.Divisions is { Count: > 0 })
        {
            var sb = new StringBuilder(" WHERE ISNULL(wdiv.Division, '') IN (");
            for (int i = 0; i < filter.Divisions.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var name = $"@div{i}";
                sb.Append(name);
                divParms.Add(new SqlParameter(name, filter.Divisions[i]));
            }
            sb.Append(')');
            divFrag = sb.ToString();
        }

        var sql = $@"
            SET NOCOUNT ON;

            -- Drop any prior temp-table residue from this session.
            IF OBJECT_ID('tempdb..#WhItemsAgg')     IS NOT NULL DROP TABLE #WhItemsAgg;
            IF OBJECT_ID('tempdb..#WhItemsCodes')   IS NOT NULL DROP TABLE #WhItemsCodes;
            IF OBJECT_ID('tempdb..#WhItemsSoh')     IS NOT NULL DROP TABLE #WhItemsSoh;
            IF OBJECT_ID('tempdb..#WhItemsSkuMax')  IS NOT NULL DROP TABLE #WhItemsSkuMax;
            IF OBJECT_ID('tempdb..#WhItemsDesc')    IS NOT NULL DROP TABLE #WhItemsDesc;
            IF OBJECT_ID('tempdb..#WhItemsDiv')     IS NOT NULL DROP TABLE #WhItemsDiv;
            IF OBJECT_ID('tempdb..#WhItemsBrand')   IS NOT NULL DROP TABLE #WhItemsBrand;
            IF OBJECT_ID('tempdb..#WhItemsBlocked') IS NOT NULL DROP TABLE #WhItemsBlocked;  -- 1.14.82

            -- 1) WH Qty per itemcode. This defines the row universe — any
            --    itemcode in the country's warehouse, filtered by pallet
            --    category + season + LPM/Non-LPM + LPM months.
            --    Excludes Non-Purchased rows (ShopEligible = 'E' marks boxes
            --    still in-process / not yet purchased) so the WH Qty totals
            --    reconcile with WH Stock Position. 1.14.52 — also excludes
            --    PalletCategory = 'NON-PURCHASED' to drop the rows the
            --    planner flagged on the screenshot.
            -- 1.14.82: HoPrice (MAX) + SlashedQty (SUM) projected alongside WhQty.
            -- Both come from the same {whSrc} grain so they ride the same scan +
            -- WHERE filter — no extra cost.
            --
            -- 1.14.85 hotfix: HOPrice and Slashed are stored as varchar in
            -- whboxitems / WHBoxItemsExport (not numeric), so:
            --   * The original SUM(CAST(ISNULL(w.Slashed, 0) AS bigint))
            --     fails with ''Error converting data type varchar to bigint''
            --     whenever a row has a non-numeric Slashed value (blank, ''N'',
            --     etc.) -- SQL Server resolves ISNULL(varchar, 0) by
            --     converting the varchar to int first (int has higher type
            --     precedence) and fails on the first bad value.
            --   * MAX(w.HOPrice) on a varchar returns a lexicographic max,
            --     which is wrong for prices (''9'' > ''100'').
            -- TRY_CAST returns NULL on conversion failure instead of erroring;
            -- ISNULL(...,0) collapses the NULLs back to 0 for the SUM.
            SELECT w.itemcode,
                   WhQty      = SUM(CAST(ISNULL(w.Qty, 0) AS bigint)),
                   HoPrice    = MAX(TRY_CAST(w.HOPrice AS decimal(18, 2))),
                   SlashedQty = SUM(ISNULL(TRY_CAST(w.Slashed AS bigint), 0))
              INTO #WhItemsAgg
              FROM {whSrc} w
             WHERE w.itemcode IS NOT NULL AND w.itemcode <> ''
               AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
               AND (w.PalletCategory IS NULL OR UPPER(w.PalletCategory) <> 'NON-PURCHASED')
               -- 1.14.50: Season filter (A=All / S / W)
               AND (@season = 'A' OR UPPER(ISNULL(w.Season, '')) = @season)
               -- 1.14.50: Box source filter (A=All / L=LpmOnly / N=NonLpmOnly)
               AND (@boxSource = 'A'
                    OR (@boxSource = 'L' AND w.LPMDt IS NOT NULL)
                    OR (@boxSource = 'N' AND w.LPMDt IS NULL))
               {palletFrag}
               {lpmMonthsFrag}
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
            --    1.14.50 — Negative SOH (oversold rows in LocStock) clamped
            --    to zero per the planner spec: if SOH is negative, treat
            --    it as zero. Same clamp pattern the allocator uses in
            --    cap math (1.14.31).
            SELECT ls.Itemcode,
                   StoresSoh = SUM(CAST(
                       CASE WHEN ISNULL(ls.SOH, 0) < 0 THEN 0
                            ELSE ls.SOH END AS bigint))
              INTO #WhItemsSoh
              FROM racks.dbo.LPM_LocStock ls
              INNER JOIN bfldata.dbo.DataSettings ds ON ds.StoreID = ls.StoreID
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
              FROM LPMSIM.dbo.LPM_SimItemSkuMax
             WHERE Country = @country
             ORDER BY Year1 DESC, Month1 DESC;

            -- 1.14.52: AvgSkuMax = per-store mean across the stores that have
            -- a rule for this item in the latest period. Each row in
            -- LPM_SimItemSkuMax is one (Country, StoreID, ItemCode, Year, Month)
            -- tuple, so AVG over (ItemCode, latest period) computes
            -- SUM(SKUMax) / COUNT(rows with rule) directly — stores without
            -- a rule for this item are excluded from the denominator, which
            -- matches what the planner asked for.
            SELECT sm.ItemCode,
                   SkuMax    = SUM(CAST(ISNULL(sm.SKUMax,    0) AS bigint)),
                   AvgSkuMax = AVG(CAST(ISNULL(sm.SKUMax,    0) AS decimal(18,2))),
                   ToFillQty = SUM(CAST(ISNULL(sm.ToFillQty, 0) AS bigint))
              INTO #WhItemsSkuMax
              FROM LPMSIM.dbo.LPM_SimItemSkuMax sm
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

            -- 7b) 1.14.82 — Per-itemcode rollup of the SKU Max exclusion audit
            --     table (LPM_SimItemSkuMaxExcluded) for the selected country
            --     at the latest (Year1, Month1) — matches the period rule used
            --     by the existing SkuMax / ToFillQty columns above so all three
            --     reflect the same monthly snapshot.
            --
            --     The audit table can hold MULTIPLE rows per (Store, Item) when
            --     more than one exclusion rule fires for the same combination
            --     (e.g. both ExcludeExport_Planning and RemoveItemsFromTransfer
            --     match). Without deduping, SUM(PriorSKUMax) would double-count.
            --     Dedupe to one row per (Store, Item) first (PriorSKUMax is
            --     identical across rules for the same Store/Item — it's the
            --     original SKUMax value before zeroing — so MAX is correct).
            IF OBJECT_ID('tempdb..#WhItemsBlocked') IS NOT NULL DROP TABLE #WhItemsBlocked;

            DECLARE @y_excl int, @m_excl int;
            SELECT TOP 1 @y_excl = Year1, @m_excl = Month1
              FROM LPMSIM.dbo.LPM_SimItemSkuMaxExcluded
             WHERE Country = @country
             ORDER BY Year1 DESC, Month1 DESC;

            ;WITH ExclDedupe AS (
                SELECT e.ItemCode, e.StoreID,
                       PriorSKUMax = MAX(e.PriorSKUMax)
                  FROM LPMSIM.dbo.LPM_SimItemSkuMaxExcluded e
                  INNER JOIN #WhItemsCodes wc ON wc.itemcode = e.ItemCode
                 WHERE e.Country = @country
                   AND e.Year1   = @y_excl
                   AND e.Month1  = @m_excl
                 GROUP BY e.ItemCode, e.StoreID
            )
            SELECT ItemCode,
                   BlockedQty    = SUM(CAST(ISNULL(PriorSKUMax, 0) AS bigint)),
                   BlockedStores = COUNT(DISTINCT StoreID)
              INTO #WhItemsBlocked
              FROM ExclDedupe
             GROUP BY ItemCode;
            CREATE CLUSTERED INDEX IX_WhItemsBlocked ON #WhItemsBlocked (ItemCode);

            -- 8) Final result. LEFT JOIN every enrichment so items missing
            --    from the lookup tables still produce a row (with empty
            --    metadata / zero quantity for the missing dimension).
            --
            --    1.14.50 — ToFillQty is capped at WhQty. The raw column on
            --    LPM_SimItemSkuMax sums fill-capacity per store across
            --    stores, which can exceed the actual warehouse stock
            --    available to ship (e.g. SkuMax demand totals 18 but only
            --    16 units exist in the warehouse). The displayed value is
            --    the effective fillable quantity = MIN(demand, supply).
            SELECT  ItemCode  = wa.itemcode,
                    ItemName  = ISNULL(wd.ItemName,   ''),
                    Division  = ISNULL(wdiv.Division,    ''),
                    Department= ISNULL(wdiv.Department,  ''),
                    Brand     = ISNULL(wb.Brand,      ''),
                    WhQty     = wa.WhQty,
                    StoresSoh = ISNULL(ws.StoresSoh,  0),
                    SkuMax    = ISNULL(wsm.SkuMax,    0),
                    AvgSkuMax = ISNULL(wsm.AvgSkuMax, CAST(0 AS decimal(18,2))),
                    ToFillQty = CASE
                                  WHEN ISNULL(wsm.ToFillQty, 0) > wa.WhQty
                                       THEN wa.WhQty
                                  ELSE ISNULL(wsm.ToFillQty, 0)
                                END,
                    -- 1.14.82 — HoPrice / SlashedQty come from #WhItemsAgg
                    -- (computed alongside WhQty against {whSrc}); BlockedQty +
                    -- BlockedStores come from the exclusion audit table rollup.
                    HoPrice       = ISNULL(wa.HoPrice,       CAST(0 AS decimal(18,2))),
                    SlashedQty    = ISNULL(wa.SlashedQty,    0),
                    BlockedQty    = ISNULL(wblk.BlockedQty,  0),
                    BlockedStores = ISNULL(wblk.BlockedStores, 0)
              FROM  #WhItemsAgg   wa
              LEFT  JOIN #WhItemsDesc   wd   ON wd.Itemcode  = wa.itemcode
              LEFT  JOIN #WhItemsDiv    wdiv ON wdiv.itemcode= wa.itemcode
              LEFT  JOIN #WhItemsBrand  wb   ON wb.itemcode  = wa.itemcode
              LEFT  JOIN #WhItemsSoh    ws   ON ws.Itemcode  = wa.itemcode
              LEFT  JOIN #WhItemsSkuMax wsm  ON wsm.ItemCode = wa.itemcode
              LEFT  JOIN #WhItemsBlocked wblk ON wblk.ItemCode = wa.itemcode    -- 1.14.82
              {divFrag}
              ORDER BY Division, ItemCode;
        ";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.Parameters.Add(new SqlParameter("@country", filter.Country));
        cmd.Parameters.Add(new SqlParameter("@season", seasonCode));
        cmd.Parameters.Add(new SqlParameter("@boxSource", boxSourceCode));
        foreach (var p in palletParms)    cmd.Parameters.Add(p);
        foreach (var p in lpmMonthsParms) cmd.Parameters.Add(p);
        foreach (var p in divParms)       cmd.Parameters.Add(p);

        var rows = new List<WhItemsReportRow>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new WhItemsReportRow(
                ItemCode:      rdr.IsDBNull(0)  ? "" : rdr.GetString(0),
                ItemName:      rdr.IsDBNull(1)  ? "" : rdr.GetString(1),
                Division:      rdr.IsDBNull(2)  ? "" : rdr.GetString(2),
                Department:    rdr.IsDBNull(3)  ? "" : rdr.GetString(3),
                Brand:         rdr.IsDBNull(4)  ? "" : rdr.GetString(4),
                WhQty:         rdr.IsDBNull(5)  ? 0L : rdr.GetInt64(5),
                StoresSoh:     rdr.IsDBNull(6)  ? 0L : rdr.GetInt64(6),
                SkuMax:        rdr.IsDBNull(7)  ? 0L : rdr.GetInt64(7),
                AvgSkuMax:     rdr.IsDBNull(8)  ? 0m : rdr.GetDecimal(8),
                ToFillQty:     rdr.IsDBNull(9)  ? 0L : rdr.GetInt64(9),
                // 1.14.82 — HoPrice (10), SlashedQty (11), BlockedQty (12), BlockedStores (13).
                HoPrice:       rdr.IsDBNull(10) ? 0m : Convert.ToDecimal(rdr.GetValue(10)),
                SlashedQty:    rdr.IsDBNull(11) ? 0L : rdr.GetInt64(11),
                BlockedQty:    rdr.IsDBNull(12) ? 0L : rdr.GetInt64(12),
                BlockedStores: rdr.IsDBNull(13) ? 0  : rdr.GetInt32(13)));
        }
        return rows;
    }
}
