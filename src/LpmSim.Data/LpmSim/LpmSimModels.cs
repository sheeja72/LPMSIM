namespace LpmSim.Data.LpmSim;

public record LpmSimReadiness(
    bool EomReady,        string EomDetail,
    bool BoxesReady,      string BoxesDetail,
    bool LocStockReady,   string LocStockDetail,
    bool ExistingDraft,   bool ExistingApproved,
    long? CurrentBatchNo, string? CurrentStatus, DateTime? CurrentApprovedTS, string? CurrentApprovedBy)
{
    public bool CanGenerate => EomReady && BoxesReady && !ExistingApproved;
    public bool CanApprove  => ExistingDraft && CurrentBatchNo.HasValue;
    public bool CanDelete   => CurrentBatchNo.HasValue;

    // Aggregate metrics for the readiness cards.
    public int  EomRows            { get; init; }
    public long TotalEom           { get; init; }
    public long TotalBalanceToFill { get; init; }
    public int  EligibleBoxes      { get; init; }
    public int  EligibleLines      { get; init; }
    public long EligibleQty        { get; init; }
    public int  LocStockRows       { get; init; }
    public long TotalSoh           { get; init; }

    // Per-segment breakdown for the Boxes ribbon (LPM/Non-LPM × Summer/Winter).
    public BoxSegmentCounts? BoxSegments { get; init; }

    // Filter snapshot from the existing batch (what produced it), so the UI can
    // show the user that the current checkboxes don't necessarily match.
    public string? CurrentBatchSources              { get; init; }
    public string? CurrentBatchSeasons              { get; init; }
    public int?    CurrentBatchOverrideUsabilityPct { get; init; }
    public string? CurrentBatchWarehouses           { get; init; }
    public string? CurrentBatchFillStrategy         { get; init; }
}

/// <summary>Counts and quantities split across LPM/Non-LPM × Summer/Winter.</summary>
public record BoxSegmentCounts(
    int LpmSummerBoxes,    long LpmSummerQty,
    int NonLpmSummerBoxes, long NonLpmSummerQty,
    int LpmWinterBoxes,    long LpmWinterQty,
    int NonLpmWinterBoxes, long NonLpmWinterQty)
{
    public int  TotalSummerBoxes => LpmSummerBoxes + NonLpmSummerBoxes;
    public long TotalSummerQty   => LpmSummerQty   + NonLpmSummerQty;
    public int  TotalWinterBoxes => LpmWinterBoxes + NonLpmWinterBoxes;
    public long TotalWinterQty   => LpmWinterQty   + NonLpmWinterQty;
    public int  TotalBoxes       => TotalSummerBoxes + TotalWinterBoxes;
    public long TotalQty         => TotalSummerQty   + TotalWinterQty;
}

[Flags]
public enum LpmSimSourceFlags
{
    None     = 0,
    LpmBoxes    = 1,  // boxes whose LPMDt is current month or earlier
    NonLpmBoxes = 2,  // boxes whose LPMDt is NULL
    Both        = LpmBoxes | NonLpmBoxes,
}

[Flags]
public enum LpmSimSeasonFlags
{
    None   = 0,
    Summer = 1,  // pallet types where Season is NOT 'W'
    Winter = 2,  // pallet types where Season = 'W'
    Both   = Summer | Winter,
}

/// <summary>
/// How allocations are spread across stores within a Division for each
/// box-line. Phase 1b/2b round-robin always uses cycle-based fill — this
/// switch only affects Phase 1a / Phase 2a (the "normal" pass).
/// </summary>
public enum LpmSimFillStrategy
{
    /// <summary>
    /// 1 unit per store per cycle, in PriorityRank ASC order. Every store
    /// gets the same per-line qty until SKU/EOM caps stop them. Tends to
    /// produce uneven Division-level FillRate% when stores have very
    /// different EomBalance — a small store fills to 100% on a few items,
    /// while a big store has lots of headroom left.
    /// </summary>
    EqualPerStore = 0,

