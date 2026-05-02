namespace LpmSim.Core.Entities;

public class LpmSimOutput
{
    public long Id { get; set; }
    public long LPMBatchNo { get; set; }
    public string BoxNo { get; set; } = "";
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
}
