using LpmSim.Core.Entities;
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

    public async Task<List<BatchHeader>> ListBatchesAsync(string country, DateTime? runDate, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.LpmSimBatches.AsNoTracking().Where(b => b.Country == country);
        if (runDate.HasValue) q = q.Where(b => b.RunDate == runDate.Value.Date);
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

        // Drives the report off LPM_EOM_Output so EVERY (Store, Div) row in the
        // plan appears — even those with zero SIM Qty / zero SOH. SIM and SOH
        // are LEFT-JOINed onto that baseline.
        // Box-usability filter (optional): when @minPct/@maxPct are non-NULL,
        // only allocations from boxes whose total SIM/Box-Qty % falls in range
        // contribute to LpmSimQty / RoundRobinQty.
        const string sql = @"
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
           BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint)) FROM racks.dbo.whboxitems w WHERE w.BoxNo = s.BoxNo), 0),
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

        const string sql = @"
WITH BoxUsability AS (
    -- Per-box usability % = total SIM Qty allocated / total Qty in the box × 100.
    -- BoxQty comes from the source (racks.whboxitems); SIM is summed across
    -- every (Store, Item) target.
    SELECT s.BoxNo,
           BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint)) FROM racks.dbo.whboxitems w WHERE w.BoxNo = s.BoxNo), 0),
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

        const string sql = @"
