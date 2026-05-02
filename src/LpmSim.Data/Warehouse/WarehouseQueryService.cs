using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace LpmSim.Data.Warehouse;

public enum LpmPresence { Any = 0, HasLpm = 1, NoLpm = 2 }

public record WhBoxFilter(
    string? Warehouse,
    string? TypeName,
    string? PalletCategory,
    string? Lpm,
    string? Search,
    LpmPresence LpmStatus = LpmPresence.Any);

public record WhBoxRow(
    string Country,
    string Warehouse,
    string PalletNo,
    string BoxNo,
    string PalletType,
    string? TypeName,
    string? PalletCategory,
    long Qty,
    string? LPM);

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

    public async Task<List<WhBoxRow>> GetBoxesAsync(WhBoxFilter filter, int top, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TOP (@top)
                   'UAE' AS Country,
                   w.Warehouse,
                   w.PalletNo,
                   w.BoxNo,
                   w.PalletType,
                   pt.TypeName,
                   pt.PalletCategory,
                   SUM(CAST(w.Qty AS bigint)) AS Qty,
                   MAX(w.LPM) AS LPM
              FROM racks.dbo.whboxitems w
              LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
             WHERE (@warehouse IS NULL OR w.Warehouse = @warehouse)
               AND (@typeName IS NULL OR pt.TypeName = @typeName)
               AND (@palletCategory IS NULL OR pt.PalletCategory = @palletCategory)
               AND (@lpm IS NULL OR w.LPM = @lpm)
               -- LPM Status filter: 0 = Any, 1 = HasLpm (LPMDt set), 2 = NoLpm (LPMDt null)
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL
                    OR w.PalletNo LIKE @searchLike
                    OR w.BoxNo LIKE @searchLike)
             GROUP BY w.Warehouse, w.PalletNo, w.BoxNo, w.PalletType, pt.TypeName, pt.PalletCategory
             ORDER BY w.Warehouse, w.PalletNo, w.BoxNo;";

        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new SqlParameter("@top", top));
        cmd.Parameters.Add(new SqlParameter("@warehouse", (object?)filter.Warehouse ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@typeName", (object?)filter.TypeName ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@palletCategory", (object?)filter.PalletCategory ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@lpm", (object?)filter.Lpm ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@lpmStatus", (int)filter.LpmStatus));
        cmd.Parameters.Add(new SqlParameter("@search", (object?)filter.Search ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@searchLike",
            string.IsNullOrWhiteSpace(filter.Search) ? DBNull.Value : (object)$"%{filter.Search}%"));

        var rows = new List<WhBoxRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WhBoxRow(
                Country:        reader.GetString(0),
                Warehouse:      reader.IsDBNull(1) ? "" : reader.GetString(1),
                PalletNo:       reader.IsDBNull(2) ? "" : reader.GetString(2),
                BoxNo:          reader.IsDBNull(3) ? "" : reader.GetString(3),
                PalletType:     reader.IsDBNull(4) ? "" : reader.GetString(4),
                TypeName:       reader.IsDBNull(5) ? null : reader.GetString(5),
                PalletCategory: reader.IsDBNull(6) ? null : reader.GetString(6),
                Qty:            reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                LPM:            reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return rows;
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
