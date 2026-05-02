namespace LpmSim.Core.Entities;

public class LpmSalesTurns
{
    public string StoreID { get; set; } = "";
    public int DivCode { get; set; }
    public int Year1 { get; set; }
    public int Month1 { get; set; }
    public decimal? SoldQty { get; set; }
    public decimal? TurnsQty { get; set; }
    public DateTime CreateTS { get; set; }
}
