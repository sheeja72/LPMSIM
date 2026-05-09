namespace LpmSim.Data.Eom;

public record ReadinessItem(bool Ok, string Label, string Detail);

public class EomReadiness
{
    public ReadinessItem Weights      { get; init; } = default!;
    public ReadinessItem Planned      { get; init; } = default!;
    public ReadinessItem SalesTurns   { get; init; } = default!;
    public ReadinessItem WHStock      { get; init; } = default!;
    public ReadinessItem Grades       { get; init; } = default!;
    public ReadinessItem VolumeGroups { get; init; } = default!;
    public ReadinessItem SkuMaxRules  { get; init; } = default!;

    public IEnumerable<ReadinessItem> All => new[]
        { Weights, Planned, SalesTurns, WHStock, Grades, VolumeGroups, SkuMaxRules };

    public bool IsReady => All.All(x => x.Ok);
}

public class EomRow
{
    public string Country { get; set; } = "";
    public string StoreID { get; set; } = "";
    public string StoreName { get; set; } = "";
    public int DivCode { get; set; }
    public string DivisionName { get; set; } = "";
    public int Year { get; set; }
    public int Month { get; set; }

    public decimal WtAvgSoldQty { get; set; }
    public decimal WtAvgTurn { get; set; }
    public int SoldQtyRank { get; set; }
    public int TurnsRank { get; set; }
    public decimal PriorityRank { get; set; }
    public string Grade { get; set; } = "";
    public decimal TargetTurn { get; set; }
    public decimal TargetSales { get; set; }
    public decimal TargetEOM { get; set; }
    public string VolumeGroup { get; set; } = "";
    public int WHStock { get; set; }
    public int WHStockSummer { get; set; }
    public int WHStockWinter { get; set; }
    public int SKUMax { get; set; }

    /// <summary>Store-level division stock-on-hand (sum across items).</summary>
    public int SOH { get; set; }

    /// <summary>Open-to-receive for the month: TargetEOM − SOH + TargetSales.</summary>
    public int MerchNeedMonth { get; set; }

    /// <summary>Weekly view of <see cref="MerchNeedMonth"/> — divided by 4.</summary>
    public int MerchNeedWeek { get; set; }

    /// <summary>Daily slice — <see cref="MerchNeedWeek"/> divided by a fixed 6.</summary>
    public int MerchNeedDay { get; set; }

    /// <summary>Per-Division total qty of LPM-tagged eligible boxes (no
    /// ShopEligible filter). Same value repeats across every store row of
    /// the same division.</summary>
    public int LPMBoxQty { get; set; }
}
