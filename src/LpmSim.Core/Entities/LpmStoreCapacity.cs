namespace LpmSim.Core.Entities;

/// <summary>
/// Per-(Country, StoreID) EOM capacity. Editable on Planning Config →
/// Stores Capacity EOM and bulk-uploadable via Data Uploads → Stores Capacity.
/// </summary>
public class LpmStoreCapacity
{
    public string Country     { get; set; } = "";
    public string StoreID     { get; set; } = "";
    public int    EomCapacity { get; set; }
    public bool   IsActive    { get; set; } = true;
    public DateTime  CreateTS  { get; set; }
    public string?   CreatedBy { get; set; }
    public DateTime? UpdatedTS { get; set; }
    public string?   UpdatedBy { get; set; }
}
