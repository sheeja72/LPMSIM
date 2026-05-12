using System.Text;
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
    // The 8 list-shaped filters below moved from single nullable string to
    // IReadOnlyList<string>? in 1.11.0. Each one is rendered as a
    // MultiSelectFilter checkbox dropdown on the Warehouse Boxes page; the
    // SQL queries build an IN (@p0, @p1, …) clause per non-empty list.
    // null OR empty list = no filter (every value passes).
    IReadOnlyList<string>? Warehouse,
    IReadOnlyList<string>? TypeName,
    IReadOnlyList<string>? PalletCategory,
    IReadOnlyList<string>? Lpm,                        // LPM month tag (whboxitems.LPM) — separate from Brand
    string? Search,
    LpmPresence LpmStatus = LpmPresence.Any,
    IReadOnlyList<string>? Division = null,
    IReadOnlyList<string>? Department = null,
    IReadOnlyList<string>? Brand = null,               // whboxitems.Brand (the real brand)
    // Default OFF — same default behaviour as before (filter ShopEligible='E'
    // out of the result). When the planner checks the box, the
    // `ShopEligible <> 'E'` filter is dropped and "non-purchased" boxes
    // (the business term for ShopEligible='E') are also included.
    bool IncludeNonPurchased = false,
    IReadOnlyList<string>? ContNo = null,              // racks.dbo.whboxitems.ContNo — container number filter
    string? Country = "UAE");                          // SIMCountry — drives data source: UAE→racks, others→<DataName>.dbo.WHBoxItemsExport

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
    string? Purchased,                                 // "N" when ShopEligible='E', else NULL
    string? ContNo);                                   // racks.dbo.whboxitems.ContNo — container number

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

    // The four distinct-value helpers below all read whboxitems, so they
    // accept a country and resolve the right source via WhBoxItemsSource.
    // Default = "UAE" (legacy callers that don't pass a country still get
    // the UAE source). pallettype / subclassmaster are GLOBAL across
    // countries — those helpers (GetTypeNamesAsync, GetPalletCategoriesAsync,
    // GetDistinctDivisionsAsync, GetDistinctDepartmentsAsync) keep the
    // hard-coded source and don't need a country parameter.

    public async Task<List<string>> GetWarehousesAsync(string country = "UAE", CancellationToken ct = default)
    {
        var src = await ResolveSourceAsync(country, ct);
        var sql = $@"
            SELECT DISTINCT Warehouse
              FROM {src}
             WHERE Warehouse IS NOT NULL AND Warehouse <> ''
             ORDER BY Warehouse;";
        return await ReadStringsAsync(sql, ct);
    }

    public async Task<List<string>> GetLpmsAsync(string country = "UAE", CancellationToken ct = default)
    {
        var src = await ResolveSourceAsync(country, ct);
        var sql = $@"
            SELECT DISTINCT LPM
              FROM {src}
             WHERE LPM IS NOT NULL AND LPM <> ''
             ORDER BY LPM;";
        return await ReadStringsAsync(sql, ct);
    }

    public async Task<List<string>> GetContNosAsync(string country = "UAE", CancellationToken ct = default)
    {
        var src = await ResolveSourceAsync(country, ct);
        var sql = $@"
            SELECT DISTINCT ContNo
              FROM {src}
             WHERE ContNo IS NOT NULL AND ContNo <> ''
             ORDER BY ContNo;";
        return await ReadStringsAsync(sql, ct);
    }

    /// <summary>
    /// List of SIM countries from <c>bfldata.dbo.DataSettings</c> — drives
    /// the Country dropdown on Warehouse Boxes. Distinct + ordered. UAE
    /// always present even if no row sets it (defensive — UAE is the
    /// default country and the only one that reads from racks.dbo.whboxitems
    /// directly).
    /// </summary>
    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT SIMCountry
              FROM bfldata.dbo.DataSettings
             WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
             ORDER BY SIMCountry;";
        var list = await ReadStringsAsync(sql, ct);
        if (!list.Contains("UAE", StringComparer.OrdinalIgnoreCase))
            list.Insert(0, "UAE");
        return list;
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
    public async Task<List<string>> GetDistinctBrandsAsync(string country = "UAE", CancellationToken ct = default)
    {
        var src = await ResolveSourceAsync(country, ct);
        var sql = $@"
            SELECT DISTINCT Brand
              FROM {src}
             WHERE Brand IS NOT NULL AND Brand <> ''
             ORDER BY Brand;";
        return await ReadStringsAsync(sql, ct);
    }

    /// <summary>
    /// Open a connection just long enough to resolve the whboxitems source
    /// for the given country. Used by the per-helper methods above so each
    /// call resolves on its own short-lived connection (the calling
    /// helper's `ReadStringsAsync` opens its own connection for the
    /// distinct-values query). For high-frequency callers we'd want to
    /// share a connection — for the dropdown-load path (small handful of
    /// calls per page load) the per-call resolve is fine.
    /// </summary>
    private async Task<string> ResolveSourceAsync(string? country, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(country) ||
            country.Equals("UAE", StringComparison.OrdinalIgnoreCase))
        {
            return WhBoxItemsSource.UaeSource;
        }
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        return await WhBoxItemsSource.ResolveAsync(conn, country, ct);
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
        //
        // Country switch: UAE reads from racks.dbo.whboxitems; non-UAE reads
        // from [<DataName>].dbo.WHBoxItemsExport (DataName looked up from
        // DataSettings — see WhBoxItemsSource). Master tables (pallettype,
        // subclassmaster) stay GLOBAL — same join in both paths.

        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        var src = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);
        var country = string.IsNullOrWhiteSpace(filter.Country) ? "UAE" : filter.Country!;

        // Build dynamic filter fragments — each non-empty list filter becomes
        // an AND col IN (@p0, @p1, ...) clause; the scalar filters (search,
        // lpmStatus, includeNonPurchased) are still single parameters.
        // Division / Department go into HAVING here because the per-box
        // aggregate uses MAX(scm.Division) / MAX(scm.Department).
        var (whereExtra, havingExtra, filterParams) = BuildFilterClauses(filter, divDeptInHaving: true);

        var sql = $@"
            SELECT TOP (@top)
                   @country AS Country,
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
                   MAX(CASE WHEN w.ShopEligible = 'E' THEN 'N' ELSE NULL END)              AS Purchased,
                   MAX(w.ContNo)                                                           AS ContNo
              FROM {src} w
              LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
              OUTER APPLY (
                  SELECT TOP 1 sm.Division, sm.Department
                    FROM Datareporting.dbo.upc_subclass    u
                    INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                   WHERE u.itemcode = w.ItemCode
                   ORDER BY sm.Division
              ) scm
             WHERE 1 = 1
               {whereExtra}
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL
                    OR w.PalletNo LIKE @searchLike
                    OR w.BoxNo LIKE @searchLike)
               -- IncludeNonPurchased = 1 → drop the filter (show all boxes,
               -- including ShopEligible=E rows that the business calls
               -- non-purchased). 0 = apply the filter (show only available
               -- boxes — same behaviour as before).
               AND (@includeNonPurchased = 1
                    OR w.ShopEligible IS NULL
                    OR w.ShopEligible <> 'E')
             GROUP BY w.Warehouse, w.PalletNo, w.BoxNo, w.PalletType, pt.TypeName, pt.PalletCategory
            HAVING 1 = 1 {havingExtra}
             ORDER BY w.Warehouse, w.PalletNo, w.BoxNo;";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 180 };
        foreach (var p in filterParams) cmd.Parameters.Add(p);
        cmd.Parameters.Add(new SqlParameter("@top",     top));
        cmd.Parameters.Add(new SqlParameter("@country", country));

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
                Purchased:      reader.IsDBNull(13) ? null : reader.GetString(13),
                ContNo:         reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return rows;
    }

    public async Task<List<WhDivisionRow>> GetDivisionSummaryAsync(WhBoxFilter filter, CancellationToken ct = default)
    {
        // Item-level join (no GROUP BY box) — qty attributes to whichever division
        // each item actually belongs to. A box with items in 2 divisions
        // contributes proportionally to both, which is the right behaviour
        // for category-level reporting.
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        var src = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);

        var (whereExtra, _, filterParams) = BuildFilterClauses(filter, divDeptInHaving: false);

        var sql = $@"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1));
            {SummarySelect(level: "div", src: src, whereExtra: whereExtra)}
             GROUP BY sm.Division
             ORDER BY sm.Division;";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 240 };
        foreach (var p in filterParams) cmd.Parameters.Add(p);

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
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        var src = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);

        var (whereExtra, _, filterParams) = BuildFilterClauses(filter, divDeptInHaving: false);

        var sql = $@"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1));
            {SummarySelect(level: "dept", src: src, whereExtra: whereExtra)}
             GROUP BY sm.Division, sm.Department
             ORDER BY sm.Division, sm.Department;";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 240 };
        foreach (var p in filterParams) cmd.Parameters.Add(p);

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
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        var src = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);

        var (whereExtra, _, filterParams) = BuildFilterClauses(filter, divDeptInHaving: false);

        var sql = $@"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1));
            {SummarySelect(level: "brand", src: src, whereExtra: whereExtra)}
             GROUP BY sm.Division, sm.Department, w.Brand
             ORDER BY sm.Division, sm.Department, w.Brand;";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 240 };
        foreach (var p in filterParams) cmd.Parameters.Add(p);

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
    /// and the caller appends GROUP BY / ORDER BY. The whboxitems source
    /// (<paramref name="src"/>) is resolved by the caller per-country (UAE
    /// → racks.dbo.whboxitems; other → [&lt;DataName&gt;].dbo.WHBoxItemsExport).
    /// </summary>
    private static string SummarySelect(string level, string src, string whereExtra)
    {
        // Per-level SELECT prefix: division/department/brand columns shown
        // depend on which level the user picked. The 3 qty columns
        // (LPM Current / LPM Future / Non-LPM) are identical in every mode.
        //
        // <paramref name="whereExtra"/> comes from BuildFilterClauses — it
        // contains the dynamic IN (@p0, @p1, ...) clauses for the 8
        // list-shaped filters. Division/Department are included in
        // whereExtra here (not HAVING) because the summary GROUP BY is on
        // sm.Division / sm.Department directly, not on MAX(scm.Division).
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
              FROM {src} w
              INNER JOIN bfldata.dbo.pallettype          pt ON pt.PalletType = w.PalletType
              INNER JOIN Datareporting.dbo.upc_subclass    u ON u.itemcode    = w.ItemCode
              INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID      = u.MH4ID
             WHERE 1 = 1
               {whereExtra}
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL
                    OR w.PalletNo LIKE @searchLike
                    OR w.BoxNo LIKE @searchLike)
               -- IncludeNonPurchased = 1 → drop the filter (show all boxes,
               -- including ShopEligible=E rows that the business calls
               -- non-purchased). 0 = apply the filter (show only available
               -- boxes — same behaviour as before).
               AND (@includeNonPurchased = 1
                    OR w.ShopEligible IS NULL
                    OR w.ShopEligible <> 'E')";
    }

    /// <summary>
    /// Build an <c>AND col IN (@p0, @p1, ...)</c> fragment from a list of
    /// values. Returns empty fragment + empty params when the list is null
    /// / empty / contains only blanks. Each parameter gets a unique name
    /// using <paramref name="prefix"/> so multiple IN-clauses can share the
    /// same command without colliding.
    /// </summary>
    private static (string fragment, List<SqlParameter> parameters)
        BuildInClause(string colExpr, IReadOnlyList<string>? values, string prefix)
    {
        if (values is null || values.Count == 0) return ("", new List<SqlParameter>());
        var distinct = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return ("", new List<SqlParameter>());

        var paramNames = new List<string>(distinct.Count);
        var parms      = new List<SqlParameter>(distinct.Count);
        for (int i = 0; i < distinct.Count; i++)
        {
            var name = $"@{prefix}{i}";
            paramNames.Add(name);
            parms.Add(new SqlParameter(name, distinct[i]));
        }
        return ($" AND {colExpr} IN ({string.Join(", ", paramNames)})", parms);
    }

    /// <summary>
    /// Builds the per-filter SQL fragments + parameters for a WhBoxFilter.
    /// Multi-value filters become IN-clauses; single-value fields stay as
    /// scalar parameters (search, lpmStatus, includeNonPurchased).
    ///
    /// <para>
    /// Division and Department go into DIFFERENT clauses depending on the
    /// caller: the per-Box detail query (GetBoxesAsync) aggregates per box
    /// and filters Division/Department in HAVING using MAX(...). The
    /// summary queries (GetDivisionSummaryAsync etc.) join row-by-row and
    /// filter in WHERE on sm.Division / sm.Department. The
    /// <paramref name="divDeptInHaving"/> flag picks which clause receives
    /// the Division/Department filters.
    /// </para>
    /// </summary>
    private static (string whereFragment, string havingFragment, List<SqlParameter> parameters)
        BuildFilterClauses(WhBoxFilter filter, bool divDeptInHaving)
    {
        var whereSb  = new StringBuilder();
        var havingSb = new StringBuilder();
        var parms    = new List<SqlParameter>();

        void AppendWhere(string colExpr, IReadOnlyList<string>? values, string prefix)
        {
            var (frag, p) = BuildInClause(colExpr, values, prefix);
            whereSb.Append(frag);
            parms.AddRange(p);
        }

        // Shared multi-value filters (detail + summary).
        AppendWhere("w.Warehouse",       filter.Warehouse,      "wh");
        AppendWhere("pt.TypeName",       filter.TypeName,       "tn");
        AppendWhere("pt.PalletCategory", filter.PalletCategory, "pc");
        AppendWhere("w.LPM",             filter.Lpm,            "lpm");
        AppendWhere("w.Brand",           filter.Brand,          "br");
        AppendWhere("w.ContNo",          filter.ContNo,         "co");

        // Division / Department — placement depends on caller (see XML doc).
        if (divDeptInHaving)
        {
            var (divFrag, divP)   = BuildInClause("MAX(scm.Division)",   filter.Division,   "div");
            var (deptFrag, deptP) = BuildInClause("MAX(scm.Department)", filter.Department, "dept");
            havingSb.Append(divFrag);
            havingSb.Append(deptFrag);
            parms.AddRange(divP);
            parms.AddRange(deptP);
        }
        else
        {
            AppendWhere("sm.Division",   filter.Division,   "div");
            AppendWhere("sm.Department", filter.Department, "dept");
        }

        // Scalar params — shared by both query shapes.
        parms.Add(new SqlParameter("@lpmStatus",  (int)filter.LpmStatus));
        parms.Add(new SqlParameter("@search",     (object?)filter.Search ?? DBNull.Value));
        parms.Add(new SqlParameter("@searchLike",
            string.IsNullOrWhiteSpace(filter.Search) ? DBNull.Value : (object)$"%{filter.Search}%"));
        parms.Add(new SqlParameter("@includeNonPurchased", filter.IncludeNonPurchased ? 1 : 0));

        return (whereSb.ToString(), havingSb.ToString(), parms);
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
