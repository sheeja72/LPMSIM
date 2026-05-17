namespace LpmSim.Core.Entities;

public class LpmVolumeGroup
{
    /// <summary>Territory the volume group applies to. Country is part of the PK.</summary>
    public string Country { get; set; } = "";

    /// <summary>Division this group's SharePct applies to. 1.14.39 — Volume
    /// Groups became per-(Country, Division, GroupCode) so each division can
    /// have its own bucket distribution. EOM Generate Step 5 reads
    /// <c>volumeGroupsByDiv[DivCode]</c> instead of one shared list.
    /// Part of the PK.</summary>
    public int DivCode { get; set; }

    public string GroupCode { get; set; } = "";
    public string GroupName { get; set; } = "";
    public int SortOrder { get; set; }
    public decimal SharePct { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
}
