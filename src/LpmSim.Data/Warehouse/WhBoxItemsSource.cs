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
        var dataName = await ResolveDataNameAsync(conn, country, ct);
        return dataName is null
            ? UaeSource
            : $"[{dataName}].dbo.WHBoxItemsExport";
    }

    /// <summary>
    /// 1.14.70 — Returns the bare <c>DataName</c> (e.g. <c>"KSADATA"</c>) for the
    /// country, or <c>null</c> for UAE. Useful when the caller needs to build an
    /// FQTN for a NEIGHBOUR table in the same country DB — e.g.
    /// <c>[<![CDATA[<DataName>]]>].dbo.Exclude_Transfers_Sim</c> or
    /// <c>[<![CDATA[<DataName>]]>].dbo.CloseR1Pallet</c> (the closed-box
    /// exclusion sources introduced by the SIM Generate closed-box filter).
    /// Same validation rules as <see cref="ResolveAsync"/> — throws on missing
    /// or malformed DataName.
    /// </summary>
    public static async Task<string?> ResolveDataNameAsync(
        DbConnection conn, string? country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country) ||
            country.Equals("UAE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
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

        return dataName;
    }

    /// <summary>
    /// 1.14.70 — Build the SQL fragment that flags closed boxes for the given country.
    /// Used by SIM Generate's <c>ReadBoxesAsync</c> to project an <c>IsClosed</c>
    /// bit on every whboxitems row + by the closed-box meta query for the Gap
    /// diagnostic. Returns a single boolean expression suitable for either
    /// <c>CASE WHEN ... THEN 1 ELSE 0 END</c> projection or a <c>WHERE</c>
    /// clause.
    ///
    /// <para>
    /// Country rules:
    /// <list type="bullet">
    ///   <item>UAE → <c>EXISTS (SELECT 1 FROM USA.dbo.upcboxhead h WHERE h.BoxNo = w.BoxNo AND h.Closed = 'Y')</c></item>
    ///   <item>Non-UAE → <c>EXISTS (... Exclude_Transfers_Sim by Trfno = w.BoxNo)</c> OR <c>EXISTS (... CloseR1Pallet by palletno = w.BoxNo)</c></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Caller must alias the <c>whboxitems</c> source row as <c>w</c> — the
    /// fragment references <c>w.BoxNo</c> directly so it composes cleanly into
    /// the existing FROM-clause aliasing.
    /// </para>
    /// </summary>
    public static string BuildIsClosedExpression(string? country, string? dataName)
    {
        if (string.IsNullOrWhiteSpace(country) ||
            country.Equals("UAE", StringComparison.OrdinalIgnoreCase))
        {
            return @"EXISTS (SELECT 1 FROM USA.dbo.upcboxhead h
                              WHERE h.BoxNo = w.BoxNo AND h.Closed = 'Y')";
        }

        // Non-UAE: dataName MUST have been resolved by the caller. We sanitised
        // it in ResolveDataNameAsync, so it's safe to splice into the FQTN.
        if (string.IsNullOrWhiteSpace(dataName))
        {
            throw new InvalidOperationException(
                $"BuildIsClosedExpression for SIMCountry '{country}' requires a resolved DataName.");
        }

        return $@"(EXISTS (SELECT 1 FROM [{dataName}].dbo.Exclude_Transfers_Sim e
                            WHERE e.Trfno = w.BoxNo)
                OR EXISTS (SELECT 1 FROM [{dataName}].dbo.CloseR1Pallet c
                            WHERE c.palletno = w.BoxNo))";
    }
}
