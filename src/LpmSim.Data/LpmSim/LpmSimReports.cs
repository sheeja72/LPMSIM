using LpmSim.Core.Entities;
using LpmSim.Data.Warehouse;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data.LpmSim;

public class BatchHeader
{
    public long LPMBatchNo { get; set; }
    public string Country { get; set; } = "";
    public DateTime RunDate { get; set; }
    public int RunYear { get; set; }
    public int RunMonth { get; set; }
    public string Status { get; set; } = "";
    public int LinesGenerated { get; set; }
    public long TotalQty { get; set; }
    public string? Sources { get; set; }
    public string? Seasons { get; set; }
    public int? OverrideUsabilityPct { get; set; }
}

public class EomSummaryRow
{
    public long LPMBatchNo { get; set; }
    public string StoreID { get; set; } = "";
    public string StoreName { get; set; } = "";
    public int DivCode { get; set; }
    public string DivisionName { get; set; } = "";
    public decimal? EOM { get; set; }
    public int SOH { get; set; }

    /// <summary>Replaces the legacy <c>Balance</c> (TargetEOM − SOH). Drives the
    /// SIM cap as of Phase C₂. = (TargetEOM − SOH + TargetSales) / 4.</summary>
    public int? MerchNeedWeek { get; set; }

    public long LpmSimQty { get; set; }
    public long RoundRobinQty { get; set; }

    /// <summary>Subset of LpmSimQty produced by Phase 1b/2b override RR
    /// (rows with <c>IsOverride = 1</c>). Surfaces qty allocated above
    /// SKU Max / Merch Need caps so planners can flag over-allocation.</summary>
    public long OverrideQty { get; set; }

    /// <summary>
    /// Per-(Store, Div) priority rank from <c>LPM_EOM_Output.PriorityRank</c>.
    /// Lower = higher priority. Drives both EqualPerStore (primary order) and
    /// EqualFillRate (tiebreak). Surfaced in reports so planners can spot
    /// "rank 34 store got less than rank 36 store" anomalies.
    /// </summary>
    public decimal? PriorityRank { get; set; }
}

/// <summary>
/// Store-level rollup of the SIM result. One row per store, summing across
/// every division.
/// </summary>
public class StoreSummaryRow
{
    public long LPMBatchNo { get; set; }
    public string StoreID { get; set; } = "";
    public string StoreName { get; set; } = "";
    public decimal EOM { get; set; }
    public long SOH { get; set; }

    /// <summary>Replaces legacy <c>EomDiff</c>. Sum of Merch Need (Week)
    /// across all divisions for this store. Drives SIM cap as of Phase C₂.</summary>
    public long MerchNeedWeek { get; set; }

    public long SimQty { get; set; }
    public long RrQty { get; set; }

    /// <summary>Subset of SimQty produced by Phase 1b/2b override RR.</summary>
    public long OverrideQty { get; set; }
}

/// <summary>
/// Division-level rollup of the SIM result. One row per division, summing
/// across every store in the country. Same shape as
/// <see cref="StoreSummaryRow"/> but keyed by DivCode / DivisionName.
/// </summary>
public class DivisionSummaryRow
{
    public long   LPMBatchNo    { get; set; }
    public int    DivCode       { get; set; }
    public string DivisionName  { get; set; } = "";
    public decimal EOM          { get; set; }
    public long   SOH           { get; set; }

    /// <summary>Σ Merch Need (Week) across stores for this division.</summary>
    public long   MerchNeedWeek { get; set; }

    /// <summary>Σ full warehouse Box Qty for items in this division — same
    /// semantics as the Summary (Kind × Warehouse) tab's "Box Qty". Sources
    /// directly from racks.dbo.whboxitems for every item in every qualifying
    /// box, including "phantom" items in those boxes that got 0 allocation.
    /// Same Min/Max Box Usability % filter as SimQty so the two columns
    /// share the same denominator universe. (1.14.34)</summary>
    public long   BoxQty        { get; set; }

    public long   SimQty        { get; set; }
    public long   RrQty         { get; set; }

    /// <summary>Subset of SimQty above SKU Max / Merch Need caps (P1b/P2b override).</summary>
    public long   OverrideQty   { get; set; }

    /// <summary>EOM Balance = EOM − SOH. Positive = headroom to fill;
    /// negative = overstocked vs plan.</summary>
    public decimal EomBalance => EOM - SOH;

    /// <summary>Current Fill Rate % = SOH ÷ EOM × 100. NULL when EOM is 0
    /// (no plan to compare against). Computed on read to stay in sync with
    /// SOH / EOM regardless of how the row was loaded.</summary>
    public decimal? CurrentFillRate =>
        EOM > 0m ? Math.Round((decimal)SOH * 100m / EOM, 1) : (decimal?)null;

    /// <summary>Merch Need (Day) = Merch Need (Week) ÷ 6 (fixed 6-day
    /// production week). Reference daily slice; the actual production
    /// scheduler picks its own days/week.</summary>
    public long MerchNeedDay =>
        (long)Math.Round((decimal)MerchNeedWeek / 6m, MidpointRounding.AwayFromZero);
}

public class BoxDetailRow
{
    public long LPMBatchNo { get; set; }
    public string? StoreID { get; set; }
    public string? StoreName { get; set; }
    public int? DivCode { get; set; }
    public string? DivisionName { get; set; }
    public string BoxNo { get; set; } = "";
    /// <summary>Pallet number this box belongs to (1.14.12). From whboxitems.PalletNo via JOIN.</summary>
    public string? PalletNo { get; set; }
    public long? BoxQty { get; set; }
    public long LpmSimQty { get; set; }
    /// <summary>Tag indicating LPM (has LPMDt) or Non-LPM (LPMDt is null) box.</summary>
    public string? BoxKind { get; set; }
    public long RoundRobinQty { get; set; }
    public string? PalletType { get; set; }
    public string? TypeName { get; set; }
    public string? PalletCategory { get; set; }
    public DateTime? TrnDate { get; set; }
    public string? Warehouse { get; set; }
    public string? Rack { get; set; }
    /// <summary>
    /// 1.14.65 — Physical tote identifier from <c>whboxitems.ToteId</c>
    /// (UAE) or <c>[&lt;DataName&gt;].dbo.WHBoxItemsExport.ToteId</c>. NULL
    /// when the row has no tote tag.
    /// </summary>
    public string? ToteId { get; set; }

    // 1.14.70 — Pallet-purchase / GIN columns. Sourced from whboxitems /
    // WHBoxItemsExport, matched on (BoxNo, PalletNo) so the value belongs
    // to the SPECIFIC pallet the allocator chose (rather than a non-
    // deterministic MAX across all rows sharing the BoxNo). Nullable —
    // many older rows have these blank.
    /// <summary>1.14.70 — Purchase date for the pallet (whboxitems.PurDate). NULL when not set.</summary>
    public DateTime? PurDate { get; set; }
    /// <summary>1.14.70 — Goods Inwards Note number for the pallet (whboxitems.GINNo). NULL when not set.</summary>
    public string? GINNo { get; set; }
    /// <summary>1.14.70 — Goods Inwards Note date for the pallet (whboxitems.GinDate). NULL when not set.</summary>
    public DateTime? GinDate { get; set; }
    /// <summary>1.14.70 — From/To routing label for the pallet (whboxitems.FromTo). NULL when not set.</summary>
    public string? FromTo { get; set; }

    /// <summary>1.14.80 — Container number (whboxitems.ContNo). Identifies the
    /// inbound shipping container this box came in on. NULL when not tagged.</summary>
    public string? ContNo { get; set; }

    /// <summary>1.14.80 — Most recent Approved batch (other than the current one)
    /// in the same country that contains this BoxNo. NULL when the box has
    /// never been allocated in any earlier Approved batch. Helps planners
    /// spot "this box already shipped in batch #N, why is it back?" cases.</summary>
    public long? RecentBatchNo { get; set; }

    /// <summary>1.14.81 — Raw LPM-tagged date from <c>whboxitems.LPMDt</c> /
    /// <c>WHBoxItemsExport.LPMDt</c>. Pair-column with BoxKind: when this is
    /// non-NULL the box is "LPM" (which is exactly how BoxKind is computed).
    /// NULL means the box is Non-LPM. Surfaced separately so the planner can
    /// see *when* the LPM tag was applied, not just whether it exists.</summary>
    public DateTime? LpmDate { get; set; }

    /// <summary>% of the box's qty that was allocated in this batch (Allocated / Box Qty × 100).</summary>
    public decimal? SkuUsabilityPct =>
        BoxQty.HasValue && BoxQty.Value > 0
            ? Math.Round((decimal)LpmSimQty * 100m / BoxQty.Value, 1)
            : (decimal?)null;
}

public record BatchAggregates(int Lines, int Stores, int Boxes, long TotalQty)
{
    // Per-source split. LPM = Phase starts with "P1"; Non-LPM = Phase starts with "P2".
    public int  LpmLines     { get; init; }
    public int  LpmBoxes     { get; init; }
    public int  LpmStores    { get; init; }
    public long LpmQty       { get; init; }
    public int  NonLpmLines  { get; init; }
    public int  NonLpmBoxes  { get; init; }
    public int  NonLpmStores { get; init; }
    public long NonLpmQty    { get; init; }
}

/// <summary>
/// 1.14.93 — One row of the per-UPC Allocation Gap report (new tab next to
/// the existing per-box Allocation Gap tab). Grain = one ItemCode for the
/// chosen batch. Lists items where the SIM didn't fully drain the eligible
/// warehouse qty, along with the dominant SKIP reason from
/// <c>LPMSIM_AllocTrace</c> so the planner can see why per item.
/// </summary>
/// <param name="EligibleWhQty">Qty in eligible whboxitems that ENTERED the
///   SIM box pool for this item — sum of <c>MAX(LineQty)</c> per
///   <c>(BoxNo, ItemCode)</c> in <c>LPMSIM_AllocTrace</c> for the batch.
///   Matches what the allocator actually considered (after every filter:
///   ShopEligible, PalletCategory, Season, LPM/Non-LPM, LPM Months,
///   Warehouses, closed-box exclusion).</param>
/// <param name="SimQty">Qty actually placed — <c>SUM(Qty)</c> from
///   <c>LPMSIM_Output</c> for the batch grouped by ItemCode.</param>
/// <param name="RemainingQty"><c>EligibleWhQty − SimQty</c>. The "gap".</param>
/// <param name="TopReason">The most-frequent <c>SKIP_*</c> Decision in the
///   AllocTrace for this item — the dominant reason a candidate store was
///   skipped. Empty string when no SKIP rows exist for the item (rare —
///   usually means the allocator simply ran out of candidate stores rather
///   than hitting an explicit skip rule).</param>
public record ItemAllocationGapRow(
    string ItemCode,
    string ItemName,
    string Division,
    long   EligibleWhQty,
    long   SimQty,
    long   RemainingQty,
    string TopReason);

/// <summary>
/// One row of the "Allocation Result" matrix at the top of the SIM Generate
/// preview. Grain = (Kind, Warehouse). Each row shows how many distinct items
/// / boxes flowed from a particular source-kind × warehouse pair, the
/// warehouse-side stock total of those boxes, and the SIM Qty allocated.
///
/// Replaces the older two-row LPM/Non-LPM rollup. Lets the planner see
/// "JAFZA contributed 220K of LPM stock and SIM allocated 99K of it" at a
/// glance, broken out by warehouse.
/// </summary>
public sealed record SourceWarehouseRow(
    string Kind,           // "LPM" or "Non-LPM" — derived from whboxitems.LPMDt
    string Warehouse,      // racks.dbo.whboxitems.Warehouse
    int    SkuCount,       // distinct itemcodes in LPMSIM_Output for this (Kind, WH)
    int    BoxCount,       // distinct BoxNos in LPMSIM_Output for this (Kind, WH)
    long   BoxQty,         // sum of warehouse-side qty for the boxes used (whboxitems.Qty)
    long   SimQty);        // sum of allocated qty (LPMSIM_Output.Qty)

public class ItemDetailRow
{
    public long LPMBatchNo { get; set; }
    public string StoreID { get; set; } = "";
    public string StoreName { get; set; } = "";
    public int DivCode { get; set; }
    public string DivisionName { get; set; } = "";
    public string BoxNo { get; set; } = "";
    /// <summary>Pallet number this box belongs to (1.14.12). From whboxitems.PalletNo via JOIN.</summary>
    public string? PalletNo { get; set; }
    public string Itemcode { get; set; } = "";
    public long? BoxItemQty { get; set; }
    public int? SKUMax { get; set; }
    public int SOH { get; set; }
    public int LpmQty { get; set; }
    public int RoundRobinQty { get; set; }
    public string? Phase { get; set; }
    /// <summary>"LPM" when the source box has a non-NULL LPMDt, else "Non-LPM".</summary>
    public string BoxKind { get; set; } = "";
    /// <summary>Per-(Store, Div) priority rank from <c>LPM_EOM_Output.PriorityRank</c>.</summary>
    public decimal? PriorityRank { get; set; }
}

/// <summary>
/// Per-(Country, Year, Month, Store, Item, Season) row from
/// <c>LPM_SimItemSkuMax</c>. Drives the SIM allocator's per-item SKU Max cap;
/// surfaced on the new "SKU Max Detail" tab so planners can audit how each
/// item × store × season combination was bracketed by the SKUMaxRule.
/// </summary>
public class ItemSkuMaxRow
{
    public string StoreID      { get; set; } = "";
    public string StoreName    { get; set; } = "";
    public int    DivCode      { get; set; }
    public string DivisionName { get; set; } = "";
    public string ItemCode     { get; set; } = "";
    public string Season       { get; set; } = "S";
    public long   WHBoxQty     { get; set; }
    public string? VolumeGroup { get; set; }
    public int    SKUMax       { get; set; }
}

/// <summary>
/// One per-(Box × Item × Store) allocation decision. Surfaces every reason
/// an eligible box was or wasn't placed at a candidate store. Decision values:
/// ALLOC | SKIP_SKUMAX | SKIP_TARGET | SKIP_NO_DIV | SKIP_NO_EOM.
/// </summary>
public class AllocTraceRow
{
    public long LPMBatchNo { get; set; }
    public string BoxNo { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public int? DivCode { get; set; }
    public string? DivisionName { get; set; }
    public string? StoreID { get; set; }
    public string? StoreName { get; set; }
    public int? SKUMax { get; set; }
    public int? SOH_Item { get; set; }
    public int? SkuBalance { get; set; }
    public decimal? TargetEOM { get; set; }
    public int? DivSOH { get; set; }
    public decimal? AlreadyAllocated { get; set; }
    public decimal? TargetRemain { get; set; }
    public int LineQty { get; set; }
    public int Take { get; set; }
    public string Decision { get; set; } = "";
    public string Phase { get; set; } = "";
}

/// <summary>
/// Counts of trace rows by Decision plus a separate qty view so the planner
/// can verify qty conservation (allocated + discarded = input box qty).
/// </summary>
public record AllocTraceCounts(
    int Alloc, int AllocRr, int SkipSkuMax, int SkipTarget, int SkipNoDiv, int SkipNoEom,
    long AllocQty, long AllocRrQty, long DiscardedNoDivQty, long DiscardedNoEomQty)
{
    public int  TotalDecisions    => Alloc + AllocRr + SkipSkuMax + SkipTarget + SkipNoDiv + SkipNoEom;
    public long TotalAllocatedQty => AllocQty + AllocRrQty;
    public long TotalDiscardedQty => DiscardedNoDivQty + DiscardedNoEomQty;
}

public class LpmSimReportService(IDbContextFactory<LpmDbContext> dbFactory)
{
    /// <summary>
    /// 1.14.61 — Helper used by every report method that needs to resolve the
    /// country-aware whboxitems source. Returns the table name to embed in
    /// SQL: <c>racks.dbo.whboxitems</c> for UAE; <c>[&lt;DataName&gt;].dbo.WHBoxItemsExport</c>
    /// for other countries. Opens the EF Core connection if it isn't already
    /// open (the resolver needs an open SqlConnection).
    /// </summary>
    private static async Task<string> ResolveWhSrcAsync(
        LpmDbContext db, string country, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        return await WhBoxItemsSource.ResolveAsync(conn, country, ct);
    }

