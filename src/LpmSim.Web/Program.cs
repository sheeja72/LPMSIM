using LpmSim.Core;
using LpmSim.Data;
using LpmSim.Data.Auditing;
using LpmSim.Data.Eom;
using LpmSim.Data.LpmSim;
using LpmSim.Data.Warehouse;
using LpmSim.Web.Auth;
using LpmSim.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor;
using MudBlazor.Services;

namespace LpmSim.Web;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Always pull User Secrets so the app boots regardless of which environment
        // dotnet run picks up (some terminal sessions don't end up in Development).
        builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: false);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddMudServices(c =>
        {
            // Default snackbar lifetime — keeps non-error toasts a bit longer
            // and (via per-call overrides) lets error toasts stay until clicked.
            c.SnackbarConfiguration.PositionClass        = Defaults.Classes.Position.BottomRight;
            c.SnackbarConfiguration.VisibleStateDuration = 8000;
            c.SnackbarConfiguration.ShowCloseIcon        = true;
            c.SnackbarConfiguration.PreventDuplicates    = false;
        });

        builder.Services.AddScoped<ICurrentUser, AuthStateCurrentUser>();
        builder.Services.AddScoped<AuditSaveChangesInterceptor>();
        builder.Services.AddScoped<IActionLogger, ActionLogger>();

        var connStr = builder.Configuration.GetConnectionString("Lpm")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Lpm (set via User Secrets).");

        builder.Services.AddDbContextFactory<LpmDbContext>((sp, o) =>
        {
            o.UseSqlServer(connStr);
            o.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
        }, ServiceLifetime.Scoped);

        builder.Services.AddScoped<EomCalculator>();
        builder.Services.AddScoped<LpmSimGenerator>();
        builder.Services.AddScoped<LpmSimReportService>();
        builder.Services.AddScoped<LpmSimInvestigator>();
        builder.Services.AddScoped<WarehouseQueryService>();

        // ─── Authentication ──────────────────────────────────────────────
        // Two modes selected via configuration `Auth:Mode`:
        //   "Negotiate" (default) — Windows / Kerberos, no UI changes needed.
        //   "OIDC"                — Microsoft Entra ID SSO. The user signs in
        //                           with their corporate email; Entra issues a
        //                           token; we validate it and drop a cookie so
        //                           subsequent SignalR calls stay authenticated.
        //
        // The mode is read at startup so the rest of the pipeline doesn't have
        // to branch — picking OIDC just swaps the auth handler chain.
        var authMode = (builder.Configuration["Auth:Mode"] ?? "Negotiate")
            .Trim().ToUpperInvariant();

        if (authMode == "OIDC")
        {
            // Cookie holds the validated identity for the lifetime of the SignalR
            // circuit; OpenIdConnect handles the redirect dance with Entra.
            builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

            // Microsoft.Identity.Web ships sign-in / sign-out endpoints under
            // /MicrosoftIdentity/Account/{SignIn,SignOut}. Register controllers
            // so they are reachable via MapControllers below.
            builder.Services.AddControllersWithViews()
                .AddMicrosoftIdentityUI();

            builder.Services.Configure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme,
                opts =>
                {
                    opts.ExpireTimeSpan    = TimeSpan.FromMinutes(60);
                    opts.SlidingExpiration = true;
                });
        }
        else
        {
            builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                .AddNegotiate();
        }

        // The transformer reads the same Auth:Mode value to decide whether to
        // look up LPMUser by Username (Negotiate) or Email (OIDC).
        builder.Services.AddScoped<IClaimsTransformation, LpmClaimsTransformer>();

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.RequireLpmUser, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(LpmClaimsTransformer.ActiveClaim, "1"));

            options.FallbackPolicy = options.GetPolicy(AuthPolicies.RequireLpmUser);
        });
        builder.Services.AddCascadingAuthenticationState();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseAntiforgery();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        // OIDC mode only — exposes /MicrosoftIdentity/Account/SignIn and
        // /MicrosoftIdentity/Account/SignOut. Negotiate mode doesn't need
        // controller routing.
        if (authMode == "OIDC")
        {
            app.MapControllers();
        }

        app.Run();
    }
}
