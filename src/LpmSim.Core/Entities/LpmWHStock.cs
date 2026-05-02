namespace LpmSim.Core.Entities;

public class LpmWHStock
{
    public string Country { get; set; } = "";
    public int DivCode { get; set; }
    public int Year1 { get; set; }
    public int Month1 { get; set; }
    public int WHStockQty { get; set; }
    public string? UserID { get; set; }
    public DateTime CreateTS { get; set; }
    public DateTime? UpdatedTS { get; set; }
}
