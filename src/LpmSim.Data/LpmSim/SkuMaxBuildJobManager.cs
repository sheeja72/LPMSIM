using LpmSim.Core;
using LpmSim.Core.Entities;
using LpmSim.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace LpmSim.Data.LpmSim;

/// <summary>
/// Singleton background job manager for "Build SKU Max" operations.
///
/// <para>
/// Why this exists: Build SKU Max can take several minutes on large country
/// datasets. The previous flow tied the job to the user's Blazor circuit —
/// navigating away from the SIM Generate page disposed the page's
/// CancellationToken and aborted the build mid-way. This manager runs the
/// build on its own task with a manager-owned CancellationTokenSource, so:
/// </para>
/// <list type="bullet">
/// <item>Build keeps running when the user navigates to a different page.</item>
/// <item>Returning to SIM Generate re-attaches to the live job (start/end/duration
///       state survives navigation).</item>
/// <item>The user can explicitly Cancel the in-flight build via the UI.</item>
/// </list>
///
/// <para>
/// At most one job runs per (Country, Year, Month) at a time. Completed /
/// failed / cancelled jobs are kept in memory for at least
/// <see cref="CompletedRetentionMinutes"/> minutes so the UI can still
/// display the last build's outcome after the user comes back.
/// </para>
/// </summary>
public class SkuMaxBuildJobManager
{
    /// <summary>How long a completed/failed/cancelled job stays in the dictionary.</summary>
    private const int CompletedRetentionMinutes = 60;

    private readonly IServiceProvider _services;
    private readonly ILogger<SkuMaxBuildJobManager> _log;
    private readonly ConcurrentDictionary<string, SkuMaxBuildJob> _jobs = new();

    public event EventHandler<SkuMaxBuildJob>? JobChanged;

    public SkuMaxBuildJobManager(IServiceProvider services, ILogger<SkuMaxBuildJobManager> log)
    {
        _services = services;
        _log = log;
    }

    private static string Key(string country, int year, int month)
        => $"{country?.ToUpperInvariant() ?? ""}|{year:D4}|{month:D2}";

    /// <summary>Get the current job (running OR recently completed) for a period, if any.</summary>
    public SkuMaxBuildJob? Get(string country, int year, int month)
    {
        // Sweep stale completed jobs lazily on every Get call.
        SweepCompleted();
        return _jobs.TryGetValue(Key(country, year, month), out var j) ? j : null;
    }

    /// <summary>Returns true if a job is currently Running for the period.</summary>
    public bool IsRunning(string country, int year, int month)
    {
        var j = Get(country, year, month);
        return j is not null && j.Status == SkuMaxBuildStatus.Running;
    }

