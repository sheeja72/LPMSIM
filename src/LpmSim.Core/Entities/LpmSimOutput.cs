namespace LpmSim.Core.Entities;

public class LpmSimOutput
{
    public long Id { get; set; }
    public long LPMBatchNo { get; set; }
    public string BoxNo { get; set; } = "";
    /// <summary>
    /// Pallet number this box belongs to, captured from <c>whboxitems.PalletNo</c>
    /// at allocation time (1.14.12). Lets direct SSMS queries on LPMSIM_Output
    /// see pallet info without joining the source table. NULL for historical
    /// rows that pre-date this column.
    /// </summary>
    public string? PalletNo { get; set; }
    public DateTime? LPMDt { get; set; }
    public string Itemcode { get; set; } = "";
    public int Qty { get; set; }
    public string StoreID { get; set; } = "";
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";

    /// <summary>P1 / P1_RR / P2 / P2_RR — phase that produced this allocation line.</summary>
    public string Phase { get; set; } = "P1";

    /// <summary>True when the allocation came from a round-robin pass (may exceed SKUMax / TargetEOM).</summary>
    public bool IsRoundRobin { get; set; }

    /// <summary>
    /// True when this row was produced by Phase 1b / 2b OVERRIDE round-robin
    /// (box usability ≥ Box % threshold, bypassing SKU Max and Merch Need
    /// caps to fill the box toward 100%). Used by reports to surface
    /// "Override Qty" separately from regular SIM Qty.
    /// </summary>
    public bool IsOverride { get; set; }

    /// <summary>
    /// Production day (1..N) assigned by Production Schedule. NULL when the
    /// box hasn't been scheduled yet, or was deferred because it didn't fit
    /// in the planning window. All rows for the same BoxNo always share the
    /// same Day (a box is produced as a unit).
    /// </summary>
    public int? Day { get; set; }
}