    /// <summary>
    /// SKU Max Detail report — reads <c>LPM_SimItemSkuMax</c> for the run period.
    /// At least one of <paramref name="divCode"/>, <paramref name="storeId"/>,
    /// or <paramref name="itemCode"/> must be supplied (mandatory filter).
    /// Returns rows with Store name + Division name resolved.
    /// </summary>
    public async Task<List<ItemSkuMaxRow>> GetItemSkuMaxAsync(
        string country, int year, int month,
        int? divCode, string? storeId, string? itemCode,
        int top = 5000, CancellationToken ct = default)
    {
        // Enforce mandatory filter
        bool hasDiv   = divCode.HasValue && divCode.Value > 0;
        bool hasStore = !string.IsNullOrWhiteSpace(storeId);
        bool hasItem  = !string.IsNullOrWhiteSpace(itemCode);
        if (!hasDiv && !hasStore && !hasItem)
            return new List<ItemSkuMaxRow>();   // caller responsible for showing "pick a filter"

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        using var cmd = (Microsoft.Data.SqlClient.SqlCommand)conn.CreateCommand();
        cmd.CommandText = $@"
SELECT TOP (@top)
       sim.StoreID,
       StoreName    = ISNULL(ds.PBFullname, ''),
       sim.DivCode,
       DivisionName = ISNULL(div.Division, CAST(sim.DivCode AS varchar)),
       sim.ItemCode,
       sim.Season,
       sim.WHBoxQty,
       sim.VolumeGroup,
       sim.SKUMax
  FROM dbo.LPM_SimItemSkuMax sim
  LEFT JOIN dbo.Division div ON div.DivCode = sim.DivCode
  OUTER APPLY (
      SELECT TOP 1 PBFullname FROM dbo.DataSettings d
       WHERE d.StoreID = sim.StoreID
         AND (d.SIMCountry = @c OR d.SIMCountry IS NULL)
  ) ds
 WHERE sim.Country = @c AND sim.Year1 = @y AND sim.Month1 = @m
   {(hasDiv   ? "AND sim.DivCode = @divCode"     : "")}
   {(hasStore ? "AND sim.StoreID = @storeId"     : "")}
   {(hasItem  ? "AND sim.ItemCode LIKE @itemLike" : "")}
 ORDER BY div.Division, sim.StoreID, sim.ItemCode, sim.Season;";
        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@c", country));
        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@y", year));
        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@m", month));
        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@top", top));
        if (hasDiv)   cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@divCode", divCode!.Value));
        if (hasStore) cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@storeId", storeId!.Trim()));
        if (hasItem)  cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@itemLike", "%" + itemCode!.Trim() + "%"));
        cmd.CommandTimeout = 300;

        var rows = new List<ItemSkuMaxRow>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ItemSkuMaxRow
            {
                StoreID      = rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                StoreName    = rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                DivCode      = rdr.IsDBNull(2) ? 0  : rdr.GetInt32(2),
                DivisionName = rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                ItemCode     = rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                Season       = rdr.IsDBNull(5) ? "S" : rdr.GetString(5),
                WHBoxQty     = rdr.IsDBNull(6) ? 0L : rdr.GetInt64(6),
                VolumeGroup  = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                SKUMax       = rdr.IsDBNull(8) ? 0  : rdr.GetInt32(8),
            });
        }
        return rows;
    }

    public async Task<List<BatchHeader>> ListBatchesAsync(
        string country, DateTime? runDate,
        // 1.14.67 — optional viewer-user for the "Running" batch filter.
        // Null → don't hide Running batches from anyone (legacy behaviour).
        string? currentUser = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.LpmSimBatches.AsNoTracking().Where(b => b.Country == country);
        if (runDate.HasValue) q = q.Where(b => b.RunDate == runDate.Value.Date);
        // 1.14.67 — Hide "Running" batches from anyone except their creator
        // (and only if they're not stale — older than 30 minutes).
        var staleCutoff = DateTime.Now.AddMinutes(-30);
        if (!string.IsNullOrEmpty(currentUser))
        {
            q = q.Where(b => b.Status != "Running"
                          || (b.CreatedBy == currentUser && b.CreateTS >= staleCutoff));
        }
        return await q.OrderByDescending(b => b.LPMBatchNo)
            .Select(b => new BatchHeader
            {
                LPMBatchNo           = b.LPMBatchNo,
                Country              = b.Country,
                RunDate              = b.RunDate,
                RunYear              = b.RunYear,
                RunMonth             = b.RunMonth,
                Status               = b.Status,
                LinesGenerated       = b.LinesGenerated,
                TotalQty             = b.TotalQty,
                Sources              = b.Sources,
                Seasons              = b.Seasons,
                OverrideUsabilityPct = b.OverrideUsabilityPct,
            })
            .ToListAsync(ct);
    }

    public async Task<List<EomSummaryRow>> GetEomSummaryAsync(
        long batchNo,
        decimal? minBoxUsabilityPct = null,
        decimal? maxBoxUsabilityPct = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var b = await db.LpmSimBatches.AsNoTracking().FirstOrDefaultAsync(x => x.LPMBatchNo == batchNo, ct);
        if (b is null) return new();
        // 1.14.61 — Country-aware whboxitems source.
        var whSrc = await ResolveWhSrcAsync(db, b.Country, ct);

        // Drives the report off LPM_EOM_Output so EVERY (Store, Div) row in the
        // plan appears — even those with zero SIM Qty / zero SOH. SIM and SOH
        // are LEFT-JOINed onto that baseline.
        // Box-usability filter (optional): when @minPct/@maxPct are non-NULL,
        // only allocations from boxes whose total SIM/Box-Qty % falls in range
        // contribute to LpmSimQty / RoundRobinQty.
        var sql = $@"
WITH BatchItems AS (
    -- Distinct items in this batch's allocations.
    SELECT DISTINCT Itemcode FROM dbo.LPMSIM_Output WHERE LPMBatchNo = @batchNo
),
ItemDivLs AS (
    -- Primary source: denormalized DivCode on LPM_LocStock (matches engine).
    SELECT bi.Itemcode, DivCode = MIN(ls.DivCode)
      FROM BatchItems bi
      INNER JOIN racks.dbo.LPM_LocStock ls
              ON ls.Itemcode = bi.Itemcode AND ls.Country = @country AND ls.DivCode IS NOT NULL
     GROUP BY bi.Itemcode
),
ItemDivUpc AS (
    -- Fallback for items not in LocStock for this country.
    SELECT bi.Itemcode, DivCode = MIN(d.DivCode)
      FROM BatchItems bi
      INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = bi.Itemcode
      INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID  = u.MH4ID
      INNER JOIN LPMSIM.dbo.Division       d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
     WHERE bi.Itemcode NOT IN (SELECT Itemcode FROM ItemDivLs)
     GROUP BY bi.Itemcode
),
ItemDiv AS (
    SELECT Itemcode, DivCode FROM ItemDivLs
    UNION ALL
    SELECT Itemcode, DivCode FROM ItemDivUpc
),
BoxUsability AS (
    SELECT s.BoxNo,
           BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint)) FROM {whSrc} w WHERE w.BoxNo = s.BoxNo), 0),
           SimQty = SUM(CAST(s.Qty AS bigint))
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.BoxNo
),
QualifyingBoxes AS (
    -- Usability rounded to 1 decimal so server-side filter agrees byte-for-byte
    -- with what the UI displays in the SIM Boxes tab (also rounded to 1dp).
    -- A box reading 50.0% passes when Min = 50; a box reading 49.9% fails.
    SELECT BoxNo
      FROM BoxUsability
     WHERE BoxQty > 0
       AND (@minPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) >= @minPct)
       AND (@maxPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) <= @maxPct)
),
SimAgg AS (
    SELECT s.StoreID, id.DivCode,
           LpmSimQty     = SUM(CAST(s.Qty AS bigint)),
           RoundRobinQty = SUM(CASE WHEN s.IsRoundRobin = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END),
           OverrideQty   = SUM(CASE WHEN s.IsOverride   = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END)
      FROM dbo.LPMSIM_Output s
      INNER JOIN ItemDiv id        ON id.Itemcode = s.Itemcode
      INNER JOIN QualifyingBoxes qb ON qb.BoxNo   = s.BoxNo
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.StoreID, id.DivCode
),
SohAgg AS (
    -- Country resolved via DataSettings (the authoritative source) rather than
    -- LPM_LocStock.Country, which is occasionally populated as empty string ''
    -- for some stores by the daily ETL. Joining via DataSettings means a store
    -- with the right StoreID is always counted regardless of what LocStock's
    -- denormalised Country column says.
    SELECT ls.StoreID, ls.DivCode, SOH = SUM(ISNULL(ls.SOH,0))
      FROM racks.dbo.LPM_LocStock ls
      INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
     WHERE ds.SIMCountry = @country
       AND ls.DivCode IS NOT NULL
       AND ls.StoreID IS NOT NULL AND ls.StoreID <> ''
     GROUP BY ls.StoreID, ls.DivCode
)
SELECT @batchNo                  AS LPMBatchNo,
       eo.StoreID,
       ds.PBFullname              AS StoreName,
       eo.DivCode,
       div.Division               AS DivisionName,
       EOM           = eo.TargetEOM,
       SOH           = ISNULL(soh.SOH, 0),
       MerchNeedWeek = eo.MerchNeedWeek,
       LpmSimQty     = ISNULL(sa.LpmSimQty, 0),
       RoundRobinQty = ISNULL(sa.RoundRobinQty, 0),
       OverrideQty   = ISNULL(sa.OverrideQty, 0),
       PriorityRank  = eo.PriorityRank
  FROM dbo.LPM_EOM_Output eo
  LEFT JOIN SimAgg sa            ON sa.StoreID  = eo.StoreID  AND sa.DivCode = eo.DivCode
  LEFT JOIN SohAgg soh           ON soh.StoreID = eo.StoreID  AND soh.DivCode = eo.DivCode
  -- OUTER APPLY guarantees one DataSettings row per Store, even if the
  -- master table has duplicate (StoreID, SIMCountry) entries. A plain LEFT
  -- JOIN would multiply Summary rows and inflate the LPM SIM Qty column total.
  OUTER APPLY (
      SELECT TOP 1 PBFullname
        FROM dbo.DataSettings d
       WHERE d.StoreID = eo.StoreID
         AND (d.SIMCountry = @country OR d.SIMCountry IS NULL)
  ) ds
  LEFT JOIN dbo.Division div     ON div.DivCode = eo.DivCode
 WHERE eo.Country = @country AND eo.Year1 = @y AND eo.Month1 = @m
 ORDER BY div.Division, eo.StoreID;";

        return await ExecAsync(db, sql, ReadEomSummary, ct, new Dictionary<string, object>
        {
            ["@batchNo"] = batchNo,
            ["@country"] = b.Country,
            ["@y"]       = b.RunYear,
            ["@m"]       = b.RunMonth,
            ["@minPct"]  = (object?)minBoxUsabilityPct ?? DBNull.Value,
            ["@maxPct"]  = (object?)maxBoxUsabilityPct ?? DBNull.Value,
        });
    }

    /// <summary>
    /// Store-level rollup: one row per store with totals summed across every
    /// division. Columns: StoreID, StoreName, EOM (sum TargetEOM across divs),
    /// SOH (sum LocStock across divs), EomDiff (EOM − SOH), SimQty, RrQty.
    /// <para>
    /// <paramref name="minBoxUsabilityPct"/> / <paramref name="maxBoxUsabilityPct"/>
    /// optionally restrict the allocations counted toward SimQty/RrQty to
    /// boxes whose <c>SUM(SIM Qty) / SUM(BoxQty) × 100</c> falls in the range.
    /// EOM and SOH columns are not affected by this filter (they're per-store
    /// plan + current stock, independent of which boxes we consider).
    /// </para>
    /// </summary>
    public async Task<List<StoreSummaryRow>> GetStoreSummaryAsync(
        long batchNo,
        decimal? minBoxUsabilityPct = null,
        decimal? maxBoxUsabilityPct = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var b = await db.LpmSimBatches.AsNoTracking().FirstOrDefaultAsync(x => x.LPMBatchNo == batchNo, ct);
        if (b is null) return new();
        // 1.14.61 — Country-aware whboxitems source.
        var whSrc = await ResolveWhSrcAsync(db, b.Country, ct);

        var sql = $@"
WITH BoxUsability AS (
    -- Per-box usability % = total SIM Qty allocated / total Qty in the box × 100.
    -- BoxQty comes from the source (country-aware whboxitems); SIM is summed
    -- across every (Store, Item) target.
    SELECT s.BoxNo,
           BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint)) FROM {whSrc} w WHERE w.BoxNo = s.BoxNo), 0),
           SimQty = SUM(CAST(s.Qty AS bigint))
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.BoxNo
),
QualifyingBoxes AS (
    -- Apply the optional usability range filter. NULL = no bound.
    -- Usability rounded to 1 decimal so server-side filter agrees byte-for-byte
    -- with what the UI displays in the SIM Boxes tab (also rounded to 1dp) —
    -- and so this report matches Summary / Item Details / Trace / Source Matrix.
    SELECT BoxNo
      FROM BoxUsability
     WHERE BoxQty > 0
       AND (@minPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) >= @minPct)
       AND (@maxPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) <= @maxPct)
),
SimAgg AS (
    SELECT s.StoreID,
           SimQty      = SUM(CAST(s.Qty AS bigint)),
           RrQty       = SUM(CASE WHEN s.IsRoundRobin = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END),
           OverrideQty = SUM(CASE WHEN s.IsOverride   = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END)
      FROM dbo.LPMSIM_Output s
      INNER JOIN QualifyingBoxes qb ON qb.BoxNo = s.BoxNo
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.StoreID
),
EomAgg AS (
    SELECT eo.StoreID,
           EOM           = SUM(ISNULL(eo.TargetEOM, 0)),
           MerchNeedWeek = SUM(CAST(ISNULL(eo.MerchNeedWeek, 0) AS bigint))
      FROM dbo.LPM_EOM_Output eo
     WHERE eo.Country = @country AND eo.Year1 = @y AND eo.Month1 = @m
     GROUP BY eo.StoreID
),
SohAgg AS (
    -- Total SOH at the store across ALL items (not filtered by division
    -- mapping) so the planner sees the true on-hand quantity.
    -- Country resolved via DataSettings join (LocStock.Country occasionally
    -- carries empty string '' for some stores from the ETL — see Store×Div SQL).
    SELECT ls.StoreID, SOH = SUM(CAST(ISNULL(ls.SOH, 0) AS bigint))
      FROM racks.dbo.LPM_LocStock ls
      INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
     WHERE ds.SIMCountry = @country
       AND ls.StoreID IS NOT NULL AND ls.StoreID <> ''
     GROUP BY ls.StoreID
),
AllStores AS (
    -- Every active store in the country appears, even if it produced no SIM
    -- this run. This guarantees the Store Summary mirrors the planning roster
    -- (one row per store) regardless of whether any allocation happened.
    SELECT StoreID FROM dbo.DataSettings
     WHERE ActiveStore = 'Y' AND SIMCountry = @country
       AND StoreID IS NOT NULL AND StoreID <> ''
    UNION SELECT StoreID FROM SimAgg
    UNION SELECT StoreID FROM EomAgg
    UNION SELECT StoreID FROM SohAgg
)
SELECT @batchNo                  AS LPMBatchNo,
       a.StoreID,
       ds.PBFullname              AS StoreName,
       EOM           = ISNULL(eo.EOM, 0),
       SOH           = ISNULL(soh.SOH, 0),
       MerchNeedWeek = ISNULL(eo.MerchNeedWeek, 0),
       SimQty        = ISNULL(sim.SimQty, 0),
       RrQty         = ISNULL(sim.RrQty, 0),
       OverrideQty   = ISNULL(sim.OverrideQty, 0)
  FROM AllStores a
  LEFT JOIN SimAgg sim ON sim.StoreID = a.StoreID
  LEFT JOIN EomAgg eo  ON eo.StoreID  = a.StoreID
  LEFT JOIN SohAgg soh ON soh.StoreID = a.StoreID
  -- One DataSettings row per Store (defensive — see Summary tab note).
  CROSS APPLY (
      SELECT TOP 1 PBFullname, SIMCountry
        FROM dbo.DataSettings d
       WHERE d.StoreID = a.StoreID AND d.SIMCountry = @country
  ) ds
 ORDER BY ds.PBFullname, a.StoreID;";

        return await ExecAsync(db, sql, ReadStoreSummary, ct, new Dictionary<string, object>
        {
            ["@batchNo"] = batchNo,
            ["@country"] = b.Country,
            ["@y"]       = b.RunYear,
            ["@m"]       = b.RunMonth,
            ["@minPct"]  = (object?)minBoxUsabilityPct ?? DBNull.Value,
            ["@maxPct"]  = (object?)maxBoxUsabilityPct ?? DBNull.Value,
        });
    }

    /// <summary>
    /// Division-level rollup: one row per division with totals summed across
    /// every store in the country. Mirrors <see cref="GetStoreSummaryAsync"/>
    /// — same usability filter, same column shape — but groups by DivCode.
    /// </summary>
    public async Task<List<DivisionSummaryRow>> GetDivisionSummaryAsync(
        long batchNo,
        decimal? minBoxUsabilityPct = null,
        decimal? maxBoxUsabilityPct = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var b = await db.LpmSimBatches.AsNoTracking().FirstOrDefaultAsync(x => x.LPMBatchNo == batchNo, ct);
        if (b is null) return new();
        // 1.14.61 — Country-aware whboxitems source.
        var whSrc = await ResolveWhSrcAsync(db, b.Country, ct);

        // 1.14.36 — Perf refactor. Previously this method used 8 nested
        // CTEs that the optimizer kept inlining as views, causing
        // whboxitems to be scanned 2–3× per query (once via the
        // BoxUsability correlated subquery, once via BoxItems, once via
        // BoxQtyAgg). After adding the Box Qty column in 1.14.34, the
        // Division Summary tab was slow to load.
        //
        // Fix: materialize the heavy joins ONCE into temp tables with
        // clustered indexes, then run the rollup against the temps. Same
        // pattern that fixed Rule 5 (1.14.10) and SKU Max Excluded audit
        // (1.14.32).
        //
        // Temps created in this command are session-scoped — they live for
        // the duration of this SqlCommand and get dropped at the end. SET
        // NOCOUNT ON suppresses row-count messages so the only result set
        // the SqlDataReader sees is the final SELECT.
        var sql = $@"
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#BoxUsability') IS NOT NULL DROP TABLE #BoxUsability;
IF OBJECT_ID('tempdb..#QB')           IS NOT NULL DROP TABLE #QB;
IF OBJECT_ID('tempdb..#BoxRows')      IS NOT NULL DROP TABLE #BoxRows;
IF OBJECT_ID('tempdb..#ItemDivLs')    IS NOT NULL DROP TABLE #ItemDivLs;
IF OBJECT_ID('tempdb..#ItemDivUpc')   IS NOT NULL DROP TABLE #ItemDivUpc;
IF OBJECT_ID('tempdb..#ItemDiv')      IS NOT NULL DROP TABLE #ItemDiv;

-- 1) Per-box usability % = SIM Qty / Box Qty × 100, used by the optional
-- Min/Max Box Usability % filter so this report agrees with the others.
-- The correlated subquery to whboxitems happens ONCE here (was running
-- repeatedly when this was a CTE).
SELECT s.BoxNo,
       BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint))
                          FROM {whSrc} w
                         WHERE w.BoxNo = s.BoxNo), 0),
       SimQty = SUM(CAST(s.Qty AS bigint))
  INTO #BoxUsability
  FROM dbo.LPMSIM_Output s
 WHERE s.LPMBatchNo = @batchNo
 GROUP BY s.BoxNo;

