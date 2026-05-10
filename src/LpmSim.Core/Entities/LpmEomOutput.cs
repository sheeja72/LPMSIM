namespace LpmSim.Core.Entities;

public class LpmEomOutput
{
    public string StoreID { get; set; } = "";
    public int DivCode { get; set; }
    public int Month1 { get; set; }
    public int Year1 { get; set; }

    public decimal? WtAvgSoldQty { get; set; }
    public decimal? WtAvgTurn { get; set; }
    public int? SoldQtyRank { get; set; }
    public int? TurnsRank { get; set; }
    public decimal? PriorityRank { get; set; }
    public decimal? TargetTurn { get; set; }
    public decimal? TargetSales { get; set; }
    public decimal? TargetEOM { get; set; }
    public string? VolumeGroup { get; set; }

    /// <summary>Total eligible warehouse stock (Summer + Winter) for this Division
    /// — derived live from <c>whboxitems</c> using the SIM eligibility filters,
    /// not from the manual <c>LPM_WHStock</c> upload.</summary>
    public int? WHStock { get; set; }

    /// <summary>Eligible Summer warehouse stock (PalletType.Season &lt;&gt; 'W')
    /// for this Division. Computed from <c>whboxitems</c>.</summary>
    public int? WHStockSummer { get; set; }

    /// <summary>Eligible Winter warehouse stock (PalletType.Season = 'W')
    /// for this Division. Computed from <c>whboxitems</c>.</summary>
    public int? WHStockWinter { get; set; }

    public int? SKUMax { get; set; }
    public string? Country { get; set; }
    public string? Grade { get; set; }

    /// <summary>
    /// Store-level division stock-on-hand (sum of <c>LPM_LocStock.SOH</c> across
    /// items for this Store × Division). Persisted at EOM time so reports and
    /// downstream consumers don't need to re-aggregate LocStock.
    /// </summary>
    public int? SOH { get; set; }

    /// <summary>
    /// Open-to-receive for the month: <c>TargetEOM − SOH + TargetSales</c>.
    /// What WH must ship this period so the store ends at <c>TargetEOM</c>
    /// after selling <c>TargetSales</c>.
    /// </summary>
    public int? MerchNeedMonth { get; set; }

    /// <summary>
    /// Weekly view of <see cref="MerchNeedMonth"/>. Now mirrors
    /// <see cref="MerchNeedWeek1"/> — kept in place so existing readers
    /// (ADM, ProductionScheduler, the Reports queries, the Custom Report
    /// engine) continue to work without a bigger refactor. New code should
    /// pick the appropriate <c>MerchNeedWeekN</c> based on the SIM batch's
    /// <see cref="LpmSim.Core.Entities.LpmSimBatch.WeekNo"/>.
    /// </summary>
    public int? MerchNeedWeek { get; set; }

    /// <summary>
    /// Per-week Merch Need (Open-to-Receive) for week 1 of the run month.
    /// Computed as <c>(TargetEOM − SOH) / 4 + (TargetSales × SplitPct[1] / 100)</c>
    /// where SplitPct comes from <c>LPM_WeeklySalesTargetSplit</c> for
    /// <c>(Country, Year, Month, DivCode, WeekNo = 1)</c>. When no row is
    /// configured, the EomCalculator falls back to the default split
    /// 20% / 20% / 25% / 35% so EOM never blocks.
    /// </summary>
    public int? MerchNeedWeek1 { get; set; }

    /// <summary>Per-week Merch Need for week 2 (default split 20%).</summary>
    public int? MerchNeedWeek2 { get; set; }

    /// <summary>Per-week Merch Need for week 3 (default split 25%).</summary>
    public int? MerchNeedWeek3 { get; set; }

    /// <summary>Per-week Merch Need for week 4 (default split 35%).</summary>
    public int? MerchNeedWeek4 { get; set; }

    /// <summary>
    /// Daily slice of <see cref="MerchNeedWeek"/> — divided by a fixed 6
    /// (production days/week). Reference / planning metric; the actual
    /// production scheduler picks its own days/week per run.
    /// </summary>
    public int? MerchNeedDay { get; set; }

    /// <summary>
    /// Per-Division total qty of LPM-tagged eligible boxes for the period.
    /// Sourced from racks.dbo.whboxitems with
    ///   pt.PalletCategory = 'ELIGIBLE'  AND  w.LPMDt IS NOT NULL
    /// (no ShopEligible filter — intentionally broader than the WHStock cols
    /// because the planner wants the full LPM-tagged inventory picture).
    /// Same value repeated across every (Store × Division) row in the period.
    /// </summary>
    public int? LPMBoxQty { get; set; }

    public DateTime CreateTS { get; set; }
}
