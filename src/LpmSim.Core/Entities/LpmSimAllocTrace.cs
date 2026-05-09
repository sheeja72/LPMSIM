namespace LpmSim.Core.Entities;

/// <summary>
/// Per-(Box × Item × Store) decision trace from a SIM run. One row for every
/// allocation attempt the engine considered — used to diagnose why eligible
/// boxes were under-utilised.
/// </summary>
public class LpmSimAllocTrace
{
    public long Id { get; set; }
    public long LPMBatchNo { get; set; }
    public string BoxNo { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public int? DivCode { get; set; }
    public string? StoreID { get; set; }

    public int? SKUMax { get; set; }
    public int? SOH_Item { get; set; }
    public int? SkuBalance { get; set; }
    public decimal? TargetEOM { get; set; }
    public int? DivSOH { get; set; }
    public decimal? AlreadyAllocated { get; set; }
    public decimal? TargetRemain { get; set; }

    public int LineQty { get; set; }
    public int Take { get; set; }

    /// <summary>ALLOC | ALLOC_RR | SKIP_SKUMAX | SKIP_TARGET | SKIP_NO_DIV | SKIP_NO_EOM</summary>
    public string Decision { get; set; } = "";

    /// <summary>P1 / P1_RR / P2 / P2_RR — which phase made the decision.</summary>
    public string Phase { get; set; } = "P1";

    /// <summary>True when this trace row was an override allocation
    /// (Phase 1b/2b RR with usability ≥ Box %, bypassing SKU Max + Merch Need caps).</summary>
    public bool IsOverride { get; set; }

    public DateTime CreateTS { get; set; }
}
