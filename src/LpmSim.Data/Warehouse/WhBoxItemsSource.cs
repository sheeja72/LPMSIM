using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace LpmSim.Data.Warehouse;

/// <summary>
/// Single source of truth for resolving the warehouse-boxes table reference
/// per country. UAE reads from <c>racks.dbo.whboxitems</c> (the historical
/// operational table); every other country reads from
/// <c>[&lt;DataName&gt;].dbo.WHBoxItemsExport</c>, where <c>DataName</c> is
/// looked up from <c>bfldata.dbo.DataSettings</c>.
///
/// <para>
/// All country DBs live on the same SQL Server instance, so cross-database
/// references work directly without linked servers. The export schema is
/// identical to <c>whboxitems</c> per business confirmation — every column
/// the SIM and Warehouse Boxes report use (Warehouse, PalletNo, BoxNo,
/// PalletType, ItemCode, Qty, LPM, LPMDt, Brand, Rack, ShopEligible,
/// ContNo) exists in both tables, so the only thing that changes between
/// UAE and non-UAE queries is the FROM clause.
/// </para>
///
/// <para>
/// Master tables (<c>bfldata.dbo.pallettype</c>,
/// <c>Datareporting.dbo.upc_subclass × subclassmaster × Division</c>) are
/// global per business confirmation — they are NOT swapped per country.
/// </para>
/// </summary>
public static class WhBoxItemsSource
{
    /// <summary>
    /// Hard-coded UAE source. Use this when the caller already knows the
    /// country is UAE and wants to skip the DataSettings round-trip.
    /// </summary>
    public const string UaeSource = "racks.dbo.whboxitems";

    /// <summary>
    /// Resolve the fully-qualified table reference for the given country.
    /// Throws <see cref="InvalidOperationException"/> when a non-UAE country
    /// has no <c>DataName</c> configured (the cross-DB query would fail
    /// silently otherwise).
    /// </summary>
    public static async Task<string> ResolveAsync(
        DbConnection conn, string? country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country) ||
            country.Equals("UAE", StringComparison.OrdinalIgnoreCase))
        {
            return UaeSource;
        }

        // Look up DataName for this country. DataSettings has one row per
        // store — DataName is the same for every store of a given country,
        // so TOP 1 is fine.
        string? dataName = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TOP 1 DataName
                  FROM bfldata.dbo.DataSettings
                 WHERE SIMCountry = @c
                   AND DataName IS NOT NULL
                   AND LTRIM(RTRIM(DataName)) <> '';";
            var p = cmd.CreateParameter();
            p.ParameterName = "@c";
            p.Value         = country;
            cmd.Parameters.Add(p);
            cmd.CommandTimeout = 30;
            var v = await cmd.ExecuteScalarAsync(ct);
            dataName = v is string s ? s.Trim() : null;
        }

        if (string.IsNullOrWhiteSpace(dataName))
        {
            throw new InvalidOperationException(
                $"No DataName configured in bfldata.dbo.DataSettings for SIMCountry '{country}'. " +
                "Country-specific warehouse data can't be loaded until DataSettings.DataName is populated.");
        }

        // Sanitize: SQL Server identifiers should be alphanumeric/underscore.
        // DataName comes from a trusted internal table but we defend in depth
        // — if any future ETL drift puts a malformed value here, the query
        // would otherwise be vulnerable to SQL injection via the FROM clause.
        if (!Regex.IsMatch(dataName, @"^[A-Za-z0-9_]+$"))
        {
            throw new InvalidOperationException(
                $"Invalid DataName '{dataName}' for SIMCountry '{country}' — must contain only " +
                "alphanumeric characters and underscores. Fix the DataName value in DataSettings.");
        }

        return $"[{dataName}].dbo.WHBoxItemsExport";
    }
}
