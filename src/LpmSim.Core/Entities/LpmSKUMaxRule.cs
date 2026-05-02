namespace LpmSim.Core.Entities;

public class LpmSKUMaxRule
{
    public int RuleId { get; set; }
    public string Country { get; set; } = "";
    public int DivCode { get; set; }
    public string GroupCode { get; set; } = "";
    public int WHStockFrom { get; set; }
    public int WHStockTo { get; set; }
    public int SKUMax { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }

    public LpmVolumeGroup? Group { get; set; }
    public Division? Division { get; set; }
}
