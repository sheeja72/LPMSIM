namespace LpmSim.Core.Entities;

public class LpmSimBatch
{
    public long LPMBatchNo { get; set; }
    public string Country { get; set; } = "";
    public int RunYear { get; set; }
    public int RunMonth { get; set; }
    public DateTime RunDate { get; set; }
    public string Status { get; set; } = "Draft"; // 'Draft' or 'Approved'
    public int BoxesProcessed { get; set; }
    public int LinesGenerated { get; set; }
    public long TotalQty { get; set; }
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime? ApprovedTS { get; set; }
    public string? ApprovedBy { get; set; }

    /// <summary>Snapshot of the Box-Source filter used at Generate time, e.g. "LPM", "Non-LPM", "LPM+Non-LPM".</summary>
    public string? Sources { get; set; }
    /// <summary>Snapshot of the Season filter used at Generate time, e.g. "Summer", "Winter", "Summer+Winter".</summary>
    public string? Seasons { get; set; }
    /// <summary>Snapshot of the Phase-2 RR threshold (%) used at Generate time.</summary>
    public int? OverrideUsabilityPct { get; set; }

    /// <summary>Snapshot of the selected Warehouses (comma-separated). Empty/null = all.</summary>
    public string? Warehouses { get; set; }

    /// <summary>
    /// Snapshot of the Phase-1a/2a fill strategy chosen at Generate time:
    /// <c>EqualPerStore</c> (default) or <c>EqualFillRate</c>. Stored as the
    /// enum name so SSMS / query layers can read it without a join.
    /// </summary>
    public string? FillStrategy { get; set; }

    /// <summary>
    /// Logical week of the run month this batch was generated for (1..4).
    /// Drives the allocator's per-Store × Div weekly cap — the SQL that
    /// loads <c>LPM_EOM_Output</c> picks <c>MerchNeedWeek{N}</c> based on
    /// this value. NULL on pre-migration batches; the UI treats NULL as
    /// Week 1 for display purposes (no re-allocation).
    /// </summary>
    public byte? WeekNo { get; set; }
}
