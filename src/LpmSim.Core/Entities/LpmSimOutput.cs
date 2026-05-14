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

    /// <summary>
    /// 1.14.18 — Item-level season from <c>whboxitems.Season</c>: <c>'S'</c>
    /// (Summer) or <c>'W'</c> (Winter). A mixed box can contain rows of
    /// different Seasons; SIM reads Season per item, not per box. NULL on
    /// pre-1.14.18 rows that haven't been backfilled by migration 044.
    /// </summary>
    public string? Season { get; set; }

    /// <summary>
    /// 1.14.18 — Total qty in the source box (sum across all items in the
    /// box from whboxitems). Same value on every row of the same <c>BoxNo</c>
    /// within a batch. Lets SSMS queries on LPMSIM_Output compute usability
    /// without joining whboxitems. NULL on pre-1.14.18 rows that haven't
    /// been backfilled by migration 044.
    /// </summary>
    public long? BoxQty { get; set; }

    /// <summary>
    /// 1.14.18 — Source qty of THIS item in the box (from whboxitems.Qty).
    /// Distinct from <see cref="Qty"/>, which is the allocated qty for THIS
    /// store. NULL on pre-1.14.18 rows that haven't been backfilled by
    /// migration 044.
    /// </summary>
    public int? BoxItemQty { get; set; }

    /// <summary>
    /// 1.14.18 — Per-box usability: <c>SUM(LPMSIM_Output.Qty for this BoxNo)
    /// / BoxQty × 100</c>, rounded to 2 dp. Same value on every row of the
    /// same <c>BoxNo</c> within a batch (the box is shipped as a unit, so
    /// usability is a box-level metric). NULL on pre-1.14.18 rows that
    /// haven't been backfilled by migration 044.
    /// </summary>
    public decimal? UsabilityPct { get; set; }

    /// <summary>
    /// 1.14.18 — Item's division code, looked up via <c>upc_subclass ×
    /// subclassmaster × Division</c>. NULL on pre-1.14.18 rows that haven't
    /// been backfilled by migration 044, or items that have no row in
    /// <c>upc_subclass</c>.
    /// </summary>
    public int? DivCode { get; set; }

    /// <summary>
    /// 1.14.18 — SKU Max used by the allocator at allocation time for this
    /// (Store, Item) tuple, from <c>dbo.LPM_SimItemSkuMax</c>. After
    /// migration 045, SKU Max is keyed by (Store, Item) only — no period,
    /// no season. NULL on rows allocated before the SKU Max snapshot
    /// existed, or for items not present in the snapshot.
    /// </summary>
    public int? SKUMax { get; set; }
}
