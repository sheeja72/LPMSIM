namespace LpmSim.Core.Entities;

/// <summary>
/// Per-(SIM batch) production schedule header. Records the inputs the
/// planner used + summary of how many boxes/qty got scheduled vs deferred.
///
/// One row per SIM batch — added on first Generate Schedule, deleted (via
/// FK CASCADE) when the parent SIM batch is removed. Production Schedule
/// has its own Approve / Delete workflow so a planner can revise the
/// schedule without touching the parent SIM batch.
/// </summary>
public class LpmSimProductionSchedule
{
    /// <summary>Parent SIM batch — also the primary key (one schedule per batch).</summary>
    public long LPMBatchNo { get; set; }

    /// <summary>Daily production target qty (units) the planner entered.</summary>
    public int DailyTargetQty { get; set; }

    /// <summary>Days in the production week (typically 6 or 7).</summary>
    public int DaysInWeek { get; set; }

    /// <summary>
    /// Min Usability % filter — boxes below this threshold are dropped from
    /// the schedule (Day stays NULL) and stay available for next week's
    /// SIM run.
    /// </summary>
    public decimal MinUsabilityPct { get; set; }

    /// <summary>Draft / Approved.</summary>
    public string Status { get; set; } = "Draft";

    /// <summary>Number of distinct eligible boxes (Usability% ≥ Min) at generation.</summary>
    public int EligibleBoxes { get; set; }

    /// <summary>Sum of allocated qty across eligible boxes.</summary>
    public long EligibleQty { get; set; }

    /// <summary>How many of the eligible boxes ended up assigned to a Day (1..N).</summary>
    public int ScheduledBoxes { get; set; }

    /// <summary>Sum of allocated qty for scheduled boxes.</summary>
    public long ScheduledQty { get; set; }

    /// <summary>Eligible boxes that didn't fit in the planning window (Day = NULL).</summary>
    public int DeferredBoxes { get; set; }

    /// <summary>Sum of allocated qty for deferred boxes.</summary>
    public long DeferredQty { get; set; }

    public DateTime CreateTS { get; set; }
    public string   CreatedBy { get; set; } = "";
    public DateTime? ApprovedTS { get; set; }
    public string?   ApprovedBy { get; set; }

    /// <summary>
    /// Comma-separated list of warehouse codes the planner flagged as
    /// priority — boxes from these warehouses are dispatched first (within
    /// LPM/Non-LPM tiers). NULL or empty = no priority, all equal.
    /// </summary>
    public string? PriorityWarehouses { get; set; }
}
