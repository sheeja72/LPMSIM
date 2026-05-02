namespace LpmSim.Core.Entities;

/// <summary>
/// End-of-run snapshot of per-(Store, Item) running totals. Lets the planning
/// team verify that SKU Max was not crossed by normal allocations and quantify
/// what was added by round-robin.
/// </summary>
public class LpmSimStoreItemBalance
{
    public long LPMBatchNo { get; set; }
    public string StoreID { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public int? DivCode { get; set; }
    public int? SKUMax { get; set; }
    public int SOH_Item { get; set; }
    public int P1_NormalAlloc { get; set; }
    public int P1_RR { get; set; }
    public int P2_NormalAlloc { get; set; }
    public int P2_RR { get; set; }
    public int TotalAlloc { get; set; }
    public int? SkuBalanceRemaining { get; set; }
    public DateTime CreateTS { get; set; }
}