-- 2) Apply the Min/Max % filter and persist the qualifying box list.
-- Clustered index on BoxNo so downstream INNER JOINs on BoxNo are seeks.
SELECT BoxNo
  INTO #QB
  FROM #BoxUsability
 WHERE BoxQty > 0
   AND (@minPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) >= @minPct)
   AND (@maxPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) <= @maxPct);

CREATE CLUSTERED INDEX IX_QB ON #QB (BoxNo);

-- 3) THE BIG SCAN: whboxitems × #QB happens exactly ONCE here. Everything
-- downstream (BoxQtyAgg, BoxItems via DISTINCT, ItemDiv lookups) reads
-- from this temp table.
--
-- 1.14.34 widened the item-universe from `LPMSIM_Output items` to `every
-- item in every qualifying box` so phantom items (in boxes but not
-- allocated) roll up into Box Qty correctly. That widening is what made
-- the CTE version slow.
--
-- Clustered on itemcode so the GROUP BY DivCode (via ItemDiv join) is a
-- seek-based aggregation, not a hash.
SELECT w.BoxNo, w.itemcode, Qty = CAST(ISNULL(w.Qty, 0) AS bigint)
  INTO #BoxRows
  FROM {whSrc} w
  INNER JOIN #QB qb ON qb.BoxNo = w.BoxNo;

CREATE CLUSTERED INDEX IX_BR ON #BoxRows (itemcode);