    /// <summary>
    /// Each cycle gives 1 unit to the store with the LOWEST current
    /// FillRate% (cumDiv / EomBalance). Tiebreak: PriorityRank ASC. This
    /// pulls every store toward the same Division-level FillRate% — useful
    /// when planning wants comparable shelf-presence across stores rather
    /// than equal shipment qty.
    /// </summary>
    EqualFillRate = 1,
}

public class LpmSimGenerateRequest
{
    public string Country { get; set; } = "";
    public int RunYear { get; set; }
    public int RunMonth { get; set; }
    public DateTime RunDate { get; set; }
    public string User { get; set; } = "";

    /// <summary>Which box sources to allocate from. Multi-select.</summary>
    public LpmSimSourceFlags Sources { get; set; } = LpmSimSourceFlags.LpmBoxes;

    /// <summary>Which seasons to include. Multi-select.</summary>
    public LpmSimSeasonFlags Seasons { get; set; } = LpmSimSeasonFlags.Summer;

    /// <summary>
    /// Phase 2 round-robin trigger: any Non-LPM box whose post-normal usability
    /// is &gt;= this % gets pushed to 100% via round-robin (overrides SKU/EOM caps).
    /// 0 disables Phase-2 round-robin entirely.
    /// </summary>
    public int OverrideUsabilityPct { get; set; } = 60;

    /// <summary>
    /// Filter boxes by source warehouse. Empty / null = all warehouses (no filter).
    /// Values match <c>racks.dbo.whboxitems.Warehouse</c> codes (e.g. "JAFZA", "TECHNO").
    /// </summary>
    public List<string> Warehouses { get; set; } = new();

    /// <summary>
    /// When false (the default), only the high-signal trace rows are written:
    /// ALLOC, ALLOC_RR, SKIP_NO_DIV, SKIP_NO_EOM. The noisy SKIP_SKUMAX and
    /// SKIP_TARGET rows (typically 90%+ of trace volume) are dropped — they
    /// can be inferred from <c>LPMSIM_StoreItemBalance</c> and
    /// <c>LPMSIM_StoreDivBalance</c> which are always written.
    /// Turn on for deep "why didn't this store take it?" debugging.
    /// </summary>
    public bool VerboseTrace { get; set; } = false;

    /// <summary>
    /// Phase 1a / Phase 2a fill strategy — see <see cref="LpmSimFillStrategy"/>.
    /// Default = <see cref="LpmSimFillStrategy.EqualPerStore"/> (1 unit per store per cycle).
    /// Set to <see cref="LpmSimFillStrategy.EqualFillRate"/> to equalise Division-level
    /// FillRate% instead.
    /// </summary>
    public LpmSimFillStrategy FillStrategy { get; set; } = LpmSimFillStrategy.EqualPerStore;
}

public class LpmSimGenerateResult
{
    public long LPMBatchNo { get; set; }
    public int BoxesProcessed { get; set; }
    public int LinesGenerated { get; set; }
    public long TotalQty { get; set; }
    public int ItemsWithoutDivision { get; set; }
    public int BoxesSkipped { get; set; }

    // Phase breakdown.
    public int P1NormalLines { get; set; }
    public int P1RrLines     { get; set; }
    public int P2NormalLines { get; set; }
    public int P2RrLines     { get; set; }
    public long P1NormalQty  { get; set; }
    public long P1RrQty      { get; set; }
    public long P2NormalQty  { get; set; }
    public long P2RrQty      { get; set; }

    // Timing breakdown (milliseconds) — surfaces where the run spends its time
    // so the planner can see if the bottleneck is reading inputs, the
    // allocation loop, or the bulk-insert at the end.
    public long MsReadBoxes      { get; set; }
    public long MsReadItemDiv    { get; set; }
    public long MsReadSoh        { get; set; }
    public long MsReadEom        { get; set; }
    public long MsAllocate       { get; set; }
    public long MsPersistOutput  { get; set; }
    public long MsPersistTrace   { get; set; }
    public long MsPersistBalances{ get; set; }
    public long MsTotal          { get; set; }
    public int  TraceRowsWritten { get; set; }
}
