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

    /// <summary>
    /// Weekly view of <see cref="MerchNeedMonth"/>. Now mirrors
    /// <see cref="MerchNeedWeek1"/> so legacy readers keep working.
    /// New code should use the <c>MerchNeedWeek1..4</c> properties + the
    /// SIM batch's <c>WeekNo</c> to pick the right week.
    /// </summary>
    public int MerchNeedWeek { get; set; }

    /// <summary>Per-week Merch Need — <c>(TargetEOM − SOH) / 4 + (TargetSales × SplitPct[N]/100)</c>.</summary>
    public int MerchNeedWeek1 { get; set; }
    /// <summary>Per-week Merch Need for week 2 (default split 20%).</summary>
    public int MerchNeedWeek2 { get; set; }
    /// <summary>Per-week Merch Need for week 3 (default split 25%).</summary>
    public int MerchNeedWeek3 { get; set; }
    /// <summary>Per-week Merch Need for week 4 (default split 35%).</summary>
    public int MerchNeedWeek4 { get; set; }

    /// <summary>Daily slice — <see cref="MerchNeedWeek"/> divided by a fixed 6.</summary>
    public int MerchNeedDay { get; set; }

    /// <summary>Per-Division total qty of LPM-tagged eligible boxes (no
    /// ShopEligible filter). Same value repeats across every store row of
    /// the same division.</summary>
    public int LPMBoxQty { get; set; }
}

/// <summary>
/// Per-(Division × Season) stock breakdown surfaced on the Division Summary
/// tab of the EOM Generate page. Computed on-demand from
/// <c>racks.dbo.LPM_LocStock</c> (HO Stock) and
/// <c>racks.dbo.whboxitems</c> (WH Stock variants) joined to
/// <c>upc_subclass × subclassmaster × Division</c> for the item → division
/// mapping. Not persisted to <c>LPM_EOM_Output</c> — refreshed every time
/// the user lands on the Division Summary tab so the values are always
/// current.
///
/// <para>Season source:
/// <list type="bullet">
///   <item>HO Stock — <c>usa.dbo.upcbarcodes.Itemtype</c> (<c>'W'</c> → Winter, else Summer)</item>
///   <item>WH Stock variants — <c>bfldata.dbo.pallettype.Season</c></item>
/// </list>
/// </para>
/// </summary>
/// <param name="DivCode">Division code (int).</param>
/// <param name="Season">'S' (Summer) or 'W' (Winter).</param>
/// <param name="HOStock">Σ <c>LPM_LocStock.SOH</c> for items in this Div × Season where <c>dataname='HODATA'</c>.</param>
/// <param name="WHStockPurchased">Σ <c>whboxitems.Qty</c> where <c>ShopEligible IS NULL OR &lt;&gt; 'E'</c> (purchased / cleared boxes).</param>
/// <param name="WHStockNonPurchased">Σ <c>whboxitems.Qty</c> where <c>ShopEligible = 'E'</c> (still in-process / not yet purchased).</param>
/// <param name="EligibleStock">Σ <c>whboxitems.Qty</c> where <c>PalletCategory='ELIGIBLE' AND (ShopEligible IS NULL OR &lt;&gt; 'E')</c> — purchased subset of the ELIGIBLE pallet category.</param>
public record DivisionStockBreakdown(
    int    DivCode,
    string Season,
    long   HOStock,
    long   WHStockPurchased,
    long   WHStockNonPurchased,
    long   EligibleStock);
