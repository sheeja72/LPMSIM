using LpmSim.Core;
using LpmSim.Core.Entities;
using LpmSim.Data;
using LpmSim.Data.LpmSim;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Web.Hosting;

/// <summary>
/// 1.14.75 — Nightly scheduler that auto-builds SKU Max at the configured
/// GCC-local time (default 04:00 GST) for every country with at least one
/// row in <c>bfldata.dbo.DataSettings</c> where <c>SIMCountry</c> is set.
///
/// <para>
/// Each country's build runs sequentially via the existing
/// <see cref="SkuMaxBuildJobManager"/> — the same path the UI's "Build SKU
/// Max" button uses — so the "Last Build" panel on SIM Generate sees the
/// scheduler's runs identically to manual ones. Builds are stamped with
/// <c>CreatedBy = 'scheduler@system'</c> so they're easy to identify in
/// <c>LPM_SimItemSkuMaxBuild</c>.
/// </para>
///
/// <para>
/// Failure handling: if a country's build fails (e.g. missing
/// <c>DataSettings.DataName</c> for a non-UAE country, missing Sales/Turns,
/// transient DB error), the error is logged and the scheduler proceeds to
/// the next country. One country's blocker doesn't stop the rest.
/// </para>
///
/// <para>
/// Configuration in <c>appsettings.json</c>:
/// <code>
/// "ScheduledBuilds": {
///   "SkuMax": {
///     "Enabled":    true,       // master on/off switch
///     "DailyAtGst": "04:00"     // HH:mm in GCC local time (UTC+4)
///   }
/// }
/// </code>
/// </para>
///
/// <para>
/// Multi-instance note: this scheduler assumes a single App Service
/// instance. If you ever scale to multiple instances, two BackgroundServices
/// will fire simultaneously — <see cref="SkuMaxBuildJobManager.Start"/>'s
/// in-process duplicate check protects WITHIN an instance only. Add a
/// DB-based leader lock (e.g. INSERT into a <c>LPM_ScheduledJobs</c> table
/// with <c>(JobName, Date)</c> as the unique key) before scaling out.
/// </para>
///
/// <para>
/// Hosted by the Web project (not Data) because <c>BackgroundService</c>
/// lives in <c>Microsoft.Extensions.Hosting</c>, which is part of the
/// ASP.NET Core SDK that <c>LpmSim.Web</c> already references — keeps
/// <c>LpmSim.Data</c> independent of the hosting story.
/// </para>
/// </summary>
public class SkuMaxBuildScheduler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly SkuMaxBuildJobManager _jobs;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SkuMaxBuildScheduler> _log;

    public SkuMaxBuildScheduler(
        IServiceProvider services,
        SkuMaxBuildJobManager jobs,
        IConfiguration cfg,
        ILogger<SkuMaxBuildScheduler> log)
    {
        _services = services;
        _jobs     = jobs;
        _cfg      = cfg;
        _log      = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defer the first cycle a few seconds after startup so the rest of
        // the app finishes initialising (DI scopes, EF, etc.) before we
        // start anything heavy. Doesn't matter unless the app restarts
        // exactly at 04:00 GST — in which case it delays by 15s.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Refresh config every cycle so an admin can toggle Enabled
                // / DailyAtGst without restarting the app (works via Azure
                // App Service Application settings — changes take effect on
                // the next cycle, which is at most 24 hours away).
                var enabled = _cfg.GetValue<bool?>("ScheduledBuilds:SkuMax:Enabled") ?? true;
                var dailyAt = _cfg.GetValue<string?>("ScheduledBuilds:SkuMax:DailyAtGst") ?? "04:00";

                if (!TimeOnly.TryParse(dailyAt, out var target))
                {
                    _log.LogWarning(
                        "SkuMaxBuildScheduler: ScheduledBuilds:SkuMax:DailyAtGst = '{Raw}' is not a valid HH:mm value; falling back to 04:00 GST.",
                        dailyAt);
                    target = new TimeOnly(4, 0);
                }

                var nextRunUtc = TimeFormatting.NextGstUtc(target);
                var waitFor    = nextRunUtc - DateTime.UtcNow;
                if (waitFor < TimeSpan.Zero) waitFor = TimeSpan.Zero;

                _log.LogInformation(
                    "SkuMaxBuildScheduler: enabled={Enabled}; next run at {RunUtc:yyyy-MM-dd HH:mm} UTC ({Target} GST, in {Wait}).",
                    enabled, nextRunUtc, target, waitFor);

                try { await Task.Delay(waitFor, stoppingToken).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }

                if (!enabled)
                {
                    _log.LogInformation("SkuMaxBuildScheduler: disabled via config; skipping this cycle.");
                    continue;
                }

                await RunAllCountriesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex, "SkuMaxBuildScheduler: cycle failed unexpectedly; sleeping 5 minutes before retrying.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Discover countries (DataSettings.SIMCountry distinct, non-empty) and
    /// fire a Build SKU Max for the current GST month, sequentially.
    /// Sequential — not parallel — because each build is DB-heavy and
    /// running them concurrently would compound the SQL Server load.
    /// </summary>
    private async Task RunAllCountriesAsync(CancellationToken stoppingToken)
    {
        var nowGst       = TimeFormatting.NowGst();
        var year         = nowGst.Year;
        var month        = nowGst.Month;
        const string startedBy = "scheduler@system";

        // Discover countries from DataSettings.SIMCountry (distinct, trimmed,
        // non-empty). Captures every country planners have configured at
        // the SIM level — new countries get picked up automatically once
        // their DataSettings rows are populated.
        List<string> countries;
        using (var scope = _services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<LpmDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            countries = await db.DataSettings.AsNoTracking()
                .Where(s => s.SIMCountry != null && s.SIMCountry != "")
                .Select(s => s.SIMCountry!.Trim())
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync(stoppingToken);
        }

        if (countries.Count == 0)
        {
            _log.LogWarning("SkuMaxBuildScheduler: no countries found in DataSettings.SIMCountry; nothing to build.");
            return;
        }

        _log.LogInformation(
            "SkuMaxBuildScheduler: starting nightly build for {Count} countries (period {Year:D4}-{Month:D2}): {Countries}",
            countries.Count, year, month, string.Join(", ", countries));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int succeeded = 0, failed = 0, skipped = 0;

        foreach (var country in countries)
        {
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                if (_jobs.IsRunning(country, year, month))
                {
                    _log.LogInformation(
                        "SkuMaxBuildScheduler: skipping {Country} {Year:D4}-{Month:D2} — a build is already running.",
                        country, year, month);
                    skipped++;
                    continue;
                }

                var job = _jobs.Start(country, year, month, startedBy, LpmSimSkuMaxScope.All);
                _log.LogInformation(
                    "SkuMaxBuildScheduler: started {Country} {Year:D4}-{Month:D2} (jobId={JobId}).",
                    country, year, month, job.JobId);

                // Wait for the job to finish before kicking off the next
                // country's build. Sequential lets each build use the
                // full SQL Server connection budget; parallel would risk
                // tempdb / pool exhaustion on KSA-sized periods.
                if (job.RunningTask is not null)
                {
                    try { await job.RunningTask.ConfigureAwait(false); }
                    catch { /* job.Status already captured the failure */ }
                }

                switch (job.Status)
                {
                    case SkuMaxBuildStatus.Completed:
                        succeeded++;
                        _log.LogInformation(
                            "SkuMaxBuildScheduler: {Country} {Year:D4}-{Month:D2} OK — {Rows:N0} rows in {Duration}.",
                            country, year, month, job.RowCount, job.Duration);
                        break;
                    case SkuMaxBuildStatus.Failed:
                        failed++;
                        _log.LogWarning(
                            "SkuMaxBuildScheduler: {Country} {Year:D4}-{Month:D2} FAILED — {Error}",
                            country, year, month, job.Error ?? job.StatusMessage ?? "(no error message)");
                        break;
                    case SkuMaxBuildStatus.Cancelled:
                        failed++;
                        _log.LogWarning(
                            "SkuMaxBuildScheduler: {Country} {Year:D4}-{Month:D2} CANCELLED — {Msg}",
                            country, year, month, job.StatusMessage ?? "(no detail)");
                        break;
                    default:
                        failed++;
                        _log.LogWarning(
                            "SkuMaxBuildScheduler: {Country} {Year:D4}-{Month:D2} ended in unexpected status {Status}.",
                            country, year, month, job.Status);
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogError(ex,
                    "SkuMaxBuildScheduler: {Country} {Year:D4}-{Month:D2} threw during Start — skipping.",
                    country, year, month);
            }
        }

        stopwatch.Stop();
        _log.LogInformation(
            "SkuMaxBuildScheduler: nightly cycle complete. {Succeeded} succeeded, {Failed} failed, {Skipped} skipped; total elapsed {Elapsed}.",
            succeeded, failed, skipped, stopwatch.Elapsed);
    }
}
