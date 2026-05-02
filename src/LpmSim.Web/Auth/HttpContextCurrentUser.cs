using System.Security.Claims;
using LpmSim.Core;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;

namespace LpmSim.Web.Auth;

/// <summary>
/// Resolves the authenticated user via the Blazor AuthenticationStateProvider so
/// it works during both static SSR and the SignalR circuit (where HttpContext
/// is null).
///
/// Under <c>Auth:Mode = Negotiate</c> this returns <c>Identity.Name</c>
/// (e.g. <c>BFLDOMAIN\sheeja</c>). Under <c>Auth:Mode = OIDC</c> the
/// <c>preferred_username</c> / email claim is preferred so audit log rows
/// carry a recognisable identifier (<c>sheeja@bflgroup.ae</c>) instead of an
/// empty string.
/// </summary>
public class AuthStateCurrentUser(
    AuthenticationStateProvider authStateProvider,
    IConfiguration configuration) : ICurrentUser
{
    private readonly bool _useEmail =
        string.Equals(configuration["Auth:Mode"]?.Trim(), "OIDC",
            StringComparison.OrdinalIgnoreCase);

    public string Name
    {
        get
        {
            var state = authStateProvider.GetAuthenticationStateAsync()
                .GetAwaiter().GetResult();
            var user = state.User;
            if (user.Identity?.IsAuthenticated != true) return "system";

            if (_useEmail)
            {
                var email = user.FindFirst("preferred_username")?.Value
                          ?? user.FindFirst(ClaimTypes.Email)?.Value
                          ?? user.FindFirst(ClaimTypes.Upn)?.Value;
                if (!string.IsNullOrWhiteSpace(email)) return email;
            }

            return user.Identity?.Name ?? "system";
        }
    }
}
