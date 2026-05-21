namespace LpmSim.Core.Entities;

/// <summary>
/// 1.14.102 — Per-country lock for Build SKU Max.
///
/// <para>
/// Row presence = locked. Insert a row to block Build SKU Max for that
/// country (both the manual <c>SkuMaxBuildJobManager.Start</c> path AND
/// the nightly <c>SkuMaxBuildScheduler</c> auto path). DELETE the row to
/// unlock. There is no <c>IsLocked</c> flag — the simplest possible
/// representation, matching the original ask: "I will mention the country
/// to lock; if locked, don't run Build SKU Max for that country".
/// </para>
///
/// <para>
/// Scope is per-country only (no Year/Month dimension) — locking UAE
/// blocks every period's build, which is what we want for the stated
/// use case (pausing all UAE builds while the planner investigates data).
/// </para>
///
/// <para>
/// See <c>db/060_lpm_skumaxlock.sql</c> for the table definition.
/// </para>
/// </summary>
public class LpmSkuMaxLock
{
    public string   Country  { get; set; } = "";
    public DateTime LockedAt { get; set; }
    public string   LockedBy { get; set; } = "";
    public string?  Reason   { get; set; }
}
