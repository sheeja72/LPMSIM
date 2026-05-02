using System.Security.Claims;
using LpmSim.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LpmSim.Web.Auth;

/// <summary>
/// On each authenticated request, look up the user in <c>LPMUser</c> and
/// attach role claims + an <c>lpm_active</c> marker to a cloned principal.
/// Policies rely on these claims.
///
/// <para>
/// The lookup key depends on the configured authentication mode:
/// <list type="bullet">
///   <item><b>Negotiate</b> (Windows Auth) — keys on <c>LPMUser.Username</c>
///         (e.g. <c>BFLDOMAIN\sheeja</c>), pulled from
///         <see cref="ClaimsIdentity.Name"/>.</item>
///   <item><b>OIDC</b> (Microsoft Entra ID) — keys on <c>LPMUser.Email</c>
///         (e.g. <c>sheeja@bflgroup.ae</c>), pulled from the
///         <c>preferred_username</c> claim, falling back to the standard
///         <c>email</c> / <c>upn</c> claims.</item>
/// </list>
/// </para>
/// </summary>
public class LpmClaimsTransformer(
    IDbContextFactory<LpmDbContext> dbFactory,
    IConfiguration configuration) : IClaimsTransformation
{
    public const string ActiveClaim = "lpm_active";

    private readonly bool _useEmailLookup =
        string.Equals(configuration["Auth:Mode"]?.Trim(), "OIDC",
            StringComparison.OrdinalIgnoreCase);

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity baseId || !baseId.IsAuthenticated)
            return principal;

        // Re-entry guard. Both Blazor Server (per request) and SignalR (per
        // hub method) call IClaimsTransformation, and we don't want to grow
        // the role list every hop.
        if (baseId.HasClaim(c => c.Type == ActiveClaim))
            return principal;

        var lookupKey = _useEmailLookup
            ? GetEmail(principal)
            : baseId.Name;

        if (string.IsNullOrEmpty(lookupKey)) return principal;

        await using var db = await dbFactory.CreateDbContextAsync();

        // Look up the user by either Email (OIDC) or Username (Negotiate).
        // EF can't compile a runtime-flag expression inside the predicate, so
        // we branch the query at the C# level.
        var query = db.LpmUsers.AsNoTracking().Where(u => u.IsActive);
        query = _useEmailLookup
            ? query.Where(u => u.Email == lookupKey)
            : query.Where(u => u.Username == lookupKey);

        var user = await query
            .Select(u => new
            {
                u.Username,
                Roles = u.UserRoles.Select(r => r.RoleCode).ToList()
            })
            .FirstOrDefaultAsync();

        if (user is null) return principal;

        // Return a NEW principal with a cloned identity carrying our claims —
        // the underlying handler may rebuild principals between calls, so
        // mutating the original in-place is not reliable.
        var cloned = baseId.Clone();
        if (!cloned.HasClaim(c => c.Type == ActiveClaim))
            cloned.AddClaim(new Claim(ActiveClaim, "1"));
        foreach (var role in user.Roles)
        {
            if (!cloned.HasClaim(cloned.RoleClaimType, role))
                cloned.AddClaim(new Claim(cloned.RoleClaimType, role));
        }
        return new ClaimsPrincipal(cloned);
    }

    /// <summary>
    /// Pulls a usable email from the principal in priority order:
    ///   1. <c>preferred_username</c> — Entra's default for "what to show as
    ///      the signed-in user". For most tenants this equals the UPN
    ///      (<c>user@bflgroup.ae</c>).
    ///   2. Standard <see cref="ClaimTypes.Email"/>.
    ///   3. Standard <see cref="ClaimTypes.Upn"/>.
    /// </summary>
    private static string? GetEmail(ClaimsPrincipal principal) =>
        principal.FindFirst("preferred_username")?.Value
        ?? principal.FindFirst(ClaimTypes.Email)?.Value
        ?? principal.FindFirst(ClaimTypes.Upn)?.Value;
}
