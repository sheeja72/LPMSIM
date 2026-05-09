namespace LpmSim.Core.Entities;

/// <summary>
/// ADM (Allocation Distribution Model) run header — one row per
/// (Country, RunDate). The 3 planner levers + summary counts are stored
/// here so the result page can show "what produced this run" alongside
/// the per-box detail in <see cref="LpmSimAdmBoxAlloc"/>.
///
/// Status flow: Draft → Approved.
/// </summary>
public class LpmSimAdmRun
{
    public long     AdmRunNo { get; set; }
    public string   Country  { get; set; } = "";
    public DateTime RunDate  { get; set; }
    public int      RunYear  { get; set; }
    public int      RunMonth { get; set; }

    /// <summary>Number of weeks the month is split into (typically 4 → 25 % per week).</summary>
    public int      NumWeeks { get; set; } = 4;
    public string   Status   { get; set; } = "Draft";

    // ── The 3 levers ────────────────────────────────────────────────────────
    /// <summary>
    /// Multiplier on Merch Need (Week) — the weekly target qty per division
    /// = <c>MerchNeedWeek × Week1TargetPct/100</c>. Default 100 means
    /// "ship exactly the weekly merch need". Lower for conservative drops,
    /// higher to push above need.
    /// (Column kept as Week1TargetPct for schema continuity but applies to
    /// all weeks equally — the same multiplier shapes every week's target.)
    /// </summary>
    public decimal Week1TargetPct    { get; set; } = 100.00m;
    /// <summary>Max % of any one week's qty that may come from a single brand (default 25).</summary>
    public decimal BrandCapPct       { get; set; } = 25.00m;
    /// <summary>When true, boxes from brands not yet picked this week get a tiebreak boost within each division.</summary>
    public bool    ApplyVarietyBonus { get; set; } = true;

    // ── Snapshot totals ─────────────────────────────────────────────────────
    public int  TotalEligibleBoxes { get; set; }
    public long TotalEligibleQty   { get; set; }
    public int  ScheduledBoxes     { get; set; }
    public long ScheduledQty       { get; set; }
    public int  DeferredBoxes      { get; set; }
    public long DeferredQty        { get; set; }

    public DateTime  CreateTS   { get; set; } = DateTime.Now;
    public string    CreatedBy  { get; set; } = "";
    public DateTime? ApprovedTS { get; set; }
    public string?   ApprovedBy { get; set; }
}

/// <summary>
/// One row per (AdmRun, Box). <c>Week</c> = 1..N for placed boxes,
/// <c>NULL</c> for deferred boxes (read <c>Reason</c> to find out why).
///
/// Ranking-context fields (<c>DivFillRatePct</c>, <c>DivFillGapPct</c>,
/// <c>BrandQtyAtPick</c>) are snapshotted at allocation time so the UI
/// can answer "why was this box in Week 1?" days later without recomputing.
/// </summary>
public class LpmSimAdmBoxAlloc
{
    public long    Id         { get; set; }
    public long    AdmRunNo   { get; set; }
    public int?    Week       { get; set; }

    public string  BoxNo      { get; set; } = "";
    public string? Warehouse  { get; set; }
    public string? LPMBrand   { get; set; }
    public int     BoxQty     { get; set; }
    public int     DaysInDC   { get; set; }
    public DateTime? LPMDt    { get; set; }

    public int?    DivCode    { get; set; }
    public string? Division   { get; set; }

    public decimal DivFillRatePct  { get; set; }
    public decimal DivFillGapPct   { get; set; }
    public long    BrandQtyAtPick  { get; set; }

    public string  Reason     { get; set; } = "";
    public DateTime CreateTS  { get; set; } = DateTime.Now;
}
