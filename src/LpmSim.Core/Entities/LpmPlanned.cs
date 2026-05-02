namespace LpmSim.Core.Entities;

public class LpmPlanned
{
    public string Country { get; set; } = "";
    public int DivCode { get; set; }
    public int Year1 { get; set; }
    public int Month1 { get; set; }
    public decimal PlannedTurn { get; set; }
    public decimal PlannedSalesQty { get; set; }
    public decimal PlannedEOM { get; set; }
    public string? UserID { get; set; }
    public DateTime CreateTS { get; set; }
    public DateTime? UpdatedTS { get; set; }
}
