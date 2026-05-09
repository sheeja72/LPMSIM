using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace LpmSim.Data.Warehouse;

public enum LpmPresence { Any = 0, HasLpm = 1, NoLpm = 2 }

/// <summary>
/// How the Warehouse Box Details report aggregates rows.
/// <list type="bullet">
///   <item><c>Box</c>        — one row per box (the existing detail view + Brand/Rack/Purchased columns).</item>
///   <item><c>Division</c>   — qty rolled up to division: LPM Current (elapsed + this month), LPM Future, Non-LPM.</item>
///   <item><c>Department</c> — same metrics, broken down by Division × Department.</item>
///   <item><c>Brand</c>      — broken down by Division × Department × Brand.</item>
/// </list>
/// </summary>
public enum WhGroupBy { Box = 0, Division = 1, Department = 2, Brand = 3 }

public record WhBoxFilter(
    string? Warehouse,
    string? TypeName,
    string? PalletCategory,
    string? Lpm,                                       // LPM month tag (whboxitems.LPM) — separate from Brand
    string? Search,
    LpmPresence LpmStatus = LpmPresence.Any,
    string? Division = null,
    string? Department = null,
    string? Brand = null,                              // whboxitems.Brand (the real brand)
    bool NonPurchasedOnly = true);                     // default ON — hide ShopEligible='E' (already-shopped) boxes

public record WhBoxRow(
    string Country,
    string Warehouse,
    string PalletNo,
    string BoxNo,
    string PalletType,
    string? TypeName,
    string? PalletCategory,
    long Qty,
    string? LPM,
    string? Division,
    string? Department,
    string? Brand,                                     // alias for LPM under business naming
    string? Rack,
    string? Purchased);                                // "N" when ShopEligible='E', else NULL

/// <summary>Division-level summary row: one per Division.</summary>
public record WhDivisionRow(
    string? Division,
    long LPMCurrentQty,                                // LPMDt is set AND in the past or this month
    long LPMFutureQty,                                 // LPMDt is set AND >= 1st of next month
    long NonLPMQty);                                   // LPMDt is NULL

/// <summary>Department-level summary: per Division × Department.</summary>
public record WhDepartmentRow(
    string? Division,
    string? Department,
    long LPMCurrentQty,
    long LPMFutureQty,
    long NonLPMQty);

/// <summary>Brand-level summary: per Division × Department × Brand.</summary>
public record WhBrandRow(
    string? Division,
    string? Department,
    string? Brand,
    long LPMCurrentQty,
    long LPMFutureQty,
    long NonLPMQty);

public class WarehouseQueryService(IConfiguration cfg)
{
    private readonly string _connStr =
        cfg.GetConnectionString("Warehouse")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:Warehouse (set via User Secrets).");