-- 4) Item → DivCode resolution. Mirrors the Store×Div SQL: LocStock first
-- (matches the allocator), upc_subclass × subclassmaster × Division as
-- fallback for items not in LocStock.
--
-- Sourced from DISTINCT itemcodes in #BoxRows (was BoxItems CTE) so
-- phantom items get resolved too — necessary for the 1.14.34 Box Qty
-- semantics.
SELECT bi.itemcode, DivCode = MIN(ls.DivCode)
  INTO #ItemDivLs
  FROM (SELECT DISTINCT itemcode FROM #BoxRows) bi
  INNER JOIN racks.dbo.LPM_LocStock ls
          ON ls.Itemcode = bi.itemcode
         AND ls.Country  = @country
         AND ls.DivCode  IS NOT NULL
 GROUP BY bi.itemcode;

CREATE CLUSTERED INDEX IX_IDL ON #ItemDivLs (itemcode);

SELECT bi.itemcode, DivCode = MIN(d.DivCode)
  INTO #ItemDivUpc
  FROM (SELECT DISTINCT br.itemcode
          FROM #BoxRows br
         WHERE br.itemcode NOT IN (SELECT itemcode FROM #ItemDivLs)) bi
  INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = bi.itemcode
  INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID  = u.MH4ID
  INNER JOIN LPMSIM.dbo.Division               d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
 GROUP BY bi.itemcode;

SELECT itemcode, DivCode INTO #ItemDiv FROM #ItemDivLs
UNION ALL
SELECT itemcode, DivCode FROM #ItemDivUpc;

CREATE CLUSTERED INDEX IX_ID ON #ItemDiv (itemcode);

-- 5) Final rollup. Every CTE below reads from indexed temp tables — no
-- more re-scans of whboxitems / LocStock / upc_subclass.
WITH SimAgg AS (
    SELECT id.DivCode,
           SimQty      = SUM(CAST(s.Qty AS bigint)),
           RrQty       = SUM(CASE WHEN s.IsRoundRobin = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END),
           OverrideQty = SUM(CASE WHEN s.IsOverride   = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END)
      FROM dbo.LPMSIM_Output s
      INNER JOIN #ItemDiv id ON id.itemcode = s.Itemcode
      INNER JOIN #QB qb      ON qb.BoxNo    = s.BoxNo
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY id.DivCode
),
BoxQtyAgg AS (
    -- 1.14.34 semantics: Σ full whboxitems.Qty for every item in every
    -- qualifying box, by division. Includes phantom items not allocated.
    SELECT id.DivCode,
           BoxQty = SUM(br.Qty)
      FROM #BoxRows br
      INNER JOIN #ItemDiv id ON id.itemcode = br.itemcode
     GROUP BY id.DivCode
),
EomAgg AS (
    SELECT eo.DivCode,
           EOM           = SUM(ISNULL(eo.TargetEOM, 0)),
           MerchNeedWeek = SUM(CAST(ISNULL(eo.MerchNeedWeek, 0) AS bigint))
      FROM dbo.LPM_EOM_Output eo
     WHERE eo.Country = @country AND eo.Year1 = @y AND eo.Month1 = @m
     GROUP BY eo.DivCode
),
SohAgg AS (
    -- Per-Division SOH from LocStock (Country resolved via DataSettings join,
    -- same defensive pattern as Store×Div).
    SELECT ls.DivCode, SOH = SUM(CAST(ISNULL(ls.SOH, 0) AS bigint))
      FROM racks.dbo.LPM_LocStock ls
      INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
     WHERE ds.SIMCountry = @country
       AND ls.DivCode IS NOT NULL
       AND ls.StoreID IS NOT NULL AND ls.StoreID <> ''
     GROUP BY ls.DivCode
),
AllDivs AS (
    -- Every division in the master shows up, even if it has no SIM/EOM/SOH
    -- this run. Guarantees the rollup mirrors the planning roster.
    SELECT DivCode FROM dbo.Division
    UNION SELECT DivCode FROM SimAgg
    UNION SELECT DivCode FROM EomAgg
    UNION SELECT DivCode FROM SohAgg
)
SELECT @batchNo                  AS LPMBatchNo,
       a.DivCode,
       div.Division               AS DivisionName,
       EOM           = ISNULL(eo.EOM, 0),
       SOH           = ISNULL(soh.SOH, 0),
       MerchNeedWeek = ISNULL(eo.MerchNeedWeek, 0),
       SimQty        = ISNULL(sim.SimQty, 0),
       RrQty         = ISNULL(sim.RrQty, 0),
       OverrideQty   = ISNULL(sim.OverrideQty, 0),
       BoxQty        = ISNULL(bq.BoxQty, 0)
  FROM AllDivs a
  LEFT JOIN dbo.Division div ON div.DivCode = a.DivCode
  LEFT JOIN SimAgg sim       ON sim.DivCode = a.DivCode
  LEFT JOIN EomAgg eo        ON eo.DivCode  = a.DivCode
  LEFT JOIN SohAgg soh       ON soh.DivCode = a.DivCode
  LEFT JOIN BoxQtyAgg bq     ON bq.DivCode  = a.DivCode
 ORDER BY div.Division, a.DivCode;

-- Cleanup. Tempdb auto-cleans on session end too, but explicit DROP
-- keeps the session footprint tight when the same connection is reused.
DROP TABLE #BoxUsability, #QB, #BoxRows, #ItemDivLs, #ItemDivUpc, #ItemDiv;";

        return await ExecAsync(db, sql, ReadDivisionSummary, ct, new Dictionary<string, object>
        {
            ["@batchNo"] = batchNo,
            ["@country"] = b.Country,
            ["@y"]       = b.RunYear,
            ["@m"]       = b.RunMonth,
            ["@minPct"]  = (object?)minBoxUsabilityPct ?? DBNull.Value,
            ["@maxPct"]  = (object?)maxBoxUsabilityPct ?? DBNull.Value,
        });
    }

    private static DivisionSummaryRow ReadDivisionSummary(SqlDataReader r) => new()
    {
        LPMBatchNo    = r.GetInt64(0),
        DivCode       = r.IsDBNull(1) ? 0 : r.GetInt32(1),
        DivisionName  = r.IsDBNull(2) ? "" : r.GetString(2),
        EOM           = r.IsDBNull(3) ? 0 : r.GetDecimal(3),
        SOH           = r.IsDBNull(4) ? 0 : r.GetInt64(4),
        MerchNeedWeek = r.IsDBNull(5) ? 0 : r.GetInt64(5),
        SimQty        = r.IsDBNull(6) ? 0 : r.GetInt64(6),
        RrQty         = r.IsDBNull(7) ? 0 : r.GetInt64(7),
        OverrideQty   = r.IsDBNull(8) ? 0 : r.GetInt64(8),
        BoxQty        = r.IsDBNull(9) ? 0 : r.GetInt64(9),
    };

    private static StoreSummaryRow ReadStoreSummary(SqlDataReader r) => new()
    {
        LPMBatchNo    = r.GetInt64(0),
        StoreID       = r.IsDBNull(1) ? "" : r.GetString(1),
        StoreName     = r.IsDBNull(2) ? "" : r.GetString(2),
        EOM           = r.IsDBNull(3) ? 0 : r.GetDecimal(3),
        SOH           = r.IsDBNull(4) ? 0 : r.GetInt64(4),
        MerchNeedWeek = r.IsDBNull(5) ? 0 : r.GetInt64(5),
        SimQty        = r.IsDBNull(6) ? 0 : r.GetInt64(6),
        RrQty         = r.IsDBNull(7) ? 0 : r.GetInt64(7),
        OverrideQty   = r.IsDBNull(8) ? 0 : r.GetInt64(8),
    };

    public async Task<List<BoxDetailRow>> GetBoxDetailsAsync(long batchNo, bool rollupToBoxOnly, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // 1.14.61 — Country-aware whboxitems source. Look up the batch's
        // country once (defensive default UAE if the batch row is missing).
        var batchCountry = await db.LpmSimBatches.AsNoTracking()
            .Where(b => b.LPMBatchNo == batchNo)
            .Select(b => b.Country)
            .FirstOrDefaultAsync(ct) ?? "UAE";
        var whSrc = await ResolveWhSrcAsync(db, batchCountry, ct);

        var sql = rollupToBoxOnly
            ? $@"
-- 1.14.72 — Two-pronged meta lookup. Some pallets in whboxitems carry an
-- EMPTY BoxNo and only a PalletNo (lone-pallet entries). The allocator
-- writes whatever BoxNo it found into LPMSIM_Output.BoxNo verbatim — so
-- those rows land in LPMSIM_Output with BoxNo = '' (or NULL) and PalletNo
-- = 'PLT…'. Pre-1.14.72 the report GROUP BY collapsed every empty-BoxNo
-- row into one bucket and MAX(PalletType) returned an alphabetically-
-- arbitrary value (e.g. 'XM' over 'R1' for PLT571291B).
--
-- New rule (per operator):
--   • LPMSIM_Output.BoxNo <> '' → look up whboxitems WHERE BoxNo = LPMSIM_Output.BoxNo
--     (PalletNo also matched to disambiguate multi-pallet boxes — 1.14.70 rule)
--   • LPMSIM_Output.BoxNo  = '' → look up whboxitems WHERE BoxNo = LPMSIM_Output.PalletNo
--     (the fallback the operator documented — empty-BoxNo rows in
--      whboxitems whose BoxNo column carries the PalletNo string)
--
-- BoxAgg is now grouped by (BoxNo, PalletNo) so empty-BoxNo rows with
-- different PalletNos no longer collapse together.
WITH BoxAgg AS (
    SELECT s.LPMBatchNo, s.BoxNo, s.PalletNo,
           SUM(CAST(s.Qty AS bigint)) AS LpmSimQty,
           SUM(CASE WHEN s.IsRoundRobin = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END) AS RoundRobinQty
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.LPMBatchNo, s.BoxNo, s.PalletNo
),
-- 1.14.72 — Combined per-(BoxNo, PalletNo) meta. UNION ALL of the two
-- lookup branches (non-empty-BoxNo + empty-BoxNo fallback) so each branch
-- runs as its own optimisable SQL plan instead of forcing the engine to
-- evaluate an OR in a JOIN predicate.
BoxMeta AS (
    -- Branch A — LPMSIM_Output.BoxNo is non-empty.
    -- Join whboxitems.BoxNo = LPMSIM_Output.BoxNo AND match the PalletNo
    -- too (1.14.70 disambiguation for the multi-pallet-per-BoxNo case).
    SELECT b.BoxNo, b.PalletNo,
           SUM(CAST(w.Qty AS bigint))                                            AS BoxQty,
           MAX(w.TrnDate)                                                        AS TrnDate,
           MAX(w.Warehouse)                                                      AS Warehouse,
           MAX(w.Rack)                                                           AS Rack,
           MAX(w.ToteId)                                                         AS ToteId,
           MAX(w.PalletType)                                                     AS PalletType,
           MAX(w.PurDate)                                                        AS PurDate,
           MAX(w.GINNo)                                                          AS GINNo,
           MAX(w.GinDate)                                                        AS GinDate,
           MAX(w.FromTo)                                                         AS FromTo,
           MAX(w.ContNo)                                                         AS ContNo,
           MAX(w.LPMDt)                                                          AS LpmDate,
           CASE WHEN MAX(CASE WHEN w.LPMDt IS NOT NULL THEN 1 ELSE 0 END) = 1
                THEN 'LPM' ELSE 'Non-LPM' END                                    AS BoxKind
      FROM BoxAgg b
      INNER JOIN {whSrc} w
              ON w.BoxNo = b.BoxNo
             AND ISNULL(w.PalletNo, '') = ISNULL(b.PalletNo, '')
     WHERE ISNULL(b.BoxNo, '') <> ''
     GROUP BY b.BoxNo, b.PalletNo
    UNION ALL
    -- Branch B — LPMSIM_Output.BoxNo is empty.
    -- Fall back to matching whboxitems.BoxNo = LPMSIM_Output.PalletNo.
    SELECT b.BoxNo, b.PalletNo,
           SUM(CAST(w.Qty AS bigint))                                            AS BoxQty,
           MAX(w.TrnDate)                                                        AS TrnDate,
           MAX(w.Warehouse)                                                      AS Warehouse,
           MAX(w.Rack)                                                           AS Rack,
           MAX(w.ToteId)                                                         AS ToteId,
           MAX(w.PalletType)                                                     AS PalletType,
           MAX(w.PurDate)                                                        AS PurDate,
           MAX(w.GINNo)                                                          AS GINNo,
           MAX(w.GinDate)                                                        AS GinDate,
           MAX(w.FromTo)                                                         AS FromTo,
           MAX(w.ContNo)                                                         AS ContNo,
           MAX(w.LPMDt)                                                          AS LpmDate,
           CASE WHEN MAX(CASE WHEN w.LPMDt IS NOT NULL THEN 1 ELSE 0 END) = 1
                THEN 'LPM' ELSE 'Non-LPM' END                                    AS BoxKind
      FROM BoxAgg b
      INNER JOIN {whSrc} w ON w.BoxNo = b.PalletNo
     WHERE ISNULL(b.BoxNo, '') = ''
     GROUP BY b.BoxNo, b.PalletNo
),
-- 1.14.65 — Per-box Division (TOP-1 reduction). 1.14.72 — same two-branch
-- shape as BoxMeta so the empty-BoxNo fallback also resolves the Division.
BoxDiv AS (
    -- Branch A — non-empty BoxNo
    SELECT b.BoxNo, b.PalletNo,
           MIN(d.DivCode)  AS DivCode,
           MAX(d.Division) AS DivisionName
      FROM BoxAgg b
      INNER JOIN {whSrc} w
              ON w.BoxNo = b.BoxNo
             AND ISNULL(w.PalletNo, '') = ISNULL(b.PalletNo, '')
      INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = w.ItemCode
      INNER JOIN Datareporting.dbo.subclassmaster sm  ON sm.MH4ID   = u.MH4ID
      INNER JOIN LPMSIM.dbo.Division               d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                                                     AND d.IsActive = 1
     WHERE ISNULL(b.BoxNo, '') <> ''
     GROUP BY b.BoxNo, b.PalletNo
    UNION ALL
    -- Branch B — empty BoxNo, fall back to PalletNo
    SELECT b.BoxNo, b.PalletNo,
           MIN(d.DivCode)  AS DivCode,
           MAX(d.Division) AS DivisionName
      FROM BoxAgg b
      INNER JOIN {whSrc} w ON w.BoxNo = b.PalletNo
      INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = w.ItemCode
      INNER JOIN Datareporting.dbo.subclassmaster sm  ON sm.MH4ID   = u.MH4ID
      INNER JOIN LPMSIM.dbo.Division               d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                                                     AND d.IsActive = 1
     WHERE ISNULL(b.BoxNo, '') = ''
     GROUP BY b.BoxNo, b.PalletNo
),
-- 1.14.80 — For every BoxNo in the current batch, find the most recent
-- Approved batch (in the same country, other than this batch) that also
-- contained that BoxNo. Surfaces ''this box already shipped in batch #N,
-- why is it back?'' reuse cases to the planner. NULL when the box has
-- never been allocated in any earlier Approved batch.
RecentApprovedBatchByBox AS (
    SELECT o.BoxNo, MAX(o.LPMBatchNo) AS RecentBatchNo
      FROM dbo.LPMSIM_Output o
      INNER JOIN dbo.LPMSIM_Batch b ON b.LPMBatchNo = o.LPMBatchNo
     WHERE b.Status = 'Approved'
       AND b.LPMBatchNo <> @batchNo
       AND b.Country   = @batchCountry
       AND ISNULL(o.BoxNo, '') <> ''
     GROUP BY o.BoxNo
)
SELECT b.LPMBatchNo,
       NULL AS StoreID, NULL AS StoreName,
       bd.DivCode,
       bd.DivisionName,
       b.BoxNo,
       b.PalletNo,
       bm.BoxQty,
       b.LpmSimQty,
       bm.PalletType,
       pt.TypeName,
       pt.PalletCategory,
       bm.TrnDate,
       bm.Warehouse,
       bm.Rack,
       b.RoundRobinQty,
       bm.BoxKind,
       bm.ToteId,
       bm.PurDate,
       bm.GINNo,
       bm.GinDate,
       bm.FromTo,
       bm.ContNo,           -- 1.14.80 — Container number from whboxitems
       rab.RecentBatchNo,   -- 1.14.80 — Most recent Approved batch with this BoxNo (NULL if none)
       bm.LpmDate           -- 1.14.81 — Raw LPMDt (pair with BoxKind: non-NULL ⇒ LPM)
  FROM BoxAgg b
  LEFT JOIN BoxMeta bm
         ON ISNULL(bm.BoxNo, '')   = ISNULL(b.BoxNo, '')
        AND ISNULL(bm.PalletNo, '') = ISNULL(b.PalletNo, '')
  LEFT JOIN BoxDiv bd
         ON ISNULL(bd.BoxNo, '')   = ISNULL(b.BoxNo, '')
        AND ISNULL(bd.PalletNo, '') = ISNULL(b.PalletNo, '')
  LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = bm.PalletType
  LEFT JOIN RecentApprovedBatchByBox rab ON rab.BoxNo = b.BoxNo
 ORDER BY b.BoxNo, b.PalletNo;"
            : $@"
WITH ItemDiv AS (
    -- subclassmaster.DivID (401..) does not match LPMSIM Division.DivCode (1..).
    -- Map by the human-readable Division name to get the LPMSIM DivCode.
    --
    -- DISTINCT prevents row-multiplication when an itemcode has multiple
    -- alternate-barcode rows in upc_subclass that all map to the same DivCode.
    -- MIN(DivCode) collapses the rare case where one itemcode resolves to
    -- multiple DivCodes (data-quality issue in subclassmaster); we pick a
    -- single deterministic Division so totals add up to LPMSIM_Output exactly.
    SELECT u.itemcode, MIN(d.DivCode) AS DivID
      FROM Datareporting.dbo.upc_subclass u
      INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
      INNER JOIN LPMSIM.dbo.Division d
              ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
     GROUP BY u.itemcode
),
SimAgg AS (
    -- 1.14.70 / 1.14.72: project the allocator's PalletNo so the per-pallet
    -- TOP-1 subqueries below can target the SPECIFIC physical pallet AND
    -- the MatchKey can switch between BoxNo (when populated) and PalletNo
    -- (when BoxNo is empty in LPMSIM_Output).
    -- 1.14.72: GROUP BY now includes PalletNo so empty-BoxNo rows with
    -- different PalletNos don't collapse into a single combined row.
    SELECT s.LPMBatchNo, s.StoreID, id.DivID AS DivCode, s.BoxNo, s.PalletNo,
           -- 1.14.72 — Picks which whboxitems column the lookups join to:
           --   • BoxNo non-empty → match w.BoxNo = sa.BoxNo (legacy path)
           --   • BoxNo empty     → match w.BoxNo = sa.PalletNo (fallback)
           CASE WHEN ISNULL(s.BoxNo, '') = '' THEN s.PalletNo ELSE s.BoxNo END AS MatchKey,
           SUM(CAST(s.Qty AS bigint)) AS LpmSimQty
      FROM dbo.LPMSIM_Output s
      INNER JOIN ItemDiv id ON id.itemcode = s.Itemcode
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.LPMBatchNo, s.StoreID, id.DivID, s.BoxNo, s.PalletNo
)
SELECT sa.LPMBatchNo,
       sa.StoreID,
       MAX(ds.PBFullname) AS StoreName,
       sa.DivCode,
       MAX(div.Division)  AS DivisionName,
       sa.BoxNo,
       sa.PalletNo,                                              -- 1.14.70: from LPMSIM_Output (deterministic)
       (SELECT SUM(CAST(Qty AS bigint)) FROM {whSrc} w WHERE w.BoxNo = sa.MatchKey) AS BoxQty,
       sa.LpmSimQty,
       -- 1.14.70 / 1.14.72 — per-pallet lookups join on MatchKey (BoxNo or
       -- PalletNo per the rule above). When BoxNo is non-empty we ALSO
       -- disambiguate by PalletNo so a multi-pallet-per-box BoxNo picks
       -- the right pallet (the PLT571291B 1.14.70 fix). When BoxNo is
       -- empty, MatchKey = PalletNo so the PalletNo disambiguation is
       -- implicit — the extra predicate is bypassed by the OR.
       (SELECT TOP 1 PalletType FROM {whSrc} w
         WHERE w.BoxNo = sa.MatchKey
           AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))) AS PalletType,
       (SELECT TOP 1 pt.TypeName       FROM {whSrc} w
          LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
         WHERE w.BoxNo = sa.MatchKey
           AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))) AS TypeName,
       (SELECT TOP 1 pt.PalletCategory FROM {whSrc} w
          LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
         WHERE w.BoxNo = sa.MatchKey
           AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))) AS PalletCategory,
       -- 1.14.65 — Pad to align with the rollup branch's column positions
       -- so the shared ReadBoxDetail reader maps fields correctly. The
       -- non-rollup mode doesn't surface these extras in the UI, but the
       -- positional alignment lets ToteId land at index 17 cleanly.
       CAST(NULL AS datetime) AS TrnDate,
       CAST(NULL AS nvarchar(50)) AS Warehouse,
       CAST(NULL AS nvarchar(50)) AS Rack,
       CAST(0    AS bigint)       AS RoundRobinQty,
       -- 1.14.81 — BoxKind was a NULL placeholder pre-1.14.81. Now computed:
       -- if any matching whboxitems row has a non-NULL LPMDt for this pallet,
       -- the box is ''LPM''; otherwise ''Non-LPM''. Same rule as the rollup CTE.
       CASE WHEN EXISTS (
                SELECT 1 FROM {whSrc} w
                 WHERE w.BoxNo = sa.MatchKey
                   AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))
                   AND w.LPMDt IS NOT NULL)
            THEN 'LPM' ELSE 'Non-LPM' END AS BoxKind,
       (SELECT TOP 1 ToteId FROM {whSrc} w WHERE w.BoxNo = sa.MatchKey) AS ToteId,
       -- 1.14.70 — new columns from whboxitems / WHBoxItemsExport. All
       -- pallet-level attributes so they're matched on (BoxNo, PalletNo)
       -- in the non-empty branch and just (MatchKey=PalletNo) in the
       -- empty-BoxNo branch.
       (SELECT TOP 1 PurDate FROM {whSrc} w
         WHERE w.BoxNo = sa.MatchKey
           AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))) AS PurDate,
       (SELECT TOP 1 GINNo   FROM {whSrc} w
         WHERE w.BoxNo = sa.MatchKey
           AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))) AS GINNo,
       (SELECT TOP 1 GinDate FROM {whSrc} w
         WHERE w.BoxNo = sa.MatchKey
           AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))) AS GinDate,
       (SELECT TOP 1 FromTo  FROM {whSrc} w
         WHERE w.BoxNo = sa.MatchKey
           AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))) AS FromTo,
       -- 1.14.80 — ContNo (pallet-level container number) + RecentBatchNo
       -- (most recent Approved batch in same country containing this BoxNo,
       -- other than the current one). Both NULL when not applicable.
       (SELECT TOP 1 ContNo  FROM {whSrc} w
         WHERE w.BoxNo = sa.MatchKey
           AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))) AS ContNo,
       (SELECT MAX(o.LPMBatchNo)
          FROM dbo.LPMSIM_Output o
          INNER JOIN dbo.LPMSIM_Batch bb ON bb.LPMBatchNo = o.LPMBatchNo
         WHERE bb.Status = 'Approved'
           AND bb.LPMBatchNo <> @batchNo
           AND bb.Country   = @batchCountry
           AND o.BoxNo      = sa.BoxNo
           AND ISNULL(sa.BoxNo, '') <> '') AS RecentBatchNo,
       -- 1.14.81 — Per-pallet LPMDt (paired with BoxKind). NULL ⇒ Non-LPM box.
       (SELECT TOP 1 LPMDt FROM {whSrc} w
         WHERE w.BoxNo = sa.MatchKey
           AND (ISNULL(sa.BoxNo, '') = '' OR ISNULL(w.PalletNo, '') = ISNULL(sa.PalletNo, ''))) AS LpmDate
  FROM SimAgg sa
  LEFT JOIN dbo.DataSettings ds ON ds.StoreID = sa.StoreID
  LEFT JOIN dbo.Division div ON div.DivCode = sa.DivCode
 GROUP BY sa.LPMBatchNo, sa.StoreID, sa.DivCode, sa.BoxNo, sa.PalletNo, sa.MatchKey, sa.LpmSimQty
 ORDER BY MAX(div.Division), sa.StoreID, sa.BoxNo;";

        return await ExecAsync(db, sql, ReadBoxDetail, ct, new Dictionary<string, object>
        {
            ["@batchNo"]      = batchNo,
            ["@batchCountry"] = batchCountry,
        });
    }

    /// <summary>
    /// Single-row aggregate used by the Generate page metric cards. Includes
    /// LPM / Non-LPM split: LPM = rows where Phase starts with "P1"; Non-LPM
    /// = rows where Phase starts with "P2".
    /// </summary>
    public async Task<BatchAggregates> GetBatchAggregatesAsync(
        long batchNo,
        decimal? minBoxUsabilityPct = null,
        decimal? maxBoxUsabilityPct = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // 1.14.61 — Country-aware whboxitems source for the BoxQty subquery.
        var batchCountry = await db.LpmSimBatches.AsNoTracking()
            .Where(b => b.LPMBatchNo == batchNo)
            .Select(b => b.Country)
            .FirstOrDefaultAsync(ct) ?? "UAE";
        var whSrc = await ResolveWhSrcAsync(db, batchCountry, ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        // Box-usability filter (same CTE rule used by Summary / Item Details /
        // Store Summary / Trace) — keeps the source-split matrix aligned with
        // every other tab so SIM Qty totals reconcile byte-for-byte.
        cmd.CommandText = $@"
WITH BoxUsability AS (
    SELECT s.BoxNo,
           BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint))
                              FROM {whSrc} w
                             WHERE w.BoxNo = s.BoxNo), 0),
           SimQty = SUM(CAST(s.Qty AS bigint))
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.BoxNo
),
QualifyingBoxes AS (
    -- Usability rounded to 1 decimal so server-side filter agrees byte-for-byte
    -- with what the UI displays in the SIM Boxes tab (also rounded to 1dp).
    -- A box reading 50.0% passes when Min = 50; a box reading 49.9% fails.
    SELECT BoxNo
      FROM BoxUsability
     WHERE BoxQty > 0
       AND (@minPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) >= @minPct)
       AND (@maxPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) <= @maxPct)
)
SELECT
    LinesAll       = COUNT(*),
    StoresAll      = COUNT(DISTINCT s.StoreID),
    BoxesAll       = COUNT(DISTINCT s.BoxNo),
    QtyAll         = SUM(CAST(ISNULL(s.Qty,0) AS bigint)),

    LinesLpm       = SUM(CASE WHEN s.Phase LIKE 'P1%' THEN 1 ELSE 0 END),
    StoresLpm      = COUNT(DISTINCT CASE WHEN s.Phase LIKE 'P1%' THEN s.StoreID END),
    BoxesLpm       = COUNT(DISTINCT CASE WHEN s.Phase LIKE 'P1%' THEN s.BoxNo END),
    QtyLpm         = SUM(CASE WHEN s.Phase LIKE 'P1%' THEN CAST(ISNULL(s.Qty,0) AS bigint) ELSE 0 END),

    LinesNonLpm    = SUM(CASE WHEN s.Phase LIKE 'P2%' THEN 1 ELSE 0 END),
    StoresNonLpm   = COUNT(DISTINCT CASE WHEN s.Phase LIKE 'P2%' THEN s.StoreID END),
    BoxesNonLpm    = COUNT(DISTINCT CASE WHEN s.Phase LIKE 'P2%' THEN s.BoxNo END),
    QtyNonLpm      = SUM(CASE WHEN s.Phase LIKE 'P2%' THEN CAST(ISNULL(s.Qty,0) AS bigint) ELSE 0 END)
  FROM dbo.LPMSIM_Output s
  INNER JOIN QualifyingBoxes qb ON qb.BoxNo = s.BoxNo
 WHERE s.LPMBatchNo = @batchNo;";
        cmd.Parameters.Add(new SqlParameter("@batchNo", batchNo));
        cmd.Parameters.Add(new SqlParameter("@minPct", (object?)minBoxUsabilityPct ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@maxPct", (object?)maxBoxUsabilityPct ?? DBNull.Value));
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (await rdr.ReadAsync(ct))
        {
            int  lAll = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
            int  sAll = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
            int  bAll = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
            long qAll = rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3);
            int  lLpm = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
            int  sLpm = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5);
            int  bLpm = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6);
            long qLpm = rdr.IsDBNull(7) ? 0 : rdr.GetInt64(7);
            int  lNon = rdr.IsDBNull(8) ? 0 : rdr.GetInt32(8);
            int  sNon = rdr.IsDBNull(9) ? 0 : rdr.GetInt32(9);
            int  bNon = rdr.IsDBNull(10)? 0 : rdr.GetInt32(10);
            long qNon = rdr.IsDBNull(11)? 0 : rdr.GetInt64(11);
            return new BatchAggregates(lAll, sAll, bAll, qAll)
            {
                LpmLines     = lLpm, LpmStores     = sLpm, LpmBoxes     = bLpm, LpmQty     = qLpm,
                NonLpmLines  = lNon, NonLpmStores  = sNon, NonLpmBoxes  = bNon, NonLpmQty  = qNon,
            };
        }
        return new BatchAggregates(0, 0, 0, 0);
    }

    /// <summary>
    /// Per-(Kind × Warehouse) breakdown of a SIM batch — drives the
    /// "Allocation Result" matrix at the top of the Result Preview. Returns
    /// one row per (LPM/Non-LPM, Warehouse) pair that participated in the
    /// batch, with distinct-item / distinct-box counts, the warehouse-side
    /// stock that backed those boxes, and the SIM Qty allocated from them.
    ///
    /// Implementation: pre-aggregates <c>racks.dbo.whboxitems</c> ONCE for
    /// the batch's box set (mirrors the perf pattern used by the Item
    /// Details rewrite — no per-row correlated subqueries).
    /// </summary>
    public async Task<List<SourceWarehouseRow>> GetSourceWarehouseBreakdownAsync(
        long batchNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // 1.14.61 — Country-aware whboxitems source.
        var batchCountry = await db.LpmSimBatches.AsNoTracking()
            .Where(b => b.LPMBatchNo == batchNo)
            .Select(b => b.Country)
            .FirstOrDefaultAsync(ct) ?? "UAE";
        var whSrc = await ResolveWhSrcAsync(db, batchCountry, ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        var sql = $@"
;WITH BatchBoxes AS (
    SELECT DISTINCT BoxNo
      FROM dbo.LPMSIM_Output
     WHERE LPMBatchNo = @batchNo
),
-- Per BoxNo: warehouse, kind (LPM if any whboxitems row for that box has
-- a non-NULL LPMDt), and total warehouse stock for the box.
BoxMeta AS (
    SELECT w.BoxNo,
           Warehouse = MAX(ISNULL(NULLIF(LTRIM(RTRIM(w.Warehouse)), ''), '(none)')),
           Kind = CASE WHEN MAX(CASE WHEN w.LPMDt IS NOT NULL THEN 1 ELSE 0 END) = 1
                       THEN 'LPM' ELSE 'Non-LPM' END,
           BoxQty = SUM(CAST(w.Qty AS bigint))
      FROM {whSrc} w
     WHERE w.BoxNo IN (SELECT BoxNo FROM BatchBoxes)
     GROUP BY w.BoxNo
),
-- Per (Kind, Warehouse): distinct items + distinct boxes + SIM Qty.
SimByGroup AS (
    SELECT bm.Kind, bm.Warehouse,
           SkuCount = COUNT(DISTINCT s.Itemcode),
           BoxCount = COUNT(DISTINCT s.BoxNo),
           SimQty   = SUM(CAST(s.Qty AS bigint))
      FROM dbo.LPMSIM_Output s
      INNER JOIN BoxMeta bm ON bm.BoxNo = s.BoxNo
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY bm.Kind, bm.Warehouse
),
-- Per (Kind, Warehouse): warehouse stock total across the boxes used.
BoxQtyByGroup AS (
    SELECT Kind, Warehouse, BoxQty = SUM(BoxQty)
      FROM BoxMeta
     GROUP BY Kind, Warehouse
)
SELECT  sg.Kind,
        sg.Warehouse,
        sg.SkuCount,
        sg.BoxCount,
        BoxQty = ISNULL(bq.BoxQty, 0),
        sg.SimQty
  FROM  SimByGroup sg
  LEFT  JOIN BoxQtyByGroup bq
         ON bq.Kind = sg.Kind AND bq.Warehouse = sg.Warehouse
 ORDER BY sg.Kind, sg.Warehouse;";

        var rows = new List<SourceWarehouseRow>();
        using (var cmd = (SqlCommand)conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqlParameter("@batchNo", batchNo));
            cmd.CommandTimeout = 120;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new SourceWarehouseRow(
                    Kind:      rdr.IsDBNull(0) ? "Non-LPM" : rdr.GetString(0),
                    Warehouse: rdr.IsDBNull(1) ? "(unknown)" : rdr.GetString(1),
                    SkuCount:  rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2),
                    BoxCount:  rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3),
                    BoxQty:    rdr.IsDBNull(4) ? 0L : rdr.GetInt64(4),
                    SimQty:    rdr.IsDBNull(5) ? 0L : rdr.GetInt64(5)));
            }
        }
        return rows;
    }

    public async Task<List<ItemDetailRow>> GetItemDetailsAsync(
        long batchNo,
        decimal? minBoxUsabilityPct = null,
        decimal? maxBoxUsabilityPct = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var b = await db.LpmSimBatches.AsNoTracking().FirstOrDefaultAsync(x => x.LPMBatchNo == batchNo, ct);
        if (b is null) return new();
        // 1.14.61 — Country-aware whboxitems source.
        var whSrc = await ResolveWhSrcAsync(db, b.Country, ct);

        var sql = $@"
-- ── Item Details — perf rewrite ─────────────────────────────────────────
-- Pre-aggregate everything from the country-aware whboxitems source into
-- CTEs ONCE rather than the previous per-row subqueries that hit whboxitems
-- for each of the 116K LPMSIM_Output rows. On the UAE batch this dropped
-- the query from multi-second to sub-second territory. Restricting the
-- CTEs to BatchBoxes (= boxes that participated in this batch) keeps them
-- small.
;WITH BatchBoxes AS (
    SELECT DISTINCT BoxNo
      FROM dbo.LPMSIM_Output
     WHERE LPMBatchNo = @batchNo
),
BatchItems AS (
    SELECT DISTINCT Itemcode
      FROM dbo.LPMSIM_Output
     WHERE LPMBatchNo = @batchNo
),
-- One row per (BoxNo, ItemCode) — qty in this slot of the box. Replaces
-- the per-row correlated subquery (SELECT SUM(Qty) FROM whboxitems …)
-- in the previous version.
BoxAttrs AS (
    SELECT w.BoxNo, w.ItemCode,
           BoxItemQty = SUM(CAST(w.Qty AS bigint)),
           PalletNo   = MAX(w.PalletNo)                       -- 1.14.12
      FROM {whSrc} w
     WHERE w.BoxNo IN (SELECT BoxNo FROM BatchBoxes)
     GROUP BY w.BoxNo, w.ItemCode
),
-- One row per BoxNo — total qty + did-any-row-have-LPMDt? Drives Box
-- Usability filter AND the LPM/Non-LPM column. Replaces both the previous
-- BoxUsability per-box subquery AND the per-row EXISTS subquery for BoxKind.
BoxTotals AS (
    SELECT w.BoxNo,
           BoxQty   = SUM(CAST(w.Qty AS bigint)),
           AnyLpmDt = MAX(w.LPMDt)
      FROM {whSrc} w
     WHERE w.BoxNo IN (SELECT BoxNo FROM BatchBoxes)
     GROUP BY w.BoxNo
),
SimAggByBox AS (
    SELECT s.BoxNo, SimQty = SUM(CAST(s.Qty AS bigint))
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.BoxNo
),
QualifyingBoxes AS (
    -- Usability rounded to 1 decimal so server-side filter agrees byte-for-byte
    -- with what the UI displays in the SIM Boxes tab (also rounded to 1dp).
    -- A box reading 50.0% passes when Min = 50; a box reading 49.9% fails.
    SELECT bt.BoxNo
      FROM BoxTotals bt
      INNER JOIN SimAggByBox sa ON sa.BoxNo = bt.BoxNo
     WHERE bt.BoxQty > 0
       AND (@minPct IS NULL OR ROUND(CAST(sa.SimQty AS decimal(20,4)) * 100 / bt.BoxQty, 1) >= @minPct)
       AND (@maxPct IS NULL OR ROUND(CAST(sa.SimQty AS decimal(20,4)) * 100 / bt.BoxQty, 1) <= @maxPct)
),
-- ItemDiv: LocStock-first, upc_subclass fallback. Mirrors what the
-- allocator uses in LpmSimGenerator (LPM_LocStock.DivCode is the
-- denormalised, daily-ETL'd source of truth; upc_subclass × subclassmaster
-- × Division is only used for items that aren't yet stocked anywhere in
-- the country).
ItemDivLs AS (
    SELECT bi.Itemcode, DivCode = MIN(ls.DivCode)
      FROM BatchItems bi
      INNER JOIN racks.dbo.LPM_LocStock ls
              ON ls.Itemcode = bi.Itemcode
             AND ls.Country  = @country
             AND ls.DivCode IS NOT NULL
     GROUP BY bi.Itemcode
),
ItemDivUpc AS (
    SELECT bi.Itemcode, DivCode = MIN(d.DivCode)
      FROM BatchItems bi
      INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = bi.Itemcode
      INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID   = u.MH4ID
      INNER JOIN LPMSIM.dbo.Division               d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
     WHERE bi.Itemcode NOT IN (SELECT Itemcode FROM ItemDivLs)
     GROUP BY bi.Itemcode
),
ItemDiv AS (
    SELECT Itemcode, DivCode FROM ItemDivLs
    UNION ALL
    SELECT Itemcode, DivCode FROM ItemDivUpc
),
SohAgg AS (
    SELECT ls.StoreID, ls.Itemcode, SUM(ISNULL(ls.SOH,0)) AS SOH
      FROM racks.dbo.LPM_LocStock ls
      INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
     WHERE ds.SIMCountry = @country
       AND ls.StoreID  IS NOT NULL AND ls.StoreID  <> ''
       AND ls.Itemcode IS NOT NULL AND ls.Itemcode <> ''
     GROUP BY ls.StoreID, ls.Itemcode
),
SkuMaxAgg AS (
    SELECT StoreID, ItemCode, MAX(SKUMax) AS SKUMax
      FROM dbo.LPM_SimItemSkuMax
     WHERE Country = @country AND Year1 = @y AND Month1 = @m
     GROUP BY StoreID, ItemCode
)
SELECT s.LPMBatchNo,
       s.StoreID,
       ds.PBFullname           AS StoreName,
       id.DivCode              AS DivCode,
       div.Division            AS DivisionName,
       s.BoxNo,
       ba.PalletNo,                                         -- 1.14.12
       s.Itemcode,
       ba.BoxItemQty,
       sk.SKUMax,
       ISNULL(soh.SOH, 0)      AS SOH,
       s.Qty                   AS LpmQty,
       CASE WHEN s.IsRoundRobin = 1 THEN s.Qty ELSE 0 END AS RoundRobinQty,
       s.Phase                 AS Phase,
       CASE WHEN bt.AnyLpmDt IS NOT NULL THEN 'LPM' ELSE 'Non-LPM' END AS BoxKind,
       eo.PriorityRank
  FROM dbo.LPMSIM_Output s
  INNER JOIN ItemDiv id         ON id.Itemcode = s.Itemcode
  INNER JOIN QualifyingBoxes qb ON qb.BoxNo    = s.BoxNo
  LEFT  JOIN BoxAttrs ba        ON ba.BoxNo    = s.BoxNo  AND ba.ItemCode = s.Itemcode
  LEFT  JOIN BoxTotals bt       ON bt.BoxNo    = s.BoxNo
  -- One DataSettings row per Store (defensive against duplicate StoreID
  -- entries in bfldata.dbo.DataSettings — would otherwise multiply rows).
  OUTER APPLY (
      SELECT TOP 1 PBFullname FROM dbo.DataSettings d
       WHERE d.StoreID = s.StoreID
         AND (d.SIMCountry = @country OR d.SIMCountry IS NULL)
  ) ds
  LEFT  JOIN dbo.Division div   ON div.DivCode = id.DivCode
  LEFT  JOIN SkuMaxAgg sk       ON sk.StoreID  = s.StoreID  AND sk.ItemCode = s.Itemcode
  LEFT  JOIN SohAgg soh         ON soh.StoreID = s.StoreID  AND soh.Itemcode = s.Itemcode
  -- LPM_EOM_Output gives us the per-(Store, Div) priority rank — surfaced
  -- in the report so the planner can spot rank vs allocation anomalies.
  LEFT  JOIN dbo.LPM_EOM_Output eo
         ON eo.Country = @country AND eo.Year1 = @y AND eo.Month1 = @m
        AND eo.StoreID = s.StoreID AND eo.DivCode = id.DivCode
 WHERE s.LPMBatchNo = @batchNo
 ORDER BY div.Division, s.StoreID, s.BoxNo, s.Itemcode;";

        return await ExecAsync(db, sql, ReadItemDetail, ct, new Dictionary<string, object>
        {
            ["@batchNo"] = batchNo,
            ["@country"] = b.Country,
            ["@y"]       = b.RunYear,
            ["@m"]       = b.RunMonth,
            ["@minPct"]  = (object?)minBoxUsabilityPct ?? DBNull.Value,
            ["@maxPct"]  = (object?)maxBoxUsabilityPct ?? DBNull.Value,
        });
    }

    /// <summary>
    /// Distinct warehouse codes from the country-aware whboxitems source.
    /// Used to populate the multi-select on the SIM Generate page so the
    /// planner can scope a run to one or more warehouses.
    /// 1.14.61 — Optional <paramref name="country"/> parameter routes the
    /// query to the right source (UAE → <c>racks.dbo.whboxitems</c>; others
    /// → <c>[&lt;DataName&gt;].dbo.WHBoxItemsExport</c>). Defaults to "UAE"
    /// so existing callers that don't pass a country keep the legacy behaviour.
    /// </summary>
    public async Task<List<string>> GetDistinctWarehousesAsync(string country = "UAE", CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var whSrc = await ResolveWhSrcAsync(db, country, ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT DISTINCT Warehouse
              FROM {whSrc}
             WHERE Warehouse IS NOT NULL AND Warehouse <> ''
             ORDER BY Warehouse;";
        cmd.CommandTimeout = 60;
        var list = new List<string>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            if (!rdr.IsDBNull(0)) list.Add(rdr.GetString(0));
        return list;
    }

    /// <summary>
    /// Lightweight summary of one SIM batch — used by the result preview's
    /// "Other batches for this period" pill list so the planner can switch
    /// between Draft and any Approved batches without changing pages.
    /// </summary>
    public sealed record BatchListEntry(
        long LPMBatchNo, string Status, DateTime CreateTS, string CreatedBy,
        string? Sources, int? OverrideUsabilityPct, string? FillStrategy,
        DateTime? ApprovedTS, string? ApprovedBy);

    /// <summary>
    /// All batches for a (Country, RunDate) ordered newest first. Empty list
    /// when no batches exist. Multiple Approved batches are now allowed
    /// (since the GenerateAsync logic was relaxed to keep Approved ones).
    /// </summary>
    public async Task<List<BatchListEntry>> GetBatchesForPeriodAsync(
        string country, DateTime runDate,
        // 1.14.67 — Optional current-user filter so "Running" batches from
        // other users don't show up in the SIM Generate page's per-period
        // pill list while their generation is in flight.
        string? currentUser = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.LpmSimBatches.AsNoTracking()
            .Where(b => b.Country == country && b.RunDate == runDate.Date);
        var staleCutoff = DateTime.Now.AddMinutes(-30);
        if (!string.IsNullOrEmpty(currentUser))
        {
            q = q.Where(b => b.Status != "Running"
                          || (b.CreatedBy == currentUser && b.CreateTS >= staleCutoff));
        }
        return await q
            .OrderByDescending(b => b.LPMBatchNo)
            .Select(b => new BatchListEntry(
                b.LPMBatchNo, b.Status ?? "", b.CreateTS, b.CreatedBy ?? "",
                b.Sources, b.OverrideUsabilityPct, b.FillStrategy,
                b.ApprovedTS, b.ApprovedBy))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Distinct LPM months from the country-aware whboxitems source's
    /// <c>LPMDt</c> column, returned as month-start
    /// <see cref="DateTime"/> values (1st of each month). Feeds the SIM
    /// Generate "LPM Months" multi-select so the planner can scope an LPM
    /// run to specific months instead of the default "all months up to the
    /// run period". Empty selection on the page = legacy behaviour.
    /// 1.14.61 — Optional <paramref name="country"/> parameter routes the
    /// query to the right source (UAE → <c>racks.dbo.whboxitems</c>; others
    /// → <c>[&lt;DataName&gt;].dbo.WHBoxItemsExport</c>). Defaults to "UAE".
    /// </summary>
    public async Task<List<DateTime>> GetDistinctLpmMonthsAsync(string country = "UAE", CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var whSrc = await ResolveWhSrcAsync(db, country, ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT DISTINCT DATEFROMPARTS(YEAR(LPMDt), MONTH(LPMDt), 1) AS MonthStart
              FROM {whSrc}
             WHERE LPMDt IS NOT NULL
             ORDER BY MonthStart;";
        cmd.CommandTimeout = 60;
        var list = new List<DateTime>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            if (!rdr.IsDBNull(0)) list.Add(rdr.GetDateTime(0));
        return list;
    }

    /// <summary>
    /// 1.14.91 — Distinct container numbers purchased on the current server-
    /// local date, sourced from <c>usa.dbo.USAPurchase.ContNo</c>. Drives the
    /// "Containers Purchased Today" panel near the Input Readiness grid on
    /// SIM Generate so the planner can spot which containers landed in
    /// today's purchase run before kicking off SIM.
    ///
    /// <para>
    /// The reference SQL the planner gave:
    /// <code>SELECT DISTINCT ContNo FROM usa..USAPurchase WHERE Trndate = todays date</code>
    /// </para>
    ///
    /// <para>
    /// Implementation uses a half-open <c>[today, tomorrow)</c> range
    /// instead of equality so the query works whether <c>Trndate</c> is
    /// stored as <c>date</c> or <c>datetime</c> (avoids the
    /// "datetime with a non-midnight time" trap). NULL / blank ContNo
    /// rows are skipped. Order: ContNo ASC (stable list for the UI).
    /// </para>
    ///
    /// <para>
    /// "Today" = server-local date (<c>DateTime.Today</c>) per the planner's
    /// pick — could drift from GST by a few hours around midnight if the
    /// server is on UTC, but matches the literal raw SQL the planner runs.
    /// </para>
    /// </summary>
    public async Task<List<string>> GetContainersPurchasedTodayAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT ContNo
              FROM usa.dbo.USAPurchase
             WHERE Trndate >= @today AND Trndate < @tomorrow
               AND ContNo IS NOT NULL
               AND LTRIM(RTRIM(ContNo)) <> ''
             ORDER BY ContNo;";
        var today = DateTime.Today;
        cmd.Parameters.Add(new SqlParameter("@today",    today));
        cmd.Parameters.Add(new SqlParameter("@tomorrow", today.AddDays(1)));
        cmd.CommandTimeout = 30;
        var list = new List<string>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            if (!rdr.IsDBNull(0)) list.Add(rdr.GetString(0).Trim());
        return list;
    }

    /// <summary>
    /// Distinct PalletCategory values from <c>bfldata.dbo.pallettype</c> —
    /// fed into the SIM Generate "Pallet Categories" multi-select so the
    /// planner can pick which categories enter SIM. Default selection on
    /// the page is just "ELIGIBLE" (matches legacy behaviour).
    /// </summary>
    public async Task<List<string>> GetDistinctPalletCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT PalletCategory
              FROM bfldata.dbo.pallettype
             WHERE PalletCategory IS NOT NULL AND PalletCategory <> ''
             ORDER BY PalletCategory;";
        cmd.CommandTimeout = 60;
        var list = new List<string>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            if (!rdr.IsDBNull(0)) list.Add(rdr.GetString(0));
        return list;
    }

    /// <summary>
    /// Counts of trace rows by Decision PLUS the qty side of the ledger:
    /// for ALLOC / ALLOC_RR we sum the Take qty; for SKIP_NO_DIV / SKIP_NO_EOM
    /// we sum LineQty (the qty discarded because the line could not be placed).
    /// SKIP_SKUMAX / SKIP_TARGET produce per-store-attempt rows that don't
    /// correspond to qty (the same line will retry on the next store).
    /// </summary>
    public async Task<AllocTraceCounts> GetAllocTraceCountsAsync(long batchNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Decision,
                   COUNT(*)              AS Cnt,
                   SUM(CAST(Take    AS bigint)) AS TakeQty,
                   SUM(CAST(LineQty AS bigint)) AS LineQty
              FROM dbo.LPMSIM_AllocTrace
             WHERE LPMBatchNo = @batchNo
             GROUP BY Decision;";
        cmd.Parameters.Add(new SqlParameter("@batchNo", batchNo));
        int alloc = 0, allocRr = 0, sm = 0, st = 0, nd = 0, ne = 0;
        long allocQty = 0, allocRrQty = 0, ndQty = 0, neQty = 0;
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var d = rdr.GetString(0);
            var c = rdr.GetInt32(1);
            var takeQty = rdr.IsDBNull(2) ? 0 : rdr.GetInt64(2);
            var lineQty = rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3);
            switch (d)
            {
                case "ALLOC":       alloc   = c; allocQty   = takeQty; break;
                case "ALLOC_RR":    allocRr = c; allocRrQty = takeQty; break;
                case "SKIP_SKUMAX": sm      = c;                       break;
                case "SKIP_TARGET": st      = c;                       break;
                case "SKIP_NO_DIV": nd      = c; ndQty      = lineQty; break;
                case "SKIP_NO_EOM": ne      = c; neQty      = lineQty; break;
            }
        }
        return new AllocTraceCounts(
            Alloc:              alloc,
            AllocRr:            allocRr,
            SkipSkuMax:         sm,
            SkipTarget:         st,
            SkipNoDiv:          nd,
            SkipNoEom:          ne,
            AllocQty:           allocQty,
            AllocRrQty:         allocRrQty,
            DiscardedNoDivQty:  ndQty,
            DiscardedNoEomQty:  neQty);
    }

    /// <summary>
    /// Per-decision trace rows for a batch with optional filters. Capped to 'top'
    /// rows so the UI doesn't try to render millions of rows in one go.
    /// </summary>
    public async Task<List<AllocTraceRow>> GetAllocTraceAsync(
        long batchNo,
        string? boxNo, string? itemCode, string? storeID, string? decision,
        decimal? minBoxUsabilityPct = null,
        decimal? maxBoxUsabilityPct = null,
        int top = 5000,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // 1.14.61 — Country-aware whboxitems source.
        var batchCountry = await db.LpmSimBatches.AsNoTracking()
            .Where(b => b.LPMBatchNo == batchNo)
            .Select(b => b.Country)
            .FirstOrDefaultAsync(ct) ?? "UAE";
        var whSrc = await ResolveWhSrcAsync(db, batchCountry, ct);
        var sql = $@"
WITH BoxUsability AS (
    SELECT s.BoxNo,
           BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint)) FROM {whSrc} w WHERE w.BoxNo = s.BoxNo), 0),
           SimQty = SUM(CAST(s.Qty AS bigint))
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.BoxNo
),
QualifyingBoxes AS (
    -- Usability rounded to 1 decimal so server-side filter agrees byte-for-byte
    -- with what the UI displays in the SIM Boxes tab (also rounded to 1dp).
    -- A box reading 50.0% passes when Min = 50; a box reading 49.9% fails.
    SELECT BoxNo
      FROM BoxUsability
     WHERE BoxQty > 0
       AND (@minPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) >= @minPct)
       AND (@maxPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) <= @maxPct)
)
SELECT TOP (@top)
       t.LPMBatchNo, t.BoxNo, t.ItemCode,
       t.DivCode, div.Division AS DivisionName,
       t.StoreID, ds.PBFullname AS StoreName,
       t.SKUMax, t.SOH_Item, t.SkuBalance,
       t.TargetEOM, t.DivSOH, t.AlreadyAllocated, t.TargetRemain,
       t.LineQty, t.Take, t.Decision, t.Phase
  FROM dbo.LPMSIM_AllocTrace t
  LEFT JOIN dbo.Division div ON div.DivCode = t.DivCode
  -- One DataSettings row per Store (defensive against duplicate StoreID
  -- entries — see Summary tab note).
  OUTER APPLY (
      SELECT TOP 1 PBFullname FROM dbo.DataSettings d
       WHERE d.StoreID = t.StoreID
         AND (d.SIMCountry IS NULL OR d.SIMCountry IN (SELECT Country FROM dbo.LPMSIM_Batch WHERE LPMBatchNo = @batchNo))
  ) ds
 WHERE t.LPMBatchNo = @batchNo
   AND (@minPct IS NULL AND @maxPct IS NULL OR t.BoxNo IN (SELECT BoxNo FROM QualifyingBoxes))
   {(string.IsNullOrWhiteSpace(boxNo)    ? "" : "AND t.BoxNo    LIKE @boxNo")}
   {(string.IsNullOrWhiteSpace(itemCode) ? "" : "AND t.ItemCode LIKE @itemCode")}
   {(string.IsNullOrWhiteSpace(storeID)  ? "" : "AND t.StoreID  = @storeID")}
   {(string.IsNullOrWhiteSpace(decision) ? "" : "AND t.Decision = @decision")}
 ORDER BY t.Id;";

        var parms = new Dictionary<string, object>
        {
            ["@batchNo"] = batchNo,
            ["@top"]     = top,
            ["@minPct"]  = (object?)minBoxUsabilityPct ?? DBNull.Value,
            ["@maxPct"]  = (object?)maxBoxUsabilityPct ?? DBNull.Value,
        };
        if (!string.IsNullOrWhiteSpace(boxNo))    parms["@boxNo"]    = $"%{boxNo}%";
        if (!string.IsNullOrWhiteSpace(itemCode)) parms["@itemCode"] = $"%{itemCode}%";
        if (!string.IsNullOrWhiteSpace(storeID))  parms["@storeID"]  = storeID;
        if (!string.IsNullOrWhiteSpace(decision)) parms["@decision"] = decision;

        return await ExecAsync(db, sql, ReadAllocTrace, ct, parms);
    }

    private static AllocTraceRow ReadAllocTrace(SqlDataReader r) => new()
    {
        LPMBatchNo       = r.GetInt64(0),
        BoxNo            = r.IsDBNull(1) ? "" : r.GetString(1),
        ItemCode         = r.IsDBNull(2) ? "" : r.GetString(2),
        DivCode          = r.IsDBNull(3) ? null : r.GetInt32(3),
        DivisionName     = r.IsDBNull(4) ? null : r.GetString(4),
        StoreID          = r.IsDBNull(5) ? null : r.GetString(5),
        StoreName        = r.IsDBNull(6) ? null : r.GetString(6),
        SKUMax           = r.IsDBNull(7) ? null : r.GetInt32(7),
        SOH_Item         = r.IsDBNull(8) ? null : r.GetInt32(8),
        SkuBalance       = r.IsDBNull(9) ? null : r.GetInt32(9),
        TargetEOM        = r.IsDBNull(10) ? null : r.GetDecimal(10),
        DivSOH           = r.IsDBNull(11) ? null : r.GetInt32(11),
        AlreadyAllocated = r.IsDBNull(12) ? null : r.GetDecimal(12),
        TargetRemain     = r.IsDBNull(13) ? null : r.GetDecimal(13),
        LineQty          = r.IsDBNull(14) ? 0 : r.GetInt32(14),
        Take             = r.IsDBNull(15) ? 0 : r.GetInt32(15),
        Decision         = r.IsDBNull(16) ? "" : r.GetString(16),
        Phase            = r.IsDBNull(17) ? "" : r.GetString(17),
    };

    // -- Readers --
    private static EomSummaryRow ReadEomSummary(SqlDataReader r) => new()
    {
        LPMBatchNo    = r.GetInt64(0),
        StoreID       = r.IsDBNull(1) ? "" : r.GetString(1),
        StoreName     = r.IsDBNull(2) ? "" : r.GetString(2),
        DivCode       = r.IsDBNull(3) ? 0  : r.GetInt32(3),
        DivisionName  = r.IsDBNull(4) ? "" : r.GetString(4),
        EOM           = r.IsDBNull(5) ? null : r.GetDecimal(5),
        SOH           = r.IsDBNull(6) ? 0 : Convert.ToInt32(r.GetValue(6)),
        MerchNeedWeek = r.IsDBNull(7) ? null : r.GetInt32(7),
        LpmSimQty     = r.IsDBNull(8) ? 0 : r.GetInt64(8),
        RoundRobinQty = r.IsDBNull(9) ? 0 : r.GetInt64(9),
        OverrideQty   = r.IsDBNull(10) ? 0 : r.GetInt64(10),
        PriorityRank  = r.IsDBNull(11) ? null : r.GetDecimal(11),
    };

    // 1.14.12: PalletNo column added at index 6; every subsequent index shifted by +1.
    private static BoxDetailRow ReadBoxDetail(SqlDataReader r) => new()
    {
        LPMBatchNo     = r.GetInt64(0),
        StoreID        = r.IsDBNull(1) ? null : r.GetString(1),
        StoreName      = r.IsDBNull(2) ? null : r.GetString(2),
        DivCode        = r.IsDBNull(3) ? null : r.GetInt32(3),
        DivisionName   = r.IsDBNull(4) ? null : r.GetString(4),
        BoxNo          = r.IsDBNull(5) ? "" : r.GetString(5),
        PalletNo       = r.IsDBNull(6) ? null : r.GetString(6),
        BoxQty         = r.IsDBNull(7) ? null : r.GetInt64(7),
        LpmSimQty      = r.IsDBNull(8) ? 0 : r.GetInt64(8),
        PalletType     = r.IsDBNull(9) ? null : r.GetString(9),
        TypeName       = r.IsDBNull(10) ? null : r.GetString(10),
        PalletCategory = r.IsDBNull(11) ? null : r.GetString(11),
        TrnDate        = r.FieldCount > 12 && !r.IsDBNull(12) ? r.GetDateTime(12) : null,
        Warehouse      = r.FieldCount > 13 && !r.IsDBNull(13) ? r.GetString(13)   : null,
        Rack           = r.FieldCount > 14 && !r.IsDBNull(14) ? r.GetString(14)   : null,
        RoundRobinQty  = r.FieldCount > 15 && !r.IsDBNull(15) ? r.GetInt64(15)    : 0,
        BoxKind        = r.FieldCount > 16 && !r.IsDBNull(16) ? r.GetString(16)   : null,
        // 1.14.65 — ToteId column appended at index 17. FieldCount guard
        // means callers reading an older SELECT (pre-1.14.65) still work.
        ToteId         = r.FieldCount > 17 && !r.IsDBNull(17) ? r.GetString(17)   : null,
        // 1.14.70 — PurDate / GINNo / GinDate / FromTo appended at 18-21.
        // FieldCount guards preserve forward-compat for any callers that
        // somehow run an older SELECT shape.
        PurDate        = r.FieldCount > 18 && !r.IsDBNull(18) ? r.GetDateTime(18) : null,
        GINNo          = r.FieldCount > 19 && !r.IsDBNull(19) ? r.GetString(19)   : null,
        GinDate        = r.FieldCount > 20 && !r.IsDBNull(20) ? r.GetDateTime(20) : null,
        FromTo         = r.FieldCount > 21 && !r.IsDBNull(21) ? r.GetString(21)   : null,
        // 1.14.80 — ContNo (22) + RecentBatchNo (23). FieldCount guards keep
        // older callers safe if they ever run an out-of-date SELECT.
        ContNo         = r.FieldCount > 22 && !r.IsDBNull(22) ? r.GetString(22)   : null,
        RecentBatchNo  = r.FieldCount > 23 && !r.IsDBNull(23) ? r.GetInt64(23)    : (long?)null,
        // 1.14.81 — LpmDate at index 24.
        LpmDate        = r.FieldCount > 24 && !r.IsDBNull(24) ? r.GetDateTime(24) : (DateTime?)null,
    };

    // 1.14.12: PalletNo column added at index 6; every subsequent index shifted by +1.
    private static ItemDetailRow ReadItemDetail(SqlDataReader r) => new()
    {
        LPMBatchNo    = r.GetInt64(0),
        StoreID       = r.IsDBNull(1) ? "" : r.GetString(1),
        StoreName     = r.IsDBNull(2) ? "" : r.GetString(2),
        DivCode       = r.IsDBNull(3) ? 0  : r.GetInt32(3),
        DivisionName  = r.IsDBNull(4) ? "" : r.GetString(4),
        BoxNo         = r.IsDBNull(5) ? "" : r.GetString(5),
        PalletNo      = r.IsDBNull(6) ? null : r.GetString(6),
        Itemcode      = r.IsDBNull(7) ? "" : r.GetString(7),
        BoxItemQty    = r.IsDBNull(8) ? null : r.GetInt64(8),
        SKUMax        = r.IsDBNull(9) ? null : r.GetInt32(9),
        SOH           = r.IsDBNull(10) ? 0 : Convert.ToInt32(r.GetValue(10)),
        LpmQty        = r.IsDBNull(11) ? 0 : r.GetInt32(11),
        RoundRobinQty = r.IsDBNull(12) ? 0 : r.GetInt32(12),
        Phase         = r.IsDBNull(13) ? null : r.GetString(13),
        BoxKind       = r.IsDBNull(14) ? "" : r.GetString(14),
        PriorityRank  = r.IsDBNull(15) ? null : r.GetDecimal(15),
    };

    private static async Task<List<T>> ExecAsync<T>(
        LpmDbContext db, string sql, Func<SqlDataReader, T> reader,
        CancellationToken ct, Dictionary<string, object> parameters)
    {
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = sql;
        // 1.14.6: bumped from 180s → 600s. The shared CommandTimeout
        // applies to every report query routed through this helper
        // (EOM Summary, Store Summary, Box Detail, Allocation Trace,
        // Custom Report). EOM Summary was hitting the previous 180s
        // ceiling on larger batches and surfacing "Execution Timeout
        // Expired" to the planner. If a single query genuinely needs
        // more than 10 minutes, the right fix is at the SQL / index
        // level rather than another timeout bump.
        cmd.CommandTimeout = 600;
        foreach (var (name, val) in parameters)
            cmd.Parameters.Add(new SqlParameter(name, val));
        var rows = new List<T>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) rows.Add(reader(rdr));
        return rows;
    }

    // ── Custom Report ───────────────────────────────────────────────────────
    // Whitelist of fields the planner can pick on the Custom Report tab. Each
    // entry maps the enum value to a vetted SQL fragment — never any string
    // from the UI. The "SelectExpr" goes into SELECT (and GROUP BY when the
    // field is a dimension); for measures the "Aggregation" wraps it
    // (SUM(...), MAX(...), etc.).
    private sealed record FieldDef(
        string Key,
        string DisplayName,
        string SelectExpr,
        bool IsDimension,
        string Aggregation,
        bool IsNumeric);

    private static readonly Dictionary<CustomReportField, FieldDef> CustomReportFieldDefs = new()
    {
        // Dimensions
        [CustomReportField.StoreID]   = new("StoreID",     "Store ID",     "s.StoreID",                                     true,  "",    false),
        [CustomReportField.StoreName] = new("StoreName",   "Store Name",   "ds.PBFullname",                                 true,  "",    false),
        [CustomReportField.Division]  = new("Division",    "Division",     "div.Division",                                  true,  "",    false),
        [CustomReportField.DivCode]   = new("DivCode",     "Div Code",     "id.DivCode",                                    true,  "",    true),
        [CustomReportField.BoxNo]     = new("BoxNo",       "Box No",       "s.BoxNo",                                       true,  "",    false),
        [CustomReportField.Itemcode]  = new("Itemcode",    "Itemcode",     "s.Itemcode",                                    true,  "",    false),
        [CustomReportField.Brand]     = new("Brand",       "Brand",        "ba.Brand",                                      true,  "",    false),
        [CustomReportField.TrnDate]   = new("TrnDate",     "TrnDate",      "ba.TrnDate",                                    true,  "",    false),
        [CustomReportField.LPMDt]     = new("LPMDt",       "LPM Date",     "ba.LPMDt",                                      true,  "",    false),
        [CustomReportField.BoxKind]   = new("BoxKind",     "LPM",          "CASE WHEN bt.AnyLpmDt IS NOT NULL THEN 'LPM' ELSE 'Non-LPM' END", true, "", false),
        [CustomReportField.Phase]     = new("Phase",       "Phase",        "s.Phase",                                       true,  "",    false),

        // Measures (SUM by default — the dedup-by-grain caveat from Item
        // Details applies; for v1 we just SUM and let the planner pick a
        // grain that makes the totals meaningful).
        [CustomReportField.BoxItemQty]    = new("BoxItemQty",    "Box×Item Qty",    "ba.BoxItemQty",                              false, "SUM", true),
        [CustomReportField.BoxQty]        = new("BoxQty",        "Box Qty",         "bt.BoxQty",                                  false, "SUM", true),
        [CustomReportField.SIMQty]        = new("SIMQty",        "SIM Qty",         "CAST(s.Qty AS bigint)",                      false, "SUM", true),
        [CustomReportField.RRQty]         = new("RRQty",         "RR Qty",          "CASE WHEN s.IsRoundRobin = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END", false, "SUM", true),
        [CustomReportField.OverrideQty]   = new("OverrideQty",   "Override Qty",    "CASE WHEN s.IsOverride   = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END", false, "SUM", true),
        [CustomReportField.SOH]           = new("SOH",           "SOH",             "ISNULL(soh.SOH, 0)",                         false, "SUM", true),
        [CustomReportField.SKUMax]        = new("SKUMax",        "SKU Max",         "ISNULL(sk.SKUMax, 0)",                       false, "SUM", true),
        [CustomReportField.DivSOH]        = new("DivSOH",        "Div SOH",         "ISNULL(dsoh.DivSOH, 0)",                     false, "SUM", true),
        [CustomReportField.DivEOMBalance] = new("DivEOMBalance", "Div EOM Balance", "(ISNULL(eo.TargetEOM, 0) - ISNULL(dsoh.DivSOH, 0))", false, "SUM", true),
        [CustomReportField.DivMerchNeed]  = new("DivMerchNeed",  "Div Merch Need",  "ISNULL(eo.MerchNeedWeek, 0)",                false, "SUM", true),
        // Per-(Store, Div) attributes from LPM_EOM_Output. They're per-pair
        // values (not per-row), so SUM doesn't make sense at finer grains —
        // we use MAX so when the grain is (Store, Div) the rank/wt-avg show
        // unchanged, and at coarser grains the largest value in the group
        // surfaces. Mark them as IsDimension=false so they appear in the
        // "Columns" picker but require a (Store, Div)-or-finer Group By to
        // be meaningful.
        [CustomReportField.PriorityRank]  = new("PriorityRank",  "Rank",            "eo.PriorityRank",                            false, "MAX", true),
        [CustomReportField.WtAvgSoldQty]  = new("WtAvgSoldQty",  "Wt Avg Sold",     "eo.WtAvgSoldQty",                            false, "MAX", true),
    };

    /// <summary>
    /// Runs a planner-defined report against a SIM batch. Group-By drives the
    /// row grain; dimension columns are auto-included in the GROUP BY so the
    /// SQL stays valid. Measure columns are wrapped in their aggregator
    /// (SUM by default). Field names come from a server-side whitelist —
    /// never raw column strings — so the dynamic SQL is injection-safe.
    /// </summary>
    public async Task<CustomReportResult> RunCustomReportAsync(
        long batchNo, CustomReportSpec spec, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var b = await db.LpmSimBatches.AsNoTracking().FirstOrDefaultAsync(x => x.LPMBatchNo == batchNo, ct);
        if (b is null) return new CustomReportResult(new(), new());
        // 1.14.61 — Country-aware whboxitems source for the BoxAttrs / BoxTotals CTEs.
        var whSrc = await ResolveWhSrcAsync(db, b.Country, ct);

        // Normalise spec — dedupe + auto-include dimensions from Columns
        // into GroupBy. Empty Columns = nothing to show; bail early.
        var columns = spec.Columns?.Distinct().ToList() ?? new();
        if (columns.Count == 0) return new CustomReportResult(new(), new());

        var groupBy = new List<CustomReportField>();
        foreach (var f in spec.GroupBy ?? Enumerable.Empty<CustomReportField>())
            if (!groupBy.Contains(f)) groupBy.Add(f);
        // Any dimension that's in Columns but missing from GroupBy must be
        // added — the alternative (MAX(dim)) gives confusing results when the
        // grain has multiple values for a (pre-defined) row.
        foreach (var f in columns)
            if (CustomReportFieldDefs[f].IsDimension && !groupBy.Contains(f))
                groupBy.Add(f);
        if (groupBy.Count == 0)
        {
            // No dimensions at all — the result is a single aggregate row.
            // SQL Server still wants no GROUP BY clause in that case.
        }

        // Build SELECT list in the order the planner picked.
        var selectParts = new List<string>(columns.Count);
        var colInfo     = new List<CustomReportColumnInfo>(columns.Count);
        foreach (var f in columns)
        {
            var def  = CustomReportFieldDefs[f];
            var alias = def.Key;
            var expr = def.IsDimension ? def.SelectExpr : $"{def.Aggregation}({def.SelectExpr})";
            selectParts.Add($"{expr} AS [{alias}]");
            colInfo.Add(new CustomReportColumnInfo(f, alias, def.DisplayName, def.IsNumeric));
        }

        var groupByExprs = groupBy
            .Select(f => CustomReportFieldDefs[f].SelectExpr)
            .ToList();
        var groupByClause = groupByExprs.Count == 0 ? "" : "GROUP BY " + string.Join(", ", groupByExprs);
        var orderByClause = groupByExprs.Count == 0 ? "" : "ORDER BY " + string.Join(", ", groupByExprs);

        // Base CTEs — always built; any combination of column picks reads from
        // these. Mirrors the existing reports' join shape so totals agree.
        var baseCtes = $@"
;WITH BatchItems AS (
    SELECT DISTINCT Itemcode FROM dbo.LPMSIM_Output WHERE LPMBatchNo = @batchNo
),
BatchBoxes AS (
    SELECT DISTINCT BoxNo    FROM dbo.LPMSIM_Output WHERE LPMBatchNo = @batchNo
),
ItemDivLs AS (
    SELECT bi.Itemcode, DivCode = MIN(ls.DivCode)
      FROM BatchItems bi
      INNER JOIN racks.dbo.LPM_LocStock ls
              ON ls.Itemcode = bi.Itemcode
             AND ls.Country  = @country
             AND ls.DivCode IS NOT NULL
     GROUP BY bi.Itemcode
),
ItemDivUpc AS (
    SELECT bi.Itemcode, DivCode = MIN(d.DivCode)
      FROM BatchItems bi
      INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = bi.Itemcode
      INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID   = u.MH4ID
      INNER JOIN LPMSIM.dbo.Division               d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
     WHERE bi.Itemcode NOT IN (SELECT Itemcode FROM ItemDivLs)
     GROUP BY bi.Itemcode
),
ItemDiv AS (
    SELECT Itemcode, DivCode FROM ItemDivLs
    UNION ALL
    SELECT Itemcode, DivCode FROM ItemDivUpc
),
BoxAttrs AS (
    -- Per (BoxNo, ItemCode): qty, brand, trndate, lpmdt
    SELECT BoxNo, ItemCode,
           BoxItemQty = SUM(CAST(Qty AS bigint)),
           Brand      = MAX(Brand),
           TrnDate    = MAX(TrnDate),
           LPMDt      = MAX(LPMDt)
      FROM {whSrc}
     WHERE BoxNo IN (SELECT BoxNo FROM BatchBoxes)
     GROUP BY BoxNo, ItemCode
),
BoxTotals AS (
    -- Per BoxNo: total qty + did-any-item-have-LPMDt? (drives 'LPM'/'Non-LPM').
    SELECT BoxNo,
           BoxQty   = SUM(CAST(Qty AS bigint)),
           AnyLpmDt = MAX(LPMDt)
      FROM {whSrc}
     WHERE BoxNo IN (SELECT BoxNo FROM BatchBoxes)
     GROUP BY BoxNo
),
SohAgg AS (
    SELECT ls.StoreID, ls.Itemcode, SOH = SUM(CAST(ISNULL(ls.SOH, 0) AS bigint))
      FROM racks.dbo.LPM_LocStock ls
      INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
     WHERE ds.SIMCountry = @country
       AND ls.StoreID  IS NOT NULL AND ls.StoreID  <> ''
       AND ls.Itemcode IS NOT NULL AND ls.Itemcode <> ''
     GROUP BY ls.StoreID, ls.Itemcode
),
DivSohAgg AS (
    -- (Store, Div) SOH for DivEOMBalance and DivSOH columns.
    SELECT ls.StoreID, ls.DivCode, DivSOH = SUM(CAST(ISNULL(ls.SOH, 0) AS bigint))
      FROM racks.dbo.LPM_LocStock ls
      INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
     WHERE ds.SIMCountry = @country
       AND ls.DivCode IS NOT NULL
       AND ls.StoreID IS NOT NULL AND ls.StoreID <> ''
     GROUP BY ls.StoreID, ls.DivCode
),
SkuMaxAgg AS (
    SELECT StoreID, ItemCode, SKUMax = MAX(SKUMax)
      FROM dbo.LPM_SimItemSkuMax
     WHERE Country = @country AND Year1 = @y AND Month1 = @m
     GROUP BY StoreID, ItemCode
)";

        var sql = $@"{baseCtes}
SELECT {string.Join(", ", selectParts)}
  FROM dbo.LPMSIM_Output s
  INNER JOIN ItemDiv     id   ON id.Itemcode  = s.Itemcode
  LEFT  JOIN BoxAttrs    ba   ON ba.BoxNo     = s.BoxNo  AND ba.ItemCode = s.Itemcode
  LEFT  JOIN BoxTotals   bt   ON bt.BoxNo     = s.BoxNo
  LEFT  JOIN dbo.Division div ON div.DivCode  = id.DivCode
  OUTER APPLY (
      SELECT TOP 1 PBFullname FROM dbo.DataSettings d
       WHERE d.StoreID = s.StoreID
         AND (d.SIMCountry = @country OR d.SIMCountry IS NULL)
  ) ds
  LEFT  JOIN SohAgg      soh  ON soh.StoreID  = s.StoreID AND soh.Itemcode = s.Itemcode
  LEFT  JOIN DivSohAgg   dsoh ON dsoh.StoreID = s.StoreID AND dsoh.DivCode = id.DivCode
  LEFT  JOIN dbo.LPM_EOM_Output eo
         ON eo.Country = @country AND eo.Year1 = @y AND eo.Month1 = @m
        AND eo.StoreID = s.StoreID AND eo.DivCode = id.DivCode
  LEFT  JOIN SkuMaxAgg   sk   ON sk.StoreID   = s.StoreID AND sk.ItemCode = s.Itemcode
 WHERE s.LPMBatchNo = @batchNo
 {groupByClause}
 {orderByClause};";

        // Run + materialise rows as Dictionary<string, object?> keyed by
        // the column alias so the UI can render dynamic columns without a
        // typed result class per spec.
        var rows = new List<Dictionary<string, object?>>();
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        using (var cmd = (SqlCommand)conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqlParameter("@batchNo", batchNo));
            cmd.Parameters.Add(new SqlParameter("@country", b.Country));
            cmd.Parameters.Add(new SqlParameter("@y",       b.RunYear));
            cmd.Parameters.Add(new SqlParameter("@m",       b.RunMonth));
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(colInfo.Count);
                for (int i = 0; i < colInfo.Count; i++)
                {
                    var v = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                    row[colInfo[i].Key] = v;
                }
                rows.Add(row);
            }
        }
        return new CustomReportResult(colInfo, rows);
    }

    /// <summary>
    /// 1.14.26 — Per-eligible-box allocation gap diagnostic for the
    /// "Allocation Gap" tab on the SIM Generate result preview. Reads
    /// <c>dbo.LPMSIM_UnallocatedDiagnostic</c> (populated at the end of
    /// every successful SIM Generate from 1.14.26 onward).
    ///
    /// Filters (all optional, all AND-combined):
    ///   • <paramref name="boxKind"/> — "LPM" or "Non-LPM"; null = both.
    ///   • <paramref name="topReason"/> — e.g. "FILTERED_SEASON" /
    ///     "SKIP_NO_DIV" / "CAP" / etc. null = no filter.
    ///   • <paramref name="boxNoContains"/> — case-insensitive substring;
    ///     null/empty = no filter.
    ///   • <paramref name="minRemaining"/> — only rows with RemainingQty
    ///     ≥ N; null = no minimum (every row in the table).
    ///
    /// Sorted by RemainingQty DESC so the biggest gaps surface first.
    /// </summary>
    public async Task<List<UnallocatedDiagnosticRow>> GetUnallocatedDiagnosticAsync(
        long batchNo,
        string? boxKind = null,
        string? topReason = null,
        string? boxNoContains = null,
        long? minRemaining = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.LpmSimUnallocatedDiagnostics.AsNoTracking()
            .Where(d => d.LPMBatchNo == batchNo);
        if (!string.IsNullOrEmpty(boxKind))
            q = q.Where(d => d.BoxKind == boxKind);
        if (!string.IsNullOrEmpty(topReason))
            q = q.Where(d => d.TopReason == topReason);
        if (!string.IsNullOrEmpty(boxNoContains))
            q = q.Where(d => d.BoxNo.Contains(boxNoContains));
        if (minRemaining.HasValue && minRemaining.Value > 0)
            q = q.Where(d => d.RemainingQty >= minRemaining.Value);

        return await q.OrderByDescending(d => d.RemainingQty)
                      .ThenBy(d => d.BoxNo)
                      .Select(d => new UnallocatedDiagnosticRow(
                          d.BoxNo,
                          d.PalletNo,
                          d.LPMDt,
                          d.BoxKind,
                          d.BoxQty,
                          d.SimQty,
                          d.RemainingQty,
                          d.TopReason,
                          d.Reasons))
                      .ToListAsync(ct);
    }

    /// <summary>
    /// 1.14.93 — Per-UPC Allocation Gap for the given batch. One row per
    /// ItemCode where <c>EligibleWhQty − SimQty &gt; 0</c>, with the dominant
    /// SKIP reason from <c>LPMSIM_AllocTrace</c>.
    ///
    /// <para>Definitions (matches the planner's mental model):</para>
    /// <list type="bullet">
    ///   <item><c>EligibleWhQty</c> = qty in eligible whboxitems that ENTERED
    ///         the SIM box pool for the item. Source: <c>SUM(MAX(LineQty))</c>
    ///         per <c>(BoxNo, ItemCode)</c> in <c>LPMSIM_AllocTrace</c> for
    ///         the batch. LineQty is the same across all store-decisions for
    ///         the same box-line, so MAX dedupes correctly.</item>
    ///   <item><c>SimQty</c> = SUM(Qty) from <c>LPMSIM_Output</c> for the
    ///         batch grouped by Itemcode.</item>
    ///   <item><c>RemainingQty</c> = <c>EligibleWhQty − SimQty</c>.</item>
    ///   <item><c>TopReason</c> = most-frequent <c>SKIP_*</c> Decision in
    ///         the AllocTrace for the item (e.g. SKIP_SKUMAX, SKIP_MNM,
    ///         SKIP_NO_GRADE, SKIP_NO_DIV, SKIP_NO_EOM, SKIP_EOM_BALANCE).
    ///         Tie-broken alphabetically. Empty string when no SKIP rows
    ///         exist for the item (rare).</item>
    /// </list>
    ///
    /// <para>Item description from <c>HODATA.dbo.Itemmaster</c> and Division
    /// from <c>upc_subclass × subclassmaster × LPMSIM.dbo.Division</c> —
    /// same lookup shape used by WH SKU Investigation.</para>
    /// </summary>
    public async Task<List<ItemAllocationGapRow>> GetItemAllocationGapAsync(
        long batchNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        const string sql = @"
            ;WITH BoxLineEligible AS (
                -- 1 row per (BoxNo, ItemCode). LineQty is identical across
                -- store decisions for the same box-line, so MAX is fine.
                SELECT BoxNo, ItemCode, LineQty = MAX(LineQty)
                  FROM dbo.LPMSIM_AllocTrace
                 WHERE LPMBatchNo = @batchNo
                 GROUP BY BoxNo, ItemCode
            ),
            ItemEligible AS (
                SELECT ItemCode, EligibleWhQty = SUM(CAST(LineQty AS bigint))
                  FROM BoxLineEligible
                 GROUP BY ItemCode
            ),
            ItemAllocated AS (
                SELECT Itemcode AS ItemCode,
                       SimQty = SUM(CAST(Qty AS bigint))
                  FROM dbo.LPMSIM_Output
                 WHERE LPMBatchNo = @batchNo
                 GROUP BY Itemcode
            ),
            ItemReasons AS (
                -- Count of SKIP rows per (Item, Decision). Used to pick the
                -- dominant skip reason per item.
                SELECT ItemCode, Decision, Cnt = COUNT_BIG(*)
                  FROM dbo.LPMSIM_AllocTrace
                 WHERE LPMBatchNo = @batchNo
                   AND Decision LIKE 'SKIP_%'
                 GROUP BY ItemCode, Decision
            ),
            ItemTopReason AS (
                SELECT ItemCode, TopReason = Decision,
                       rn = ROW_NUMBER() OVER
                            (PARTITION BY ItemCode ORDER BY Cnt DESC, Decision)
                  FROM ItemReasons
            ),
            ItemDiv AS (
                -- Same shape as the WH SKU Investigation Division lookup.
                SELECT u.itemcode, DivisionName = MIN(d.Division)
                  FROM Datareporting.dbo.upc_subclass    u
                  INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                  INNER JOIN LPMSIM.dbo.Division               d
                          ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
                         AND d.IsActive = 1
                 GROUP BY u.itemcode
            )
            SELECT  ie.ItemCode,
                    ItemName     = ISNULL(im.description, ''),
                    Division     = ISNULL(idv.DivisionName, ''),
                    EligibleWhQty = ie.EligibleWhQty,
                    SimQty        = ISNULL(ia.SimQty, 0),
                    RemainingQty  = ie.EligibleWhQty - ISNULL(ia.SimQty, 0),
                    TopReason     = ISNULL(itr.TopReason, '')
              FROM  ItemEligible ie
              LEFT  JOIN ItemAllocated ia       ON ia.ItemCode = ie.ItemCode
              LEFT  JOIN ItemTopReason itr      ON itr.ItemCode = ie.ItemCode AND itr.rn = 1
              LEFT  JOIN ItemDiv idv            ON idv.itemcode = ie.ItemCode
              LEFT  JOIN HODATA.dbo.Itemmaster im
                      ON CAST(im.Itemcode AS nvarchar(64)) = ie.ItemCode
             WHERE (ie.EligibleWhQty - ISNULL(ia.SimQty, 0)) > 0
             ORDER BY RemainingQty DESC, ie.ItemCode;";

        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@batchNo", batchNo));
        cmd.CommandTimeout = 180;

        var rows = new List<ItemAllocationGapRow>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ItemAllocationGapRow(
                ItemCode:      rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                ItemName:      rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                Division:      rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                EligibleWhQty: rdr.IsDBNull(3) ? 0L : rdr.GetInt64(3),
                SimQty:        rdr.IsDBNull(4) ? 0L : rdr.GetInt64(4),
                RemainingQty:  rdr.IsDBNull(5) ? 0L : rdr.GetInt64(5),
                TopReason:     rdr.IsDBNull(6) ? "" : rdr.GetString(6)));
        }
        return rows;
    }
}

/// <summary>
/// 1.14.26 — Projection for the Allocation Gap tab. Mirrors
/// <see cref="LpmSim.Core.Entities.LpmSimUnallocatedDiagnostic"/> minus the
/// batch + audit columns.
/// </summary>
public record UnallocatedDiagnosticRow(
    string    BoxNo,
    string?   PalletNo,
    DateTime? LPMDt,
    string    BoxKind,
    long      BoxQty,
    long      SimQty,
    long      RemainingQty,
    string    TopReason,
    string?   Reasons);
