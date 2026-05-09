namespace LpmSim.Core.Entities;

/// <summary>
/// Per-(Country, Year, Month, Store, Item, Season) SKU Max snapshot built
/// fresh at the start of every SIM Generate.
///
/// Drives the SKU Max balance during allocation:
/// <code>skuBalance = SKUMax(Store,Item,Season) − SOH(Store,Item) − cumItem</code>
///
/// Replaces the per-(Store, Div) SKU Max roll-up that used to live on
/// <c>LPM_EOM_Output</c>. Each item's <see cref="WHBoxQty"/> drives the
/// rule lookup against <c>LPM_SKUMaxRule</c>.
/// </summary>
public class LpmSimItemSkuMax
{
    public string Country  { get; set; } = "";
    public int    Year1    { get; set; }
    public int    Month1   { get; set; }
    public string StoreID  { get; set; } = "";
    public string ItemCode { get; set; } = "";

    /// <summary>'S' (Summer) or 'W' (Winter) — from <c>pallettype.Season</c>.</summary>
    public string Season   { get; set; } = "S";

    public int    DivCode      { get; set; }
    public long   WHBoxQty     { get; set; }
    public string? VolumeGroup { get; set; }
    public int    SKUMax       { get; set; }
    public DateTime CreateTS   { get; set; }

    /// <summary>
    /// User who triggered the SKU Max build. Captured from <see cref="ICurrentUser"/>
    /// at build time. Null for rows written before migration 025.
    /// </summary>
    public string? CreatedBy   { get; set; }
}
