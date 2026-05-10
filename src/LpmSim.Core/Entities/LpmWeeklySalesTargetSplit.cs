namespace LpmSim.Core.Entities;

/// <summary>
/// Per-(Country, Year, Month, DivCode, WeekNo) split percentage of the
/// monthly Target Sales across the 4 logical weeks of the run month.
/// Drives the per-week Merch Need formula in <c>EomCalculator</c>:
///
/// <code>
/// MerchNeedWeekN = (TargetEOM − SOH) / 4
///                + (TargetSales × SplitPct[N] / 100)        for N = 1..4
/// </code>
///
/// <para>
/// Logical weeks (NOT ISO weeks):
/// <list type="bullet">
///   <item>Week 1 = days 1–7</item>
///   <item>Week 2 = days 8–14</item>
///   <item>Week 3 = days 15–21</item>
///   <item>Week 4 = days 22–end (absorbs days 29–31)</item>
/// </list>
/// </para>
///
/// <para>
/// When no rows exist for a given (Country, Year, Month, DivCode), the
/// EomCalculator falls back to the hard-coded default split
/// <c>20 / 20 / 25 / 35</c>. The "Weekly Sales Target Split" admin page
/// (under Planning Config) writes rows to override the default per
/// (Country, Year, Month, Division). The page enforces sum(SplitPct) = 100
/// across the 4 weeks of a (Country, Year, Month, Div) before saving.
/// </para>
/// </summary>
public class LpmWeeklySalesTargetSplit
{
    public int Id { get; set; }
    public string Country { get; set; } = "";
    public int Year1 { get; set; }
    public int Month1 { get; set; }
    public int DivCode { get; set; }
    /// <summary>1..4 — logical week of the month.</summary>
    public byte WeekNo { get; set; }
    /// <summary>0–100. The 4 rows of a (Country, Year, Month, Div) must sum to 100.</summary>
    public decimal SplitPct { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string? CreateBy { get; set; }
    public DateTime? UpdatedTS { get; set; }
    public string? UpdatedBy { get; set; }
}
