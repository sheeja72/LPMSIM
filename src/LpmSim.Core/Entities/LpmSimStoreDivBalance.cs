namespace LpmSim.Core.Entities;

/// <summary>
/// End-of-run snapshot of per-(Store, Division) running totals. Mirrors
/// <see cref="LpmSimStoreItemBalance"/> at the division level (TargetEOM cap).
/// </summary>
public class LpmSimStoreDivBalance
{
    public long LPMBatchNo { get; set; }
    public string StoreID { get; set; } = "";
    public int DivCode { get; set; }
    public decimal? TargetEOM { get; set; }
    public int DivSOH { get; set; }
    public int P1_NormalAlloc { get; set; }
    public int P1_RR { get; set; }
    public int P2_NormalAlloc { get; set; }
    public int P2_RR { get; set; }
    public int TotalAlloc { get; set; }
    public decimal? DivBalanceRemaining { get; set; }

    // Persisted SQL computed columns (migration 018). Read-only on the
    // C# side — SQL keeps them in sync with TargetEOM/DivSOH/TotalAlloc.
    //   EomBalance = TargetEOM − DivSOH                       (qty headroom before this run)
    //   FillRate   = TotalAlloc × 100 / NULLIF(EomBalance, 0) (% of headroom consumed)
    // Nullable because TargetEOM itself is nullable; EomBalance is NULL
    // when TargetEOM is, and FillRate evaluates to 0 in SQL for that case.
    public decimal? EomBalance { get; set; }
    public decimal? FillRate { get; set; }

    public DateTime CreateTS { get; set; }
}
