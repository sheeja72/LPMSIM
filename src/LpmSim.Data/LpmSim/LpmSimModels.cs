namespace LpmSim.Data.LpmSim;

public record LpmSimReadiness(
    bool EomReady,        string EomDetail,
    bool BoxesReady,      string BoxesDetail,
    bool LocStockReady,   string LocStockDetail,
    bool ExistingDraft,   bool ExistingApproved,
    long? CurrentBatchNo, string? CurrentStatus, DateTime? CurrentApprovedTS, string? CurrentApprovedBy)
{
    // Generate is allowed even when an Approved batch exists for the period —
    // the new run produces a fresh Draft alongside, leaving the Approved one
    // intact (see LpmSimGenerator.GenerateAsync). Only blocks: missing inputs.
    public bool CanGenerate => EomReady && BoxesReady;
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

    /// <summary>RunDate stored on the batch (the planning period the run targets).</summary>
    public DateTime? CurrentRunDate { get; init; }

    /// <summary>CreateTS — when the batch was actually generated.</summary>
    public DateTime? CurrentCreateTS { get; init; }

    /// <summary>User who created the batch (CreatedBy on LPMSIM_Batch).</summary>
    public string?   CurrentCreatedBy { get; init; }
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
    /// Default = <see cref="LpmSimFillStrategy.EqualPerStore"/> (Ranking + RR:
    /// 1 unit per store per cycle in PriorityRank ASC, WtAvgSoldQty DESC
    /// order). Set to <see cref="LpmSimFillStrategy.EqualFillRate"/> for the
    /// proportional-balancing behaviour (equalises Division-level FillRate%
    /// across stores).
    /// </summary>
    public LpmSimFillStrategy FillStrategy { get; set; } = LpmSimFillStrategy.EqualPerStore;

    /// <summary>
    /// When true, the allocator IGNORES the Merch Need (Week) cap and
    /// sizes allocations against SKU Max only. Use this when SIM should
    /// fill stores up to their SKU ceiling regardless of weekly merch
    /// targets — typically a "stress test" / "max push" run.
    ///
    /// What changes:
    ///   • Per-(Store, Div) cap = SkuMax − SOH − cumItem only — div balance
    ///     no longer blocks placement.
    ///   • EqualFillRate's "fill rate" denominator switches from
    ///     MerchNeedWeek to SkuMax (so stores with low SKU consumption are
    ///     still preferred first); EqualPerStore is unchanged.
    ///   • The Box% override behaviour is unchanged — it still bypasses
    ///     SKU Max when usability % is met.
    /// </summary>
    public bool IgnoreMerchNeed { get; set; } = false;

    /// <summary>
    /// When true, the eligibility filter does NOT apply
    /// <c>ShopEligible &lt;&gt; 'E'</c>, so already-shopped boxes
    /// (ShopEligible='E', which the business calls "Non-Purchased") are
    /// also pulled into the allocation pool. Default is false (legacy
    /// behaviour — only ShopEligible &lt;&gt; 'E' boxes are allocated).
    /// Surfaced in the UI as the "Include Non-Purchased Boxes" checkbox.
    /// </summary>
    public bool IncludePurchasedBoxes { get; set; } = false;

    /// <summary>
    /// Pallet categories the eligibility filter accepts. Default is
    /// <c>["ELIGIBLE"]</c> (legacy behaviour — only ELIGIBLE pallets enter
    /// SIM). The user can pick multiple categories from the
    /// "Pallet Categories" multi-select on SIM Generate. An empty list
    /// means "no pallet-category filter" (every category included).
    /// </summary>
    public List<string> PalletCategories { get; set; } = new() { "ELIGIBLE" };

    /// <summary>
    /// Specific LPM months the planner has selected — month-start
    /// <see cref="DateTime"/> values (1st of each month). Applies ONLY to
    /// the LPM box source (Non-LPM boxes have <c>LPMDt IS NULL</c> by
    /// definition and aren't affected). Empty list = legacy behaviour
    /// ("all months with LPMDt &lt; first of next month after RunDate").
    /// </summary>
    public List<DateTime> LpmMonths { get; set; } = new();

    /// <summary>
    /// Logical week of the run month (1..4) the planner is generating SIM
    /// for. Drives the per-Store × Div weekly cap — the SQL that loads
    /// <c>LPM_EOM_Output</c> picks <c>MerchNeedWeek{N}</c> based on this
    /// value (not the legacy <c>MerchNeedWeek</c> column). Stamped onto
    /// the resulting <c>LPM_SimBatch.WeekNo</c> so downstream knows which
    /// week the batch was generated for. Default = 1.
    /// </summary>
    public byte WeekNo { get; set; } = 1;
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

    /// <summary>
    /// How many <c>LPM_SimItemSkuMax</c> rows had their SKU Max set to 0 at
    /// the start of this run because their <c>(Store, Div)</c> is currently
    /// flagged <c>IsActive = 0</c> in <c>LPM_StoreDivAccess</c>. Lets the
    /// planner verify a deactivation took effect without rebuilding SKU Max.
    /// 0 = snapshot already in sync; nothing to do.
    /// </summary>
    public long DeactivationsSynced { get; set; }
}

