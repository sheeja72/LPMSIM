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

    public DateTime CreateTS { get; set; }
}
