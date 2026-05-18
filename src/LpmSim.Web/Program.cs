using LpmSim.Core;
using LpmSim.Data;
using LpmSim.Data.Auditing;
using LpmSim.Data.Eom;
using LpmSim.Data.LpmSim;
using LpmSim.Data.Reports;
using LpmSim.Data.Warehouse;
using LpmSim.Web.Auth;
using LpmSim.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server;
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

        // Blazor Server circuit + SignalR hub timeouts.
        //
        // Goal: one login lasts 24 hours, including overnight laptop sleeps,
        // long meetings, and idle stretches. Every "session-ish" timer is
        // set to 24h.
        //
        //   KeepAliveInterval = 15s — server pings client every 15s. Must
        //   stay ≤ half the JS client's serverTimeout so the connection
        //   doesn't time out waiting for pings.
        //
        //   ClientTimeoutInterval = 24h — server tolerates 24h of client
        //   silence before declaring it dead. Pairs with the JS reconnect
        //   policy in App.razor (very long retry window).
        //
        //   DisconnectedCircuitRetentionPeriod = 24h — when the underlying
        //   WebSocket dies (laptop sleep, network drop), we hold the
        //   in-memory circuit + auth state for 24h waiting for a reconnect.
        //
        //   JSInteropDefaultCallTimeout = 30 min — covers any reasonable
        //   user operation, including big Excel exports.
        //
        //   HandshakeTimeout = 2 min — cold-start tolerance.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddHubOptions(options =>
            {
                options.KeepAliveInterval     = TimeSpan.FromSeconds(15);
                options.ClientTimeoutInterval = TimeSpan.FromHours(24);
                options.HandshakeTimeout      = TimeSpan.FromMinutes(2);
            });

        builder.Services.Configure<CircuitOptions>(options =>
        {
            options.DisconnectedCircuitMaxRetained     = 200;
            options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(24);
            options.JSInteropDefaultCallTimeout        = TimeSpan.FromMinutes(30);
        });

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
        builder.Services.AddScoped<WeeklySalesTargetSplitService>();
        builder.Services.AddScoped<LpmSimGenerator>();
        builder.Services.AddScoped<LpmSimReportService>();
        builder.Services.AddScoped<LpmSimInvestigator>();
        builder.Services.AddScoped<ProductionScheduler>();
        builder.Services.AddScoped<LpmAdmService>();
        builder.Services.AddScoped<WarehouseQueryService>();
        builder.Services.AddScoped<WhHoStockService>();
        builder.Services.AddScoped<VarianceReportService>();
        builder.Services.AddScoped<WhItemsReportService>();

        // SKU Max background job manager — Singleton so a Build can survive
        // the user navigating away from the SIM Generate page. Builds run on
        // a manager-owned CancellationTokenSource (not the page's), so
        // navigation no longer kills the build mid-way.
        builder.Services.AddSingleton<SkuMaxBuildJobManager>();

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
                    // 24-hour cookie with sliding expiration. Any activity
                    // pushes the expiry forward; only a fully-idle 24h
                    // stretch forces a re-login through Entra.
                    opts.ExpireTimeSpan    = TimeSpan.FromHours(24);
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

        // Only enforce HTTPS redirect in non-Development environments. Locally
        // Kestrel is HTTP-only on port 5216, so the redirect middleware can't
        // resolve an HTTPS port and silently swallows requests — which is
        // what was throwing the dev-time "Failed to determine the https port
        // for redirect" warning + producing intermittent failed requests.
        if (!app.Environment.IsDevelopment())
        {
            app.UseHttpsRedirection();
        }
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