/// <summary>
/// Status of the per-(Country, Year, Month) <c>LPM_SimItemSkuMax</c> snapshot.
/// Returned by <c>LpmSimGenerator.GetLastSkuMaxBuildAsync</c> and consumed by
/// the SIM Generate UI so the user can see whether a fresh SKU Max snapshot
/// exists for the current run period before kicking off a SIM Generate.
/// </summary>
/// <summary>
/// Which subset of <c>racks.dbo.whboxitems</c> rows the SKU Max build covers.
/// "Each process shouldn't delete other" — the build only touches rows for
/// items found in its scope. Items in the OTHER scope keep their existing
/// SKU Max rows.
/// </summary>
public enum LpmSimSkuMaxScope
{
    /// <summary>All boxes — both LPM (LPMDt set) and Non-LPM (LPMDt NULL).</summary>
    All        = 0,
    /// <summary>Only items appearing in LPM boxes (LPMDt IS NOT NULL).</summary>
    LpmOnly    = 1,
    /// <summary>Only items appearing in Non-LPM boxes (LPMDt IS NULL).</summary>
    NonLpmOnly = 2,
}

/// <summary>
/// Pre-build counts surfaced in the confirmation dialog before SKU Max is
/// kicked off. Lets the planner say "yes, proceed" with a clear view of
/// how many items will be processed.
/// </summary>
public sealed record SkuMaxBuildPreview(
    long ItemsInScope,        // distinct itemcodes that will get a SKU Max row
    long ItemsInWhBoxes,      // distinct itemcodes in racks.dbo.whboxitems for the scope
    long DroppedNoMaster,     // = ItemsInWhBoxes - ItemsInScope (items missing from upc_subclass)
    long ExistingRowsInScope, // current LPM_SimItemSkuMax rows that the build will replace
    long ExistingRowsKept     // rows for items NOT in scope — preserved untouched
);

/// <summary>
/// Distinct-item counts per scope, fetched once and cached on the page so
/// the planner sees "LPM 10K · Non-LPM 40K · All 48K" next to the Scope
/// dropdown without an SQL roundtrip on every dropdown change.
/// </summary>
public sealed record SkuMaxScopeCounts(long All, long Lpm, long NonLpm);

/// <summary>
/// Whitelist of fields the planner can pick on the Custom Report tab. The
/// SQL builder maps each enum value to a vetted SQL fragment in
/// <c>LpmSimReports.CustomReportFieldDefs</c> — no raw column names ever
/// come from the UI, so the dynamic SQL stays injection-safe by
/// construction.
/// </summary>
public enum CustomReportField
{
    // Dimensions (groupable identifiers)
    StoreID,
    StoreName,
    Division,
    DivCode,
    BoxNo,
    Itemcode,
    Brand,        // racks.dbo.whboxitems.Brand
    TrnDate,      // racks.dbo.whboxitems.TrnDate
    LPMDt,        // racks.dbo.whboxitems.LPMDt — actual date (NULL = Non-LPM)
    BoxKind,      // 'LPM' / 'Non-LPM' derived from LPMDt
    Phase,        // P1Normal / P1RR / P2Normal / P2RR

