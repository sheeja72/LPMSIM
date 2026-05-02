namespace LpmSim.Core.Entities;

/// <summary>
/// End-of-run snapshot of per-Box totals. Lets the planning team see at a
/// glance how each source box was consumed by the SIM — total qty, normal
/// allocation, round-robin allocation, usability % and the qty that stayed
/// unallocated (capped by SKUMax / EOM).
/// </summary>
public class LpmSimBoxBalance
{
    public long LPMBatchNo { get; set; }
    public string BoxNo { get; set; } = "";
    public string BoxKind { get; set; } = "";   // 'LPM' or 'Non-LPM'
    public DateTime? LPMDt { get; set; }
    public long BoxQty { get; set; }
    public int P1_NormalAlloc { get; set; }
    public int P1_RR { get; set; }
    public int P2_NormalAlloc { get; set; }
    public int P2_RR { get; set; }
    public long TotalAlloc { get; set; }
    public long LeftOverQty { get; set; }
    public decimal UsabilityPct { get; set; }   // ROUND(SimQty*100/BoxQty, 1)
    public DateTime CreateTS { get; set; }
}