    /// <summary>
    /// 1.14.102 — Return the active lock row for the country, or <c>null</c>
    /// when not locked. Row presence in <c>LPM_SkuMaxLock</c> = locked.
    /// <para>
    /// Used by both the manual "Build SKU Max" click path (razor) and the
    /// nightly <see cref="LpmSim.Web.Hosting.SkuMaxBuildScheduler"/> to
    /// short-circuit BEFORE invoking <see cref="Start"/>. The Generator's
    /// <c>BuildSkuMaxAsync</c> also re-checks at the top as a safety net.
    /// </para>
    /// </summary>
    public async Task<LpmSkuMaxLock?> GetLockAsync(string country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        var trimmed = country.Trim();
        using var diScope = _services.CreateScope();
        var dbFactory = diScope.ServiceProvider.GetRequiredService<IDbContextFactory<LpmDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LpmSkuMaxLocks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Country == trimmed, ct);
    }

    /// <summary>
    /// Convenience boolean wrapper around <see cref="GetLockAsync"/>.
    /// </summary>
    public async Task<bool> IsLockedAsync(string country, CancellationToken ct = default)
        => (await GetLockAsync(country, ct)) is not null;

    /// <summary>
    /// Kick off a Build SKU Max in the background. Returns the job (Running)
    /// without waiting for completion. Throws if a build is already running
    /// for the same (Country, Year, Month) — even for a different scope, since
    /// concurrent builds against the same period can collide on
    /// <c>LPM_SimItemSkuMax</c> writes.
    /// </summary>
    public SkuMaxBuildJob Start(
        string country, int year, int month, string startedBy,
        LpmSimSkuMaxScope scope = LpmSimSkuMaxScope.All)
    {
        SweepCompleted();
        var key = Key(country, year, month);

        if (_jobs.TryGetValue(key, out var existing) && existing.Status == SkuMaxBuildStatus.Running)
            throw new InvalidOperationException(
                $"A SKU Max build is already running for {country} {year:D4}-{month:D2} " +
                $"(started {existing.StartedAt:dd-MMM HH:mm:ss}{(string.IsNullOrEmpty(existing.StartedBy) ? "" : $" by {existing.StartedBy}")}).");

        // 1.14.102 — Lock gate. Both callers (razor click handler + nightly
        // scheduler) check LPM_SkuMaxLock asynchronously BEFORE invoking
        // Start, but we re-check here so any future caller that forgets the
        // pre-check still hits a clean refusal. The lookup is a single PK
        // index seek on a tiny table — running it sync via GetAwaiter is
        // acceptable (no real blocking happens).
        var lockRow = GetLockAsync(country).GetAwaiter().GetResult();
        if (lockRow is not null)
            throw new InvalidOperationException(
                $"Build SKU Max is LOCKED for {country} " +
                $"(locked {lockRow.LockedAt:dd-MMM-yyyy HH:mm}" +
                (string.IsNullOrEmpty(lockRow.LockedBy) ? "" : $" by {lockRow.LockedBy}") +
                (string.IsNullOrEmpty(lockRow.Reason)   ? "" : $" — {lockRow.Reason}") +
                "). Delete the LPM_SkuMaxLock row to unlock.");

        var cts = new CancellationTokenSource();
        var job = new SkuMaxBuildJob
        {
            JobId      = Guid.NewGuid(),
            Country    = country,
            Year       = year,
            Month      = month,
            Scope      = scope,
            Status     = SkuMaxBuildStatus.Running,
            StartedAt  = DateTime.Now,
            StartedBy  = startedBy ?? "",
            Cts        = cts,
        };
        _jobs[key] = job;
        Notify(job);

        // Live progress hook — every Report(...) from the generator updates
        // job.StatusMessage and fires JobChanged so any subscribed UI sees
        // the current stage without polling.
        var progress = new Progress<string>(msg =>
        {
            job.StatusMessage = msg;
            Notify(job);
        });

        // Fire and forget; the manager owns the lifecycle.
        job.RunningTask = Task.Run(async () =>
        {
            try
            {
                // Build a fresh DI scope so we don't depend on the originating
                // request's scope (which would dispose when the user navigates).
                using var diScope = _services.CreateScope();
                var generator = diScope.ServiceProvider.GetRequiredService<LpmSimGenerator>();
                var status = await generator.BuildSkuMaxAsync(
                    country, year, month, cts.Token,
                    userOverride: startedBy, progress: progress, scope: scope);

                job.RowCount    = status.RowCount;
                job.CompletedAt = DateTime.Now;
                job.Duration    = job.CompletedAt - job.StartedAt;
                job.Status      = SkuMaxBuildStatus.Completed;
                job.StatusMessage = $"Built {status.RowCount:N0} rows in {FormatDuration(job.Duration.Value)}.";
            }
            catch (OperationCanceledException)
            {
                job.CompletedAt = DateTime.Now;
                job.Duration    = job.CompletedAt - job.StartedAt;
                job.Status      = SkuMaxBuildStatus.Cancelled;
                job.StatusMessage = $"Cancelled after {FormatDuration(job.Duration.Value)}.";
                _log.LogInformation("SKU Max build cancelled for {Country} {Year}-{Month}", country, year, month);
            }
            catch (Exception ex) when (cts.IsCancellationRequested)
            {
                // The force-close-on-cancel handler in BuildSkuMaxAsync slammed
                // the SqlConnection shut, which surfaces as SqlException /
                // InvalidOperationException rather than OperationCanceledException.
                // Bucket those as Cancelled (not Failed) when the user actually
                // pressed Cancel — otherwise the banner says "Failed" with a
                // confusing "connection forcibly closed" message.
                job.CompletedAt = DateTime.Now;
                job.Duration    = job.CompletedAt - job.StartedAt;
                job.Status      = SkuMaxBuildStatus.Cancelled;
                job.StatusMessage = $"Cancelled after {FormatDuration(job.Duration.Value)}.";
                _log.LogInformation(ex, "SKU Max build cancelled (via connection close) for {Country} {Year}-{Month}", country, year, month);
            }
            catch (Exception ex)
            {
                job.CompletedAt = DateTime.Now;
                job.Duration    = job.CompletedAt - job.StartedAt;
                job.Status      = SkuMaxBuildStatus.Failed;
                job.Error       = ex.InnerException?.Message ?? ex.Message;
                job.StatusMessage = $"Failed after {FormatDuration(job.Duration.Value)}: {job.Error}";
                _log.LogError(ex, "SKU Max build failed for {Country} {Year}-{Month}", country, year, month);
            }
            finally
            {
                cts.Dispose();
                Notify(job);
            }
        });

        return job;
    }

    /// <summary>
    /// Cancel an in-flight build. Returns true if a Running job was signalled.
    /// SQL Server may take a few seconds to actually roll back the in-flight
    /// transaction even after the connection is closed — we update
    /// StatusMessage immediately so the live banner shows "Cancelling…"
    /// instead of leaving the user wondering whether the click registered.
    /// </summary>
    public bool Cancel(string country, int year, int month)
    {
        var j = Get(country, year, month);
        if (j is null || j.Status != SkuMaxBuildStatus.Running) return false;
        // Immediate UX feedback — flips the banner to "Cancelling…" before
        // we wait for SQL to actually unwind.
        j.StatusMessage = "Cancelling…";
        Notify(j);
        try { j.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* already finished — race; harmless */ }
        return true;
    }

    /// <summary>
    /// Forget a finished job (so the UI banner clears). Running jobs are not
    /// removed — caller must Cancel + wait first.
    /// </summary>
    public bool Clear(string country, int year, int month)
    {
        var key = Key(country, year, month);
        if (_jobs.TryGetValue(key, out var j) && j.Status == SkuMaxBuildStatus.Running) return false;
        return _jobs.TryRemove(key, out _);
    }

    /// <summary>Drop completed/failed/cancelled jobs older than the retention window.</summary>
    private void SweepCompleted()
    {
        var cutoff = DateTime.Now.AddMinutes(-CompletedRetentionMinutes);
        foreach (var kv in _jobs)
        {
            var j = kv.Value;
            if (j.Status != SkuMaxBuildStatus.Running
                && j.CompletedAt.HasValue
                && j.CompletedAt.Value < cutoff)
            {
                _jobs.TryRemove(kv.Key, out _);
            }
        }
    }

    private void Notify(SkuMaxBuildJob job)
    {
        try { JobChanged?.Invoke(this, job); }
        catch { /* subscriber threw — never let it kill the manager */ }
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        if (t.TotalSeconds >= 1) return $"{t.TotalSeconds:0.0}s";
        return $"{t.TotalMilliseconds:0}ms";
    }
}

public enum SkuMaxBuildStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Snapshot of a Build SKU Max job. Mutable on the manager side as the job
/// progresses; UI subscribers receive the same instance via JobChanged so
/// reading the live properties is safe (single-writer manager pattern).
/// </summary>
public class SkuMaxBuildJob
{
    public Guid     JobId { get; init; }
    public string   Country { get; init; } = "";
    public int      Year { get; init; }
    public int      Month { get; init; }
    /// <summary>Subset of items the build covers (All / LpmOnly / NonLpmOnly).</summary>
    public LpmSimSkuMaxScope Scope { get; init; } = LpmSimSkuMaxScope.All;
    public string   StartedBy { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
    public SkuMaxBuildStatus Status { get; set; } = SkuMaxBuildStatus.Running;
    public string?  StatusMessage { get; set; }
    public string?  Error { get; set; }
    public long     RowCount { get; set; }
    public CancellationTokenSource Cts { get; init; } = null!;
    public Task?    RunningTask { get; set; }
}
