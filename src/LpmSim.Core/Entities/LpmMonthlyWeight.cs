namespace LpmSim.Core.Entities;

public class LpmMonthlyWeight
{
    public string Country { get; set; } = "";
    public int RunYear { get; set; }
    public int RunMonth { get; set; }
    public int PeriodSeq { get; set; }
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public decimal WeightPct { get; set; }
    public DateTime CreateTS { get; set; }
}
