using LpmSim.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data;

/// <summary>
/// 1.14.77 — Helpers around the <c>LPM_CountryLink</c> table.
///
/// <para>
/// Two perspectives the EOM/SIM pipeline needs:
/// </para>
///
/// <list type="bullet">
///   <item><see cref="GetParentCountryAsync"/> — "if this country is a child
///         (shipped from another's warehouse), what's the parent?".
///         Used by EOM Calculator so OMAN's EOM reads UAE's whboxitems.</item>
///   <item><see cref="GetChildCountriesAsync"/> — "if this country is a
///         parent (ships to other countries), who are the children?".
///         Used by SIM Generate so UAE's run includes OMAN stores.</item>
/// </list>
///
/// <para>
/// Only <c>IsActive = 1</c> rows participate. Inactivating a link lets you
/// pause the linkage without dropping data (e.g. temporarily split OMAN
/// off into its own SIM batch).
/// </para>
///
/// <para>
/// The unique constraint on <c>ChildCountry</c> (see migration 057)
/// guarantees the parent lookup never has more than one match, so the
/// resolver returns a single string (not a list) for that direction.
/// </para>
/// </summary>
public static class CountryLinkResolver
{
    /// <summary>
    /// Return the parent country for the given child, or <c>null</c> when
    /// the country has no parent link (which is the default for every
    /// country — links are opt-in).
    /// </summary>
    public static async Task<string?> GetParentCountryAsync(
        LpmDbContext db, string childCountry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(childCountry)) return null;
        var trimmed = childCountry.Trim();
        return await db.LpmCountryLinks.AsNoTracking()
            .Where(l => l.IsActive && l.ChildCountry == trimmed)
            .Select(l => l.ParentCountry)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Return the child countries for the given parent, sorted alphabetically.
    /// Empty list when the country has no children (the default).
    /// </summary>
    public static async Task<List<string>> GetChildCountriesAsync(
        LpmDbContext db, string parentCountry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(parentCountry)) return new();
        var trimmed = parentCountry.Trim();
        return await db.LpmCountryLinks.AsNoTracking()
            .Where(l => l.IsActive && l.ParentCountry == trimmed)
            .Select(l => l.ChildCountry)
            .OrderBy(c => c)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Resolve the effective WH-source country for the given country.
    /// Returns the parent if linked, else the country itself. Convenience
    /// for EOM Calculator's WH-source resolution — one call returns the
    /// right country to pass to <c>WhBoxItemsSource.ResolveAsync</c>.
    /// </summary>
    public static async Task<string> ResolveWhSourceCountryAsync(
        LpmDbContext db, string country, CancellationToken ct = default)
    {
        var parent = await GetParentCountryAsync(db, country, ct);
        return parent ?? country;
    }
}