    // Measures (numbers — aggregated as SUM by default)
    BoxItemQty,    // qty of this item in this box (whboxitems.Qty per Box×Item)
    BoxQty,        // total qty across all items in the box
    SIMQty,        // allocated qty per LPMSIM_Output row
    RRQty,         // SIM qty where IsRoundRobin = 1
    OverrideQty,   // SIM qty where IsOverride   = 1
    SOH,           // per-(Store, Item) SOH from LocStock
    SKUMax,        // per-(Store, Item) SKU Max from LPM_SimItemSkuMax
    DivSOH,        // per-(Store, Div) SOH from LocStock
    DivEOMBalance, // LPM_EOM_Output.TargetEOM − DivSOH(Store,Div)
    DivMerchNeed,  // LPM_EOM_Output.MerchNeedWeek
    /// <summary>Per-(Store, Div) priority rank from <c>LPM_EOM_Output.PriorityRank</c>.</summary>
    PriorityRank,
    /// <summary>Per-(Store, Div) weighted-average sold qty from <c>LPM_EOM_Output.WtAvgSoldQty</c>.</summary>
    WtAvgSoldQty,
}

/// <summary>
/// What the planner picks on the Custom Report tab — multi-select Group By
/// (defines the row grain) and multi-select Columns (what to display).
/// The SQL builder validates / normalises (e.g. dimensions in Columns are
/// auto-included in Group By so the SQL stays valid).
/// </summary>
public sealed record CustomReportSpec(
    List<CustomReportField> GroupBy,
    List<CustomReportField> Columns);

/// <summary>One column's metadata for the result table — drives the UI's dynamic header + cell rendering.</summary>
public sealed record CustomReportColumnInfo(
    CustomReportField Field,
    string Key,           // SQL alias / dictionary key
    string DisplayName,   // header label
    bool IsNumeric);      // true → right-align + N0 format

/// <summary>
/// Result of <c>RunCustomReportAsync</c>: column metadata in the order they
/// should be rendered, plus the rows as plain dictionaries keyed by the
/// column's <see cref="CustomReportColumnInfo.Key"/>.
/// </summary>
public sealed record CustomReportResult(
    List<CustomReportColumnInfo> Columns,
    List<Dictionary<string, object?>> Rows);

public class LpmSimSkuMaxBuildStatus
{
    /// <summary>Latest <c>CreateTS</c> across all rows for the run period. Null when no rows exist.</summary>
    public DateTime? LastBuildTS { get; set; }

    /// <summary>User who built the snapshot (last writer). Null when no rows exist or pre-migration-025.</summary>
    public string?   LastBuildBy { get; set; }

    /// <summary>Number of rows present for the run period.</summary>
    public long      RowCount    { get; set; }

    /// <summary>True when LastBuildTS is non-null and falls on the current calendar day (server-local time).</summary>
    public bool      IsFreshToday { get; set; }

    /// <summary>
    /// Wall-clock duration of the most recent build, persisted in
    /// <c>LPM_SimItemSkuMaxBuild</c>. Survives server restarts so the UI
    /// can show "built in 3m 42s" days later. Null when no build header
    /// row exists for the period (e.g. legacy snapshot built before
    /// migration 032 was applied).
    /// </summary>
    public TimeSpan? LastBuildDuration { get; set; }
}

/// <summary>
/// Timestamps for the inputs that drive SKU Max build. Surfaced in the UI so
/// the user can spot when input data has changed since their last build.
/// </summary>
public class LpmSimInputFreshness
{
    /// <summary>Most recent row in <c>racks.dbo.whboxitems</c> matching the eligibility filter.</summary>
    public DateTime? LastWHBoxLoad { get; set; }

    /// <summary>Latest <c>UpdateTS</c> on any active <c>LPM_SKUMaxRule</c> row for the country.</summary>
    public DateTime? LastSkuMaxRuleChange { get; set; }

    /// <summary>Latest change to a Volume Group assignment (any approved <c>LPM_EOM_Output</c> row for the period).</summary>
    public DateTime? LastVolumeGroupChange { get; set; }
}

/// <summary>
/// Thrown by <c>GenerateAsync</c> when the per-(Country, Year, Month) SKU Max
/// snapshot is missing or wasn't built today. Caller (the UI) should catch
/// this and prompt the user to click "Build SKU Max" before retrying.
/// </summary>
public sealed class SkuMaxStaleException : InvalidOperationException
{
    public LpmSimSkuMaxBuildStatus Status { get; }

    public SkuMaxStaleException(string message, LpmSimSkuMaxBuildStatus status)
        : base(message)
    {
        Status = status;
    }
}
