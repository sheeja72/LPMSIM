namespace LpmSim.Core.Entities;

public class LpmVolumeGroup
{
    /// <summary>Territory the volume group applies to. Country is part of the PK.</summary>
    public string Country { get; set; } = "";
    public string GroupCode { get; set; } = "";
    public string GroupName { get; set; } = "";
    public int SortOrder { get; set; }
    public decimal SharePct { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
}
