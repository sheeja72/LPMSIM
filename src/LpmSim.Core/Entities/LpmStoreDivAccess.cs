namespace LpmSim.Core.Entities;

/// <summary>
/// Per-Country, per-Store, per-Division activation flag for SIM allocation.
/// A row with <see cref="IsActive"/> = false means that division's items
/// must NOT be allocated to that store. EOM Generate enforces this by
/// setting <c>LPM_EOM_Output.SKUMax = 0</c> for the (Store, Div) row, so
/// SIM Generate's SKU Max balance is zero and every allocation cycle
/// skips the store for that division.
///
/// Default behaviour (no row in this table) is ACTIVE — only explicit
/// deactivations need rows here.
/// </summary>
public class LpmStoreDivAccess
{
    public string Country { get; set; } = "";
    public string StoreID { get; set; } = "";
    public int DivCode { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedTS { get; set; }
    public string? UpdatedBy { get; set; }
}