    public async Task<List<string>> GetWarehousesAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT Warehouse
              FROM racks.dbo.whboxitems
             WHERE Warehouse IS NOT NULL AND Warehouse <> ''
             ORDER BY Warehouse;";
        return await ReadStringsAsync(sql, ct);
    }

    public async Task<List<string>> GetLpmsAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT LPM
              FROM racks.dbo.whboxitems
             WHERE LPM IS NOT NULL AND LPM <> ''
             ORDER BY LPM;";
        return await ReadStringsAsync(sql, ct);
    }

    public async Task<List<string>> GetTypeNamesAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT TypeName
              FROM bfldata.dbo.pallettype
             WHERE TypeName IS NOT NULL AND TypeName <> ''
             ORDER BY TypeName;";
        return await ReadStringsAsync(sql, ct);
    }

    public async Task<List<string>> GetPalletCategoriesAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT PalletCategory
              FROM bfldata.dbo.pallettype
             WHERE PalletCategory IS NOT NULL AND PalletCategory <> ''
             ORDER BY PalletCategory;";
        return await ReadStringsAsync(sql, ct);
    }

    /// <summary>Distinct Division values from subclassmaster — for the Division filter dropdown.</summary>
    public async Task<List<string>> GetDistinctDivisionsAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT Division
              FROM Datareporting.dbo.subclassmaster
             WHERE Division IS NOT NULL AND Division <> ''
             ORDER BY Division;";
        return await ReadStringsAsync(sql, ct);
    }

    /// <summary>Distinct Department values from subclassmaster — for the Department filter dropdown.</summary>
    public async Task<List<string>> GetDistinctDepartmentsAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT Department
              FROM Datareporting.dbo.subclassmaster
             WHERE Department IS NOT NULL AND Department <> ''
             ORDER BY Department;";
        return await ReadStringsAsync(sql, ct);
    }

    /// <summary>Distinct Brand values from whboxitems — for the Brand filter dropdown.</summary>
    public async Task<List<string>> GetDistinctBrandsAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT Brand
              FROM racks.dbo.whboxitems
             WHERE Brand IS NOT NULL AND Brand <> ''
             ORDER BY Brand;";
        return await ReadStringsAsync(sql, ct);
    }

    public async Task<List<WhBoxRow>> GetBoxesAsync(WhBoxFilter filter, int top, CancellationToken ct = default)
    {
        // Per-box detail. OUTER APPLY keeps the per-box row count from
        // multiplying when an item has multiple subclass rows. Columns:
        //   • Division / Department  — from subclassmaster (TOP 1 per box).
        //   • Brand                  — whboxitems.Brand (the real brand column).
        //   • LPM                    — whboxitems.LPM (the LPM month tag — May-26 etc.,
        //                              kept SEPARATE from Brand per business confirmation).
        //   • Rack                   — whboxitems.Rack (warehouse rack location).
        //   • Purchased              — "N" when ShopEligible='E', else NULL/blank.
        //
        // Filters: Division/Department use HAVING (post-aggregation since their
        // value is the box's primary div/dept). Brand filter is on whboxitems.Brand.
        const string sql = @"
            SELECT TOP (@top)
                   'UAE' AS Country,
                   w.Warehouse,
                   w.PalletNo,
                   w.BoxNo,
                   w.PalletType,
                   pt.TypeName,
                   pt.PalletCategory,
                   SUM(CAST(w.Qty AS bigint))                                              AS Qty,
                   MAX(w.LPM)                                                              AS LPM,
                   MAX(scm.Division)                                                       AS Division,
                   MAX(scm.Department)                                                     AS Department,
                   MAX(w.Brand)                                                            AS Brand,
                   MAX(w.Rack)                                                             AS Rack,
                   MAX(CASE WHEN w.ShopEligible = 'E' THEN 'N' ELSE NULL END)              AS Purchased
              FROM racks.dbo.whboxitems w
              LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
              OUTER APPLY (
                  SELECT TOP 1 sm.Division, sm.Department
                    FROM Datareporting.dbo.upc_subclass    u
                    INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                   WHERE u.itemcode = w.ItemCode
                   ORDER BY sm.Division
              ) scm
             WHERE (@warehouse IS NULL OR w.Warehouse = @warehouse)
               AND (@typeName IS NULL OR pt.TypeName = @typeName)
               AND (@palletCategory IS NULL OR pt.PalletCategory = @palletCategory)
               AND (@lpm IS NULL OR w.LPM = @lpm)
               AND (@brand IS NULL OR w.Brand = @brand)
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL
                    OR w.PalletNo LIKE @searchLike
                    OR w.BoxNo LIKE @searchLike)
               AND (@nonPurchasedOnly = 0
                    OR w.ShopEligible IS NULL
                    OR w.ShopEligible <> 'E')
             GROUP BY w.Warehouse, w.PalletNo, w.BoxNo, w.PalletType, pt.TypeName, pt.PalletCategory
            HAVING (@division   IS NULL OR MAX(scm.Division)   = @division)
               AND (@department IS NULL OR MAX(scm.Department) = @department)
             ORDER BY w.Warehouse, w.PalletNo, w.BoxNo;";

        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 180 };
        AddFilterParams(cmd, filter);
        cmd.Parameters.Add(new SqlParameter("@top", top));

        var rows = new List<WhBoxRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WhBoxRow(
                Country:        reader.GetString(0),
                Warehouse:      reader.IsDBNull(1)  ? "" : reader.GetString(1),
                PalletNo:       reader.IsDBNull(2)  ? "" : reader.GetString(2),
                BoxNo:          reader.IsDBNull(3)  ? "" : reader.GetString(3),
                PalletType:     reader.IsDBNull(4)  ? "" : reader.GetString(4),
                TypeName:       reader.IsDBNull(5)  ? null : reader.GetString(5),
                PalletCategory: reader.IsDBNull(6)  ? null : reader.GetString(6),
                Qty:            reader.IsDBNull(7)  ? 0 : reader.GetInt64(7),
                LPM:            reader.IsDBNull(8)  ? null : reader.GetString(8),
                Division:       reader.IsDBNull(9)  ? null : reader.GetString(9),
                Department:     reader.IsDBNull(10) ? null : reader.GetString(10),
                Brand:          reader.IsDBNull(11) ? null : reader.GetString(11),
                Rack:           reader.IsDBNull(12) ? null : reader.GetString(12),
                Purchased:      reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        return rows;
    }

    public async Task<List<WhDivisionRow>> GetDivisionSummaryAsync(WhBoxFilter filter, CancellationToken ct = default)
    {
        // Item-level join (no GROUP BY box) — qty attributes to whichever division
        // each item actually belongs to. A box with items in 2 divisions
        // contributes proportionally to both, which is the right behaviour
        // for category-level reporting.
        var sql = $@"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1));
            {SummarySelect(level: "div")}
             GROUP BY sm.Division
             ORDER BY sm.Division;";

        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 240 };
        AddFilterParams(cmd, filter);

        var rows = new List<WhDivisionRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WhDivisionRow(
                Division:      reader.IsDBNull(0) ? null : reader.GetString(0),
                LPMCurrentQty: reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                LPMFutureQty:  reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                NonLPMQty:     reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
        }
        return rows;
    }

    public async Task<List<WhDepartmentRow>> GetDepartmentSummaryAsync(WhBoxFilter filter, CancellationToken ct = default)
    {
        var sql = $@"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1));
            {SummarySelect(level: "dept")}
             GROUP BY sm.Division, sm.Department
             ORDER BY sm.Division, sm.Department;";

        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 240 };
        AddFilterParams(cmd, filter);

        var rows = new List<WhDepartmentRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WhDepartmentRow(
                Division:      reader.IsDBNull(0) ? null : reader.GetString(0),
                Department:    reader.IsDBNull(1) ? null : reader.GetString(1),
                LPMCurrentQty: reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                LPMFutureQty:  reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                NonLPMQty:     reader.IsDBNull(4) ? 0 : reader.GetInt64(4)));
        }
        return rows;
    }

    public async Task<List<WhBrandRow>> GetBrandSummaryAsync(WhBoxFilter filter, CancellationToken ct = default)
    {
        var sql = $@"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1));
            {SummarySelect(level: "brand")}
             GROUP BY sm.Division, sm.Department, w.Brand
             ORDER BY sm.Division, sm.Department, w.Brand;";

        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 240 };
        AddFilterParams(cmd, filter);

        var rows = new List<WhBrandRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WhBrandRow(
                Division:      reader.IsDBNull(0) ? null : reader.GetString(0),
                Department:    reader.IsDBNull(1) ? null : reader.GetString(1),
                Brand:         reader.IsDBNull(2) ? null : reader.GetString(2),
                LPMCurrentQty: reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                LPMFutureQty:  reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                NonLPMQty:     reader.IsDBNull(5) ? 0 : reader.GetInt64(5)));
        }
        return rows;
    }

    /// <summary>
    /// Shared SELECT body for the three summary modes — only the SELECT-list
    /// columns differ (per level), so we generate the matching prefix here
    /// and the caller appends GROUP BY / ORDER BY.
    /// </summary>
    private static string SummarySelect(string level)
    {
        // Per-level SELECT prefix: division/department/brand columns shown
        // depend on which level the user picked. The 3 qty columns
        // (LPM Current / LPM Future / Non-LPM) are identical in every mode.
        string selectCols = level switch
        {
            "div"   => "sm.Division",
            "dept"  => "sm.Division, sm.Department",
            "brand" => "sm.Division, sm.Department, w.Brand AS Brand",
            _       => "sm.Division",
        };

        return $@"
            SELECT {selectCols},
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt <  @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMCurrentQty,
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt >= @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMFutureQty,
                   SUM(CASE WHEN w.LPMDt IS NULL                                    THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS NonLPMQty
              FROM racks.dbo.whboxitems w
              INNER JOIN bfldata.dbo.pallettype          pt ON pt.PalletType = w.PalletType
              INNER JOIN Datareporting.dbo.upc_subclass    u ON u.itemcode    = w.ItemCode
              INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID      = u.MH4ID
             WHERE (@warehouse IS NULL OR w.Warehouse = @warehouse)
               AND (@typeName IS NULL OR pt.TypeName = @typeName)
               AND (@palletCategory IS NULL OR pt.PalletCategory = @palletCategory)
               AND (@lpm IS NULL OR w.LPM = @lpm)
               AND (@brand IS NULL OR w.Brand = @brand)
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL
                    OR w.PalletNo LIKE @searchLike
                    OR w.BoxNo LIKE @searchLike)
               AND (@nonPurchasedOnly = 0
                    OR w.ShopEligible IS NULL
                    OR w.ShopEligible <> 'E')
               AND (@division   IS NULL OR sm.Division   = @division)
               AND (@department IS NULL OR sm.Department = @department)";
    }

    /// <summary>Attach all filter parameters once — both the detail and summary queries
    /// accept the same filter shape so we share the binding code.</summary>
    private static void AddFilterParams(SqlCommand cmd, WhBoxFilter filter)
    {
        cmd.Parameters.Add(new SqlParameter("@warehouse",      (object?)filter.Warehouse      ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@typeName",       (object?)filter.TypeName       ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@palletCategory", (object?)filter.PalletCategory ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@lpm",            (object?)filter.Lpm            ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@lpmStatus",      (int)filter.LpmStatus));
        cmd.Parameters.Add(new SqlParameter("@search",         (object?)filter.Search         ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@searchLike",
            string.IsNullOrWhiteSpace(filter.Search) ? DBNull.Value : (object)$"%{filter.Search}%"));
        cmd.Parameters.Add(new SqlParameter("@division",       (object?)filter.Division       ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@department",     (object?)filter.Department     ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@brand",          (object?)filter.Brand          ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@nonPurchasedOnly", filter.NonPurchasedOnly ? 1 : 0));
    }

    private async Task<List<string>> ReadStringsAsync(string sql, CancellationToken ct)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        var list = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (!reader.IsDBNull(0)) list.Add(reader.GetString(0));
        return list;
    }
}
