namespace LpmSim.Core.Entities;

public class Division
{
    public int DivCode { get; set; }
    public string? Name { get; set; }

    /// <summary>
    /// 1.14.55 — Soft-delete flag. Inactive divisions:
    /// <list type="bullet">
    ///   <item>Disappear from every admin dropdown (Planned Inputs, SKU Max
    ///         Rules, Volume Groups, Store/Div Access, etc.).</item>
    ///   <item>Stop participating in EOM Generate / SIM Generate readiness
    ///         checks and the (Store × Division) iteration grid.</item>
    ///   <item>Are rejected by config uploads (Planned, SkuMax Rules, Volume
    ///         Groups, WH Stock) — uploading a row for an inactive division
    ///         fails validation.</item>
    /// </list>
    /// Historical rows in LPM_SalesTurns / LPM_EOM_Output / LPM_SimItemSkuMax
    /// for inactive divisions are preserved as-is. Defaults to <c>true</c>
    /// (active) so existing rows behave the same after migration 055.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