WITH BoxUsability AS (
    -- Per-box usability % = SIM Qty / Box Qty × 100, used by the optional
    -- Min/Max Box Usability % filter so this report agrees with the others.
    SELECT s.BoxNo,
           BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint)) FROM racks.dbo.whboxitems w WHERE w.BoxNo = s.BoxNo), 0),
           SimQty = SUM(CAST(s.Qty AS bigint))
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.BoxNo
),
QualifyingBoxes AS (
    SELECT BoxNo
      FROM BoxUsability
     WHERE BoxQty > 0
       AND (@minPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) >= @minPct)
       AND (@maxPct IS NULL OR ROUND(CAST(SimQty AS decimal(20,4)) * 100 / BoxQty, 1) <= @maxPct)
),
-- DivCode resolution mirrors the Store×Div SQL: LocStock first (matches engine)
-- with upc_subclass × subclassmaster × Division as fallback for items not in LocStock.
BatchItems AS (
    SELECT DISTINCT Itemcode FROM dbo.LPMSIM_Output WHERE LPMBatchNo = @batchNo
),
ItemDivLs AS (
    SELECT bi.Itemcode, DivCode = MIN(ls.DivCode)
      FROM BatchItems bi
      INNER JOIN racks.dbo.LPM_LocStock ls
              ON ls.Itemcode = bi.Itemcode AND ls.Country = @country AND ls.DivCode IS NOT NULL
     GROUP BY bi.Itemcode
),
ItemDivUpc AS (
    SELECT bi.Itemcode, DivCode = MIN(d.DivCode)
      FROM BatchItems bi
      INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = bi.Itemcode
      INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID  = u.MH4ID
      INNER JOIN LPMSIM.dbo.Division               d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
     WHERE bi.Itemcode NOT IN (SELECT Itemcode FROM ItemDivLs)
     GROUP BY bi.Itemcode
),
ItemDiv AS (
    SELECT Itemcode, DivCode FROM ItemDivLs
    UNION ALL
    SELECT Itemcode, DivCode FROM ItemDivUpc
),
SimAgg AS (
    SELECT id.DivCode,
           SimQty      = SUM(CAST(s.Qty AS bigint)),
           RrQty       = SUM(CASE WHEN s.IsRoundRobin = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END),
           OverrideQty = SUM(CASE WHEN s.IsOverride   = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END)
      FROM dbo.LPMSIM_Output s
      INNER JOIN ItemDiv id        ON id.Itemcode = s.Itemcode
      INNER JOIN QualifyingBoxes qb ON qb.BoxNo   = s.BoxNo
     WHERE s.LPMBatchNo = @batchNo
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
       OverrideQty   = ISNULL(sim.OverrideQty, 0)
  FROM AllDivs a
  LEFT JOIN dbo.Division div ON div.DivCode = a.DivCode
  LEFT JOIN SimAgg sim       ON sim.DivCode = a.DivCode
  LEFT JOIN EomAgg eo        ON eo.DivCode  = a.DivCode
  LEFT JOIN SohAgg soh       ON soh.DivCode = a.DivCode
 ORDER BY div.Division, a.DivCode;";

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

        var sql = rollupToBoxOnly
            ? @"
WITH BoxAgg AS (
    SELECT s.LPMBatchNo, s.BoxNo,
           SUM(CAST(s.Qty AS bigint)) AS LpmSimQty,
           SUM(CASE WHEN s.IsRoundRobin = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END) AS RoundRobinQty
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.LPMBatchNo, s.BoxNo
),
BoxMeta AS (
    SELECT BoxNo,
           SUM(CAST(Qty AS bigint)) AS BoxQty,
           MAX(PalletType) AS PalletType,
           MAX(TrnDate)    AS TrnDate,
           MAX(Warehouse)  AS Warehouse,
           MAX(Rack)       AS Rack,
           CASE WHEN MAX(CASE WHEN LPMDt IS NOT NULL THEN 1 ELSE 0 END) = 1 THEN 'LPM' ELSE 'Non-LPM' END AS BoxKind
      FROM racks.dbo.whboxitems
     WHERE BoxNo IN (SELECT BoxNo FROM BoxAgg)
     GROUP BY BoxNo
)
SELECT b.LPMBatchNo,
       NULL AS StoreID, NULL AS StoreName,
       NULL AS DivCode, NULL AS DivisionName,
       b.BoxNo,
       bm.BoxQty,
       b.LpmSimQty,
       bm.PalletType,
       pt.TypeName,
       pt.PalletCategory,
       bm.TrnDate,
       bm.Warehouse,
       bm.Rack,
       b.RoundRobinQty,
       bm.BoxKind
  FROM BoxAgg b
  LEFT JOIN BoxMeta bm ON bm.BoxNo = b.BoxNo
  LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = bm.PalletType
 ORDER BY b.BoxNo;"
            : @"
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
    SELECT s.LPMBatchNo, s.StoreID, id.DivID AS DivCode, s.BoxNo,
           SUM(CAST(s.Qty AS bigint)) AS LpmSimQty
      FROM dbo.LPMSIM_Output s
      INNER JOIN ItemDiv id ON id.itemcode = s.Itemcode
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.LPMBatchNo, s.StoreID, id.DivID, s.BoxNo
)
SELECT sa.LPMBatchNo,
       sa.StoreID,
       MAX(ds.PBFullname) AS StoreName,
       sa.DivCode,
       MAX(div.Division)  AS DivisionName,
       sa.BoxNo,
       (SELECT SUM(CAST(Qty AS bigint)) FROM racks.dbo.whboxitems w WHERE w.BoxNo = sa.BoxNo) AS BoxQty,
       sa.LpmSimQty,
       (SELECT TOP 1 PalletType FROM racks.dbo.whboxitems w WHERE w.BoxNo = sa.BoxNo) AS PalletType,
       (SELECT TOP 1 pt.TypeName       FROM racks.dbo.whboxitems w
          LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
         WHERE w.BoxNo = sa.BoxNo) AS TypeName,
       (SELECT TOP 1 pt.PalletCategory FROM racks.dbo.whboxitems w
          LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
         WHERE w.BoxNo = sa.BoxNo) AS PalletCategory
  FROM SimAgg sa
  LEFT JOIN dbo.DataSettings ds ON ds.StoreID = sa.StoreID
  LEFT JOIN dbo.Division div ON div.DivCode = sa.DivCode
 GROUP BY sa.LPMBatchNo, sa.StoreID, sa.DivCode, sa.BoxNo, sa.LpmSimQty
 ORDER BY MAX(div.Division), sa.StoreID, sa.BoxNo;";

        return await ExecAsync(db, sql, ReadBoxDetail, ct, new Dictionary<string, object>
        {
            ["@batchNo"] = batchNo,
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
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        // Box-usability filter (same CTE rule used by Summary / Item Details /
        // Store Summary / Trace) — keeps the source-split matrix aligned with
        // every other tab so SIM Qty totals reconcile byte-for-byte.
        cmd.CommandText = @"
WITH BoxUsability AS (
    SELECT s.BoxNo,
           BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint))
                              FROM racks.dbo.whboxitems w
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
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        const string sql = @"
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
      FROM racks.dbo.whboxitems w
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

        const string sql = @"
-- ── Item Details — perf rewrite ─────────────────────────────────────────
-- Pre-aggregate everything from racks.dbo.whboxitems into CTEs ONCE rather
-- than the previous per-row subqueries that hit whboxitems for each of the
-- 116K LPMSIM_Output rows. On the UAE batch this dropped the query from
-- multi-second to sub-second territory. Restricting the CTEs to
-- BatchBoxes (= boxes that participated in this batch) keeps them small.
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
           BoxItemQty = SUM(CAST(w.Qty AS bigint))
      FROM racks.dbo.whboxitems w
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
      FROM racks.dbo.whboxitems w
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
    /// Distinct warehouse codes from <c>racks.dbo.whboxitems</c>. Used to populate
    /// the multi-select on the SIM Generate page so the planner can scope a run
    /// to one or more warehouses.
    /// </summary>
    public async Task<List<string>> GetDistinctWarehousesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT Warehouse
              FROM racks.dbo.whboxitems
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
    public async Task<List<BatchListEntry>> GetBatchesForPeriodAsync(string country, DateTime runDate, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LpmSimBatches.AsNoTracking()
            .Where(b => b.Country == country && b.RunDate == runDate.Date)
            .OrderByDescending(b => b.LPMBatchNo)
            .Select(b => new BatchListEntry(
                b.LPMBatchNo, b.Status ?? "", b.CreateTS, b.CreatedBy ?? "",
                b.Sources, b.OverrideUsabilityPct, b.FillStrategy,
                b.ApprovedTS, b.ApprovedBy))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Distinct LPM months from <c>racks.dbo.whboxitems.LPMDt</c> as a list of
    /// month-start <see cref="DateTime"/> values (1st of each month).
    /// Feeds the SIM Generate "LPM Months" multi-select so the planner can
    /// scope an LPM run to specific months instead of the default "all
    /// months up to the run period". Empty selection on the page = legacy
    /// behaviour.
    /// </summary>
    public async Task<List<DateTime>> GetDistinctLpmMonthsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT DATEFROMPARTS(YEAR(LPMDt), MONTH(LPMDt), 1) AS MonthStart
              FROM racks.dbo.whboxitems
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
        var sql = $@"
WITH BoxUsability AS (
    SELECT s.BoxNo,
           BoxQty = ISNULL((SELECT SUM(CAST(w.Qty AS bigint)) FROM racks.dbo.whboxitems w WHERE w.BoxNo = s.BoxNo), 0),
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

    private static BoxDetailRow ReadBoxDetail(SqlDataReader r) => new()
    {
        LPMBatchNo     = r.GetInt64(0),
        StoreID        = r.IsDBNull(1) ? null : r.GetString(1),
        StoreName      = r.IsDBNull(2) ? null : r.GetString(2),
        DivCode        = r.IsDBNull(3) ? null : r.GetInt32(3),
        DivisionName   = r.IsDBNull(4) ? null : r.GetString(4),
        BoxNo          = r.IsDBNull(5) ? "" : r.GetString(5),
        BoxQty         = r.IsDBNull(6) ? null : r.GetInt64(6),
        LpmSimQty      = r.IsDBNull(7) ? 0 : r.GetInt64(7),
        PalletType     = r.IsDBNull(8) ? null : r.GetString(8),
        TypeName       = r.IsDBNull(9) ? null : r.GetString(9),
        PalletCategory = r.IsDBNull(10) ? null : r.GetString(10),
        TrnDate        = r.FieldCount > 11 && !r.IsDBNull(11) ? r.GetDateTime(11) : null,
        Warehouse      = r.FieldCount > 12 && !r.IsDBNull(12) ? r.GetString(12)   : null,
        Rack           = r.FieldCount > 13 && !r.IsDBNull(13) ? r.GetString(13)   : null,
        RoundRobinQty  = r.FieldCount > 14 && !r.IsDBNull(14) ? r.GetInt64(14)    : 0,
        BoxKind        = r.FieldCount > 15 && !r.IsDBNull(15) ? r.GetString(15)   : null,
    };

    private static ItemDetailRow ReadItemDetail(SqlDataReader r) => new()
    {
        LPMBatchNo    = r.GetInt64(0),
        StoreID       = r.IsDBNull(1) ? "" : r.GetString(1),
        StoreName     = r.IsDBNull(2) ? "" : r.GetString(2),
        DivCode       = r.IsDBNull(3) ? 0  : r.GetInt32(3),
        DivisionName  = r.IsDBNull(4) ? "" : r.GetString(4),
        BoxNo         = r.IsDBNull(5) ? "" : r.GetString(5),
        Itemcode      = r.IsDBNull(6) ? "" : r.GetString(6),
        BoxItemQty    = r.IsDBNull(7) ? null : r.GetInt64(7),
        SKUMax        = r.IsDBNull(8) ? null : r.GetInt32(8),
        SOH           = r.IsDBNull(9) ? 0 : Convert.ToInt32(r.GetValue(9)),
        LpmQty        = r.IsDBNull(10) ? 0 : r.GetInt32(10),
        RoundRobinQty = r.IsDBNull(11) ? 0 : r.GetInt32(11),
        Phase         = r.IsDBNull(12) ? null : r.GetString(12),
        BoxKind       = r.IsDBNull(13) ? "" : r.GetString(13),
        PriorityRank  = r.IsDBNull(14) ? null : r.GetDecimal(14),
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
        const string baseCtes = @"
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
      FROM racks.dbo.whboxitems
     WHERE BoxNo IN (SELECT BoxNo FROM BatchBoxes)
     GROUP BY BoxNo, ItemCode
),
BoxTotals AS (
    -- Per BoxNo: total qty + did-any-item-have-LPMDt? (drives 'LPM'/'Non-LPM').
    SELECT BoxNo,
           BoxQty   = SUM(CAST(Qty AS bigint)),
           AnyLpmDt = MAX(LPMDt)
      FROM racks.dbo.whboxitems
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
}
