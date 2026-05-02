namespace LpmSim.Core.Entities;

public class LpmDivMax
{
    public string StoreID { get; set; } = "";
    public int DivCode { get; set; }
    public int MaxQty { get; set; }
    public DateTime CreateTS { get; set; }
    public DateTime UpdatedTS { get; set; }
    public string UserID { get; set; } = "";
}
