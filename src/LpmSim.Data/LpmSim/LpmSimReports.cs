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
    public decimal? Balance { get; set; }
    public long LpmSimQty { get; set; }
    public long RoundRobinQty { get; set; }
}

/// <summary>
/// Store-level rollup of the SIM result. One row per store, summing across
/// every division. EomDiff = EOM − SOH (positive = headroom to fill).
/// </summary>
public class StoreSummaryRow
{
    public long LPMBatchNo { get; set; }
    public string StoreID { get; set; } = "";
    public string StoreName { get; set; } = "";
    public decimal EOM { get; set; }
    public long SOH { get; set; }
    public decimal EomDiff { get; set; }
    public long SimQty { get; set; }
    public long RrQty { get; set; }
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
           RoundRobinQty = SUM(CASE WHEN s.IsRoundRobin = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END)
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
       EOM       = eo.TargetEOM,
       SOH       = ISNULL(soh.SOH, 0),
       Balance   = eo.TargetEOM - ISNULL(soh.SOH, 0),
       LpmSimQty = ISNULL(sa.LpmSimQty, 0),
       RoundRobinQty = ISNULL(sa.RoundRobinQty, 0)
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
           SimQty = SUM(CAST(s.Qty AS bigint)),
           RrQty  = SUM(CASE WHEN s.IsRoundRobin = 1 THEN CAST(s.Qty AS bigint) ELSE 0 END)
      FROM dbo.LPMSIM_Output s
      INNER JOIN QualifyingBoxes qb ON qb.BoxNo = s.BoxNo
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.StoreID
),
EomAgg AS (
    SELECT eo.StoreID, EOM = SUM(ISNULL(eo.TargetEOM, 0))
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
       EOM     = ISNULL(eo.EOM, 0),
       SOH     = ISNULL(soh.SOH, 0),
       EomDiff = ISNULL(eo.EOM, 0) - ISNULL(soh.SOH, 0),
       SimQty  = ISNULL(sim.SimQty, 0),
       RrQty   = ISNULL(sim.RrQty, 0)
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

    private static StoreSummaryRow ReadStoreSummary(SqlDataReader r) => new()
    {
        LPMBatchNo = r.GetInt64(0),
        StoreID    = r.IsDBNull(1) ? "" : r.GetString(1),
        StoreName  = r.IsDBNull(2) ? "" : r.GetString(2),
        EOM        = r.IsDBNull(3) ? 0 : r.GetDecimal(3),
        SOH        = r.IsDBNull(4) ? 0 : r.GetInt64(4),
        EomDiff    = r.IsDBNull(5) ? 0 : r.GetDecimal(5),
        SimQty     = r.IsDBNull(6) ? 0 : r.GetInt64(6),
        RrQty      = r.IsDBNull(7) ? 0 : r.GetInt64(7),
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
),
ItemDiv AS (
    SELECT u.itemcode, MIN(d.DivCode) AS DivID
      FROM Datareporting.dbo.upc_subclass u
      INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
      INNER JOIN LPMSIM.dbo.Division d
              ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
     GROUP BY u.itemcode
),
SohAgg AS (
    SELECT ls.StoreID, ls.Itemcode, SUM(ISNULL(ls.SOH,0)) AS SOH
      FROM racks.dbo.LPM_LocStock ls
      INNER JOIN dbo.DataSettings ds ON ds.StoreID = ls.StoreID
     WHERE ds.SIMCountry = @country
       AND ls.StoreID  IS NOT NULL AND ls.StoreID  <> ''
       AND ls.Itemcode IS NOT NULL AND ls.Itemcode <> ''
     GROUP BY ls.StoreID, ls.Itemcode
)
SELECT s.LPMBatchNo,
       s.StoreID,
       ds.PBFullname           AS StoreName,
       id.DivID                AS DivCode,
       div.Division            AS DivisionName,
       s.BoxNo,
       s.Itemcode,
       (SELECT SUM(CAST(Qty AS bigint)) FROM racks.dbo.whboxitems w
         WHERE w.BoxNo = s.BoxNo AND w.ItemCode = s.Itemcode) AS BoxItemQty,
       eo.SKUMax,
       ISNULL(soh.SOH, 0)      AS SOH,
       s.Qty                   AS LpmQty,
       CASE WHEN s.IsRoundRobin = 1 THEN s.Qty ELSE 0 END AS RoundRobinQty,
       s.Phase                 AS Phase
  FROM dbo.LPMSIM_Output s
  INNER JOIN ItemDiv id        ON id.itemcode = s.Itemcode
  INNER JOIN QualifyingBoxes qb ON qb.BoxNo   = s.BoxNo
  -- One DataSettings row per Store (defensive against duplicate StoreID
  -- entries in bfldata.dbo.DataSettings — would otherwise multiply rows).
  OUTER APPLY (
      SELECT TOP 1 PBFullname FROM dbo.DataSettings d
       WHERE d.StoreID = s.StoreID
         AND (d.SIMCountry = @country OR d.SIMCountry IS NULL)
  ) ds
  LEFT JOIN dbo.Division div    ON div.DivCode = id.DivID
  LEFT JOIN dbo.LPM_EOM_Output eo
         ON eo.Country = @country AND eo.StoreID = s.StoreID
        AND eo.DivCode = id.DivID AND eo.Year1 = @y AND eo.Month1 = @m
  LEFT JOIN SohAgg soh ON soh.StoreID = s.StoreID AND soh.Itemcode = s.Itemcode
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
        Balance       = r.IsDBNull(7) ? null : r.GetDecimal(7),
        LpmSimQty     = r.IsDBNull(8) ? 0 : r.GetInt64(8),
        RoundRobinQty = r.IsDBNull(9) ? 0 : r.GetInt64(9),
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
    };

    private static async Task<List<T>> ExecAsync<T>(
        LpmDbContext db, string sql, Func<SqlDataReader, T> reader,
        CancellationToken ct, Dictionary<string, object> parameters)
    {
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        using var cmd = (SqlCommand)conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 180;
        foreach (var (name, val) in parameters)
            cmd.Parameters.Add(new SqlParameter(name, val));
        var rows = new List<T>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) rows.Add(reader(rdr));
        return rows;
    }
}
