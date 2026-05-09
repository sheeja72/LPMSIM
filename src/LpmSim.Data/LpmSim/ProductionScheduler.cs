using LpmSim.Core;
using LpmSim.Core.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;   // GetDbTransaction extension

namespace LpmSim.Data.LpmSim;

// ── Models ──────────────────────────────────────────────────────────────

/// <summary>
/// Inputs for <see cref="ProductionScheduler.GenerateAsync"/>.
/// </summary>
public class ProductionScheduleRequest
{
    public long    LPMBatchNo      { get; set; }
    public int     DailyTargetQty  { get; set; } = 55_000;
    public int     DaysInWeek      { get; set; } = 6;
    public decimal MinUsabilityPct { get; set; } = 0m;
    public string  User            { get; set; } = "";

    /// <summary>
    /// Optional list of warehouse codes (matches <c>racks.dbo.whboxitems.Warehouse</c>)
    /// that should be dispatched first within each LPM/Non-LPM tier. Empty
    /// or null = no priority, all warehouses treated equal.
    /// </summary>
    public List<string> PriorityWarehouses { get; set; } = new();
}

/// <summary>
/// Result of <see cref="ProductionScheduler.GenerateAsync"/>.
/// Mirrors what's persisted on <see cref="LpmSimProductionSchedule"/>.
/// </summary>
public class ProductionScheduleResult
{
    public long LPMBatchNo     { get; set; }
    public int  EligibleBoxes  { get; set; }
    public long EligibleQty    { get; set; }
    public int  ScheduledBoxes { get; set; }
    public long ScheduledQty   { get; set; }
    public int  DeferredBoxes  { get; set; }
    public long DeferredQty    { get; set; }
    public int  DropboxBoxes   { get; set; }   // boxes below MinUsabilityPct (Day stays NULL)
    public long DropboxQty     { get; set; }
    public TimeSpan Duration   { get; set; }
}

/// <summary>
/// Per-day rollup row used by the schedule's daily summary tab.
/// </summary>
public class ProductionDayRow
{
    public int    Day            { get; set; }    // 1..D, or 0 for unscheduled
    public int    BoxCount       { get; set; }
    public long   Qty            { get; set; }
    public int    StoreCount     { get; set; }
    public int    DivisionCount  { get; set; }
    public decimal? AvgUsability { get; set; }    // average per-box usability % rounded to 1dp
}

/// <summary>
/// Per-Division rollup row. Each LPMSIM_Output row's qty is attributed to
/// its item's Division (so a box that contains items from 3 divisions
/// contributes proportionally to each, not just the dominant one).
/// Lets the planner check how each division's qty was split between
/// scheduled days and deferred / dropbox status.
/// </summary>
public class ProductionDivisionRow
{
    public int    DivCode        { get; set; }
    public string DivisionName   { get; set; } = "";
    public long   TotalQty       { get; set; }   // SUM of LPMSIM_Output.Qty for this div
    public long   ScheduledQty   { get; set; }   // qty on rows with Day IS NOT NULL
    public long   DeferredQty    { get; set; }   // qty on rows with Day IS NULL
    public int    BoxCount       { get; set; }   // distinct boxes touching this div
    public int    ScheduledBoxes { get; set; }   // distinct boxes with at least one Day-tagged row
    public int    DeferredBoxes  { get; set; }   // distinct boxes whose div lines all have Day = NULL
    public int    StoreCount     { get; set; }   // distinct stores receiving qty for this div
    public long   MerchNeedWeek  { get; set; }   // Σ Merch Need (Week) from LPM_EOM_Output for this div
    public long   MerchNeedDay   { get; set; }   // Σ Merch Need (Day)  from LPM_EOM_Output for this div
    public decimal? PctOfTotal   { get; set; }   // ScheduledQty as % of total scheduled across all divs
}

/// <summary>
/// Per-box detail row for the schedule (one row per Box, regardless of how
/// many stores the box ships to).
/// </summary>
public class ProductionBoxRow
{
    public string  BoxNo          { get; set; } = "";
    public DateTime? LPMDt        { get; set; }
    public int?    Day            { get; set; }       // NULL = unscheduled / dropbox
    public long    BoxQty         { get; set; }       // SIM-allocated qty in the box
    public long    Capacity       { get; set; }       // total qty of items in the box (whboxitems.Qty sum)
    public decimal? UsabilityPct  { get; set; }
    public int     StoreCount     { get; set; }
    public int     DivisionCount  { get; set; }
    public string? DominantDiv    { get; set; }        // div that contributed the most qty
    public string? Status         { get; set; }        // "Scheduled" | "Deferred" | "Dropbox (low usability)"
}

/// <summary>
/// Thrown by <see cref="ProductionScheduler.GenerateAsync"/> when something
/// upstream isn't ready (no SIM batch, no eligible boxes, etc.).
/// </summary>
public sealed class ProductionScheduleException : InvalidOperationException
{
    public ProductionScheduleException(string message) : base(message) { }
}

// ── Service ─────────────────────────────────────────────────────────────

/// <summary>
/// Builds a per-day production schedule on top of a SIM batch.
///
/// Algorithm (proportional + round-robin per div, top-usability first):
///
///   1. Filter eligible boxes (Usability% ≥ MinUsabilityPct).
///   2. Compute per-Division total qty across eligible boxes
///      → DailyDivQuota[Div] = (DivQty / GrandTotal) × DailyTargetQty.
///   3. Sort eligible boxes per division by Usability% DESC, BoxQty DESC.
///   4. For each Day = 1..D:
///        — Round-robin pass over divisions in priority order (largest div
///          first). Each div takes its top remaining box; loop until day's
///          quota is filled or no div has more boxes.
///        — Top-up phase: if day total still under target, take the next
///          best box from any division regardless of div quota.
///   5. Boxes that didn't fit go in with Day = NULL (deferred — surface in UI).
///
/// Cumulative store / division counters are NOT updated against any cap —
/// the production schedule respects whatever SIM allocated and just decides
/// WHEN each box is produced, not how much.
/// </summary>
public class ProductionScheduler(
    IDbContextFactory<LpmDbContext> dbFactory,
    ICurrentUser currentUser)
{
    /// <summary>
    /// Returns the existing production schedule header for a SIM batch, or
    /// <c>null</c> if none exists.
    /// </summary>
    public async Task<LpmSimProductionSchedule?> GetAsync(long batchNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LpmSimProductionSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.LPMBatchNo == batchNo, ct);
    }

    /// <summary>
    /// Quick existence + status check used by callers that only need to know
    /// whether a schedule currently locks the parent SIM batch.
    /// </summary>
    public async Task<(bool Exists, string? Status)> ExistsAsync(long batchNo, CancellationToken ct = default)
    {
        var s = await GetAsync(batchNo, ct);
        return (s is not null, s?.Status);
    }

    /// <summary>
    /// Run the scheduling algorithm on the given SIM batch + inputs and
    /// persist the result (Day on every LPMSIM_Output row + a header row in
    /// LPMSIM_ProductionSchedule). Replaces an existing Draft schedule for
    /// the batch; refuses if an Approved schedule already exists.
    /// </summary>
    public async Task<ProductionScheduleResult> GenerateAsync(
        ProductionScheduleRequest req, CancellationToken ct = default)
    {
        if (req.DailyTargetQty <= 0)
            throw new ProductionScheduleException("Daily Production Target must be greater than 0.");
        if (req.DaysInWeek is < 1 or > 14)
            throw new ProductionScheduleException("Days in week must be between 1 and 14.");
        if (req.MinUsabilityPct is < 0m or > 100m)
            throw new ProductionScheduleException("Min Usability % must be between 0 and 100.");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var batch = await db.LpmSimBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.LPMBatchNo == req.LPMBatchNo, ct)
            ?? throw new ProductionScheduleException($"SIM batch #{req.LPMBatchNo} not found.");

        var existing = await db.LpmSimProductionSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.LPMBatchNo == req.LPMBatchNo, ct);
        if (existing is { Status: "Approved" })
            throw new ProductionScheduleException(
                $"An Approved production schedule already exists for batch #{req.LPMBatchNo}. Delete it first to rerun.");

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        // ── 1. Read all output rows for the batch — one query, in-memory math ──
        // LPMDt is included so we can prioritise LPM-tagged boxes ahead of
        // Non-LPM in the per-day allocation loop below.
        var outputRows = new List<(long Id, string BoxNo, DateTime? LPMDt, string ItemCode, int Qty, string StoreID, int DivCode)>();
        using (var cmd = conn.CreateCommand())
        {
            // DivCode comes from LPM_LocStock first (matches engine), with a
            // fallback to upc_subclass × subclassmaster × Division for items
            // that aren't in LocStock for this country.
            cmd.CommandText = @"
WITH BatchItems AS (
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
)
SELECT s.Id, s.BoxNo, s.LPMDt, s.Itemcode, s.Qty, s.StoreID, ISNULL(id.DivCode, 0) AS DivCode
  FROM dbo.LPMSIM_Output s
  LEFT JOIN ItemDiv id ON id.Itemcode = s.Itemcode
 WHERE s.LPMBatchNo = @batchNo;";
            cmd.Parameters.Add(new SqlParameter("@batchNo", req.LPMBatchNo));
            cmd.Parameters.Add(new SqlParameter("@country", batch.Country));
            cmd.CommandTimeout = 300;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                outputRows.Add((
                    Id:       rdr.GetInt64(0),
                    BoxNo:    rdr.GetString(1),
                    LPMDt:    rdr.IsDBNull(2) ? null : rdr.GetDateTime(2),
                    ItemCode: rdr.GetString(3),
                    Qty:      rdr.GetInt32(4),
                    StoreID:  rdr.GetString(5),
                    DivCode:  rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6)));
            }
        }

        if (outputRows.Count == 0)
            throw new ProductionScheduleException($"SIM batch #{req.LPMBatchNo} has no output rows — generate SIM first.");

        // ── 2. Per-box capacity + warehouse from whboxitems ──
        // Loaded together via a single temp-table query — each box has one
        // warehouse (whboxitems.Warehouse is the box-level field, not per-line).
        var distinctBoxes = outputRows.Select(r => r.BoxNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var boxCapacity   = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var boxWarehouse  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (distinctBoxes.Count > 0)
        {
            using (var ddl = conn.CreateCommand())
            {
                ddl.CommandText = @"
                    IF OBJECT_ID('tempdb..#ps_boxes') IS NOT NULL DROP TABLE #ps_boxes;
                    CREATE TABLE #ps_boxes (BoxNo varchar(25) NOT NULL PRIMARY KEY);";
                await ddl.ExecuteNonQueryAsync(ct);
            }
            using (var dt = new System.Data.DataTable())
            {
                dt.Columns.Add("BoxNo", typeof(string));
                foreach (var b in distinctBoxes) dt.Rows.Add(b);
                using var bulk = new SqlBulkCopy((SqlConnection)conn)
                {
                    DestinationTableName = "#ps_boxes",
                    BatchSize = 5000,
                    BulkCopyTimeout = 60,
                };
                bulk.ColumnMappings.Add("BoxNo", "BoxNo");
                await bulk.WriteToServerAsync(dt, ct);
            }
            using (var cmd = conn.CreateCommand())
            {
                // MAX(Warehouse) is safe because all whboxitems rows for a
                // given BoxNo share the same warehouse value.
                cmd.CommandText = @"
                    SELECT t.BoxNo,
                           ISNULL(SUM(CAST(w.Qty AS bigint)), 0) AS Capacity,
                           MAX(w.Warehouse)                       AS Warehouse
                      FROM #ps_boxes t
                      LEFT JOIN racks.dbo.whboxitems w ON w.BoxNo = t.BoxNo
                     GROUP BY t.BoxNo;";
                cmd.CommandTimeout = 300;
                using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    if (rdr.IsDBNull(0)) continue;
                    var bn = rdr.GetString(0);
                    boxCapacity[bn]  = rdr.IsDBNull(1) ? 0L : rdr.GetInt64(1);
                    boxWarehouse[bn] = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                }
            }
        }

        // ── 3. Aggregate per-box: total qty, dominant div, store count, usability ──
        var priorityWarehouseSet = new HashSet<string>(
            req.PriorityWarehouses ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var boxes = outputRows
            .GroupBy(r => r.BoxNo, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var boxQty = g.Sum(r => (long)r.Qty);
                // Dominant div — div with most qty contribution to the box.
                var divQty = g.GroupBy(r => r.DivCode)
                              .Select(dg => (DivCode: dg.Key, Qty: dg.Sum(r => (long)r.Qty)))
                              .OrderByDescending(x => x.Qty)
                              .ToList();
                var dominantDiv = divQty.FirstOrDefault().DivCode;
                var divCount    = divQty.Count;
                var storeCount  = g.Select(r => r.StoreID).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var capacity    = boxCapacity.GetValueOrDefault(g.Key, 0L);
                var usability   = capacity > 0
                    ? (decimal?)Math.Round((decimal)boxQty * 100m / capacity, 1)
                    : null;
                // A box is "LPM" when any of its lines has a non-null LPMDt
                // (in practice all lines for the same BoxNo share the same
                // LPMDt — a single LPMDt per warehouse box).
                var isLpm = g.Any(r => r.LPMDt.HasValue);
                var warehouse = boxWarehouse.GetValueOrDefault(g.Key, "");
                var isPrio    = priorityWarehouseSet.Count > 0
                             && !string.IsNullOrEmpty(warehouse)
                             && priorityWarehouseSet.Contains(warehouse);
                return new
                {
                    BoxNo               = g.Key,
                    BoxQty              = boxQty,
                    Capacity            = capacity,
                    Usability           = usability,
                    DominantDiv         = dominantDiv,
                    DivCount            = divCount,
                    StoreCount          = storeCount,
                    IsLpm               = isLpm,
                    Warehouse           = warehouse,
                    IsPriorityWarehouse = isPrio,
                };
            })
            .ToList();

        var totalBoxes = boxes.Count;
        var totalQty   = boxes.Sum(b => b.BoxQty);

        // Dropboxes — below the user's min usability threshold.
        var dropboxes = boxes.Where(b => (b.Usability ?? 0m) < req.MinUsabilityPct).ToList();
        var eligibles = boxes.Where(b => (b.Usability ?? 0m) >= req.MinUsabilityPct).ToList();

        var eligibleQty = eligibles.Sum(b => b.BoxQty);

        // ── 4. Per-Division daily quota (proportional share) ──
        var divTotalQty = eligibles
            .GroupBy(b => b.DominantDiv)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.BoxQty));

        var grandTotalElig = divTotalQty.Values.Sum();
        var dailyDivQuota = new Dictionary<int, decimal>();
        if (grandTotalElig > 0)
        {
            foreach (var (div, dq) in divTotalQty)
                dailyDivQuota[div] = (decimal)dq / grandTotalElig * req.DailyTargetQty;
        }

        // Per-division eligible boxes sorted by:
        //   1. IsLpm               DESC — LPM-tagged boxes always first.
        //   2. IsPriorityWarehouse DESC — within LPM/Non-LPM tier, priority
        //                                 warehouses dispatched first.
        //   3. Usab%               DESC — top-utilised boxes first.
        //   4. BoxQty              DESC — final tiebreak.
        // LPM still wins overall (LPM-tagged committed dates outrank any
        // warehouse priority); within LPM (or within Non-LPM), warehouses
        // on the priority list outrank non-priority ones.
        var divQueues = eligibles
            .GroupBy(b => b.DominantDiv)
            .ToDictionary(
                g => g.Key,
                g => new Queue<dynamic>(g.OrderByDescending(b => b.IsLpm)
                                         .ThenByDescending(b => b.IsPriorityWarehouse)
                                         .ThenByDescending(b => b.Usability ?? 0m)
                                         .ThenByDescending(b => b.BoxQty)
                                         .Cast<dynamic>()
                                         .ToList()));

        // Divisions in priority order (largest daily quota first).
        var divsOrdered = dailyDivQuota.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();

        // ── 5. Day-by-day allocation ──
        var assignedDay = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);  // BoxNo → Day
        var allEligibleBoxNos = eligibles.Select(b => b.BoxNo).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int day = 1; day <= req.DaysInWeek; day++)
        {
            var dayQtySoFar      = 0L;
            var dayDivQty        = new Dictionary<int, long>();
            var anyProgressOuter = true;

            // Round-robin passes over divs until day target is met or no
            // div has more eligible boxes.
            while (dayQtySoFar < req.DailyTargetQty && anyProgressOuter)
            {
                anyProgressOuter = false;
                foreach (var div in divsOrdered)
                {
                    if (dayQtySoFar >= req.DailyTargetQty) break;
                    var quota = dailyDivQuota.GetValueOrDefault(div, 0m);
                    var taken = (decimal)dayDivQty.GetValueOrDefault(div, 0L);
                    if (quota > 0 && taken >= quota * 1.20m) continue;   // 20% overshoot tolerance

                    if (!divQueues.TryGetValue(div, out var q) || q.Count == 0) continue;
                    var box = q.Dequeue();
                    string boxNo = box.BoxNo;
                    long   boxQ  = box.BoxQty;
                    int    bDiv  = box.DominantDiv;

                    assignedDay[boxNo] = day;
                    dayQtySoFar += boxQ;
                    dayDivQty[bDiv] = dayDivQty.GetValueOrDefault(bDiv, 0L) + boxQ;
                    anyProgressOuter = true;
                }
            }

            // Top-up: if day still under target, pull the next-best box from
            // any division (ignoring quota) until target met or all divs are
            // empty. Keeps Day 1..D nearly full when one div dries up early.
            //
            // Priority ranking for the head-of-queue contest:
            //   • IsLpm = true  outranks IsLpm = false.
            //   • Within same LPM tier, IsPriorityWarehouse = true outranks false.
            //   • Within same (LPM, PriorityWH) tier, higher Usability% wins.
            // Each div's queue is already sorted LPM→PrioWH→Usability→BoxQty,
            // so the peek() yields the best candidate for that div automatically.
            var anyProgressTopUp = true;
            while (dayQtySoFar < req.DailyTargetQty && anyProgressTopUp)
            {
                anyProgressTopUp = false;
                int?    bestDiv = null;
                bool    bestLpm = false;
                bool    bestPrio = false;
                decimal bestU   = decimal.MinValue;
                foreach (var div in divsOrdered)
                {
                    if (!divQueues.TryGetValue(div, out var q) || q.Count == 0) continue;
                    var head    = q.Peek();
                    bool isLpm  = head.IsLpm;
                    bool isPrio = head.IsPriorityWarehouse;
                    decimal u   = head.Usability ?? 0m;
                    bool better = bestDiv is null
                               || (isLpm  && !bestLpm)
                               || (isLpm == bestLpm && isPrio && !bestPrio)
                               || (isLpm == bestLpm && isPrio == bestPrio && u > bestU);
                    if (better) { bestDiv = div; bestLpm = isLpm; bestPrio = isPrio; bestU = u; }
                }
                if (bestDiv is null) break;
                var b   = divQueues[bestDiv.Value].Dequeue();
                string bn = b.BoxNo;
                long   bq = b.BoxQty;
                int    bd = b.DominantDiv;
                assignedDay[bn] = day;
                dayQtySoFar += bq;
                dayDivQty[bd] = dayDivQty.GetValueOrDefault(bd, 0L) + bq;
                anyProgressTopUp = true;
            }
        }

        // ── 6. Compute deferred (eligible boxes left over after Day D) ──
        var assignedSet  = assignedDay.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deferredBoxNos = allEligibleBoxNos.Except(assignedSet, StringComparer.OrdinalIgnoreCase).ToList();
        var deferredQty   = eligibles.Where(b => deferredBoxNos.Contains(b.BoxNo, StringComparer.OrdinalIgnoreCase))
                                     .Sum(b => b.BoxQty);

        // ── 7. Persist Day on every LPMSIM_Output row + header ──
        //
        // The connection has a pending EF transaction here, so every raw
        // SqlCommand / SqlBulkCopy must be told about it explicitly —
        // otherwise SqlClient throws "BeginExecuteNonQuery requires the
        // command to have a transaction when the connection assigned to
        // the command is in a pending local transaction".
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var sqlTx = (SqlTransaction)tx.GetDbTransaction();

            // Reset Day for the batch (EF — already wired into the transaction).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.LPMSIM_Output SET [Day] = NULL WHERE LPMBatchNo = {req.LPMBatchNo};", ct);

            // Set Day for assigned boxes via a temp table + single UPDATE
            // (cheaper than per-box UPDATE statements).
            if (assignedDay.Count > 0)
            {
                using (var ddl = conn.CreateCommand())
                {
                    ddl.Transaction = sqlTx;
                    ddl.CommandText = @"
                        IF OBJECT_ID('tempdb..#ps_assign') IS NOT NULL DROP TABLE #ps_assign;
                        CREATE TABLE #ps_assign (BoxNo varchar(25) NOT NULL PRIMARY KEY, [Day] int NOT NULL);";
                    await ddl.ExecuteNonQueryAsync(ct);
                }
                using (var dt = new System.Data.DataTable())
                {
                    dt.Columns.Add("BoxNo", typeof(string));
                    dt.Columns.Add("Day",   typeof(int));
                    foreach (var (bn, d) in assignedDay) dt.Rows.Add(bn, d);
                    using var bulk = new SqlBulkCopy((SqlConnection)conn, SqlBulkCopyOptions.Default, sqlTx)
                    {
                        DestinationTableName = "#ps_assign",
                        BatchSize = 10000,
                        BulkCopyTimeout = 120,
                    };
                    bulk.ColumnMappings.Add("BoxNo", "BoxNo");
                    bulk.ColumnMappings.Add("Day",   "Day");
                    await bulk.WriteToServerAsync(dt, ct);
                }
                using (var upd = conn.CreateCommand())
                {
                    upd.Transaction = sqlTx;
                    upd.CommandText = @"
                        UPDATE s SET [Day] = a.[Day]
                          FROM dbo.LPMSIM_Output s
                          INNER JOIN #ps_assign a ON a.BoxNo = s.BoxNo
                         WHERE s.LPMBatchNo = @batchNo;
                        DROP TABLE #ps_assign;";
                    upd.Parameters.Add(new SqlParameter("@batchNo", req.LPMBatchNo));
                    upd.CommandTimeout = 300;
                    await upd.ExecuteNonQueryAsync(ct);
                }
            }

            // Header — replace Draft, refuse Approved (already checked above).
            if (existing is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM dbo.LPMSIM_ProductionSchedule WHERE LPMBatchNo = {req.LPMBatchNo};", ct);
                db.ChangeTracker.Clear();
            }

            var nowTs = DateTime.Now;
            var prioCsv = req.PriorityWarehouses is { Count: > 0 }
                ? string.Join(",", req.PriorityWarehouses
                    .Where(w => !string.IsNullOrWhiteSpace(w))
                    .Select(w => w.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                : null;
            var header = new LpmSimProductionSchedule
            {
                LPMBatchNo         = req.LPMBatchNo,
                DailyTargetQty     = req.DailyTargetQty,
                DaysInWeek         = req.DaysInWeek,
                MinUsabilityPct    = req.MinUsabilityPct,
                Status             = "Draft",
                EligibleBoxes      = eligibles.Count,
                EligibleQty        = eligibleQty,
                ScheduledBoxes     = assignedSet.Count,
                ScheduledQty       = eligibleQty - deferredQty,
                DeferredBoxes      = deferredBoxNos.Count,
                DeferredQty        = deferredQty,
                CreateTS           = nowTs,
                CreatedBy          = string.IsNullOrEmpty(req.User) ? (currentUser?.Name ?? "") : req.User,
                PriorityWarehouses = prioCsv,
            };
            db.LpmSimProductionSchedules.Add(header);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        sw.Stop();
        return new ProductionScheduleResult
        {
            LPMBatchNo     = req.LPMBatchNo,
            EligibleBoxes  = eligibles.Count,
            EligibleQty    = eligibleQty,
            ScheduledBoxes = assignedSet.Count,
            ScheduledQty   = eligibleQty - deferredQty,
            DeferredBoxes  = deferredBoxNos.Count,
            DeferredQty    = deferredQty,
            DropboxBoxes   = dropboxes.Count,
            DropboxQty     = dropboxes.Sum(b => b.BoxQty),
            Duration       = sw.Elapsed,
        };
    }

    /// <summary>
    /// Marks the schedule as Approved. Stores ApprovedTS / ApprovedBy.
    /// </summary>
    public async Task ApproveAsync(long batchNo, string user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.LpmSimProductionSchedules
            .FirstOrDefaultAsync(x => x.LPMBatchNo == batchNo, ct)
            ?? throw new ProductionScheduleException($"No production schedule for batch #{batchNo}.");
        if (s.Status == "Approved")
            throw new ProductionScheduleException($"Schedule #{batchNo} is already approved.");
        s.Status     = "Approved";
        s.ApprovedTS = DateTime.Now;
        s.ApprovedBy = user;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes the production schedule + clears Day on every output row.
    /// Unblocks SIM Generate / Approve / Delete on the parent batch.
    /// </summary>
    public async Task DeleteAsync(long batchNo, string user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Clear Day first (FK CASCADE removes header next).
        db.Database.SetCommandTimeout(300);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.LPMSIM_Output SET [Day] = NULL WHERE LPMBatchNo = {batchNo};", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM dbo.LPMSIM_ProductionSchedule WHERE LPMBatchNo = {batchNo};", ct);
    }

    /// <summary>
    /// Per-day rollup: BoxCount, Qty, distinct Stores/Divisions, avg
    /// usability%. Day = NULL appears as "Unscheduled" (Day = 0).
    /// </summary>
    public async Task<List<ProductionDayRow>> GetDaySummaryAsync(long batchNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        var rows = new List<ProductionDayRow>();
        using var cmd = conn.CreateCommand();
        // BoxAgg = one row per (Box, Day) carrying the box's qty and capacity.
        // DayStores = distinct count of stores touched per day, computed
        //   directly from raw rows (NOT summed from per-box counts — that was
        //   a bug that returned the per-box sum, e.g. 22,825 for Day 1
        //   instead of the actual ~150 stores).
        cmd.CommandText = @"
-- Reporting query — uses READ UNCOMMITTED so a concurrent SIM Generate /
-- Production Schedule run that holds locks on LPMSIM_Output doesn't
-- deadlock the dashboard load. Reports may show in-flight uncommitted
-- state momentarily; that's acceptable for a summary view.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

WITH BoxAgg AS (
    SELECT s.BoxNo,
           ISNULL(s.[Day], 0) AS [Day],
           SUM(CAST(s.Qty AS bigint)) AS BoxQty
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.BoxNo, ISNULL(s.[Day], 0)
),
BoxCap AS (
    SELECT b.BoxNo, ISNULL(SUM(CAST(w.Qty AS bigint)), 0) AS Capacity
      FROM (SELECT DISTINCT BoxNo FROM BoxAgg) b
      LEFT JOIN racks.dbo.whboxitems w ON w.BoxNo = b.BoxNo
     GROUP BY b.BoxNo
),
DayStores AS (
    SELECT ISNULL(s.[Day], 0)         AS [Day],
           COUNT(DISTINCT s.StoreID)  AS StoreCount
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY ISNULL(s.[Day], 0)
)
SELECT main.[Day],
       main.BoxCount,
       main.Qty,
       StoreCount   = ds.StoreCount,
       main.AvgUsability
  FROM (
        SELECT [Day]         = ba.[Day],
               BoxCount      = COUNT(*),
               Qty           = SUM(ba.BoxQty),
               AvgUsability  = AVG(CASE WHEN bc.Capacity > 0 THEN CAST(ba.BoxQty AS decimal(18,4)) * 100 / bc.Capacity ELSE NULL END)
          FROM BoxAgg ba
          LEFT JOIN BoxCap bc ON bc.BoxNo = ba.BoxNo
         GROUP BY ba.[Day]
       ) main
  INNER JOIN DayStores ds ON ds.[Day] = main.[Day]
 ORDER BY main.[Day];";
        cmd.Parameters.Add(new SqlParameter("@batchNo", batchNo));
        cmd.CommandTimeout = 120;
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ProductionDayRow
            {
                Day            = rdr.GetInt32(0),
                BoxCount       = rdr.GetInt32(1),
                Qty            = rdr.IsDBNull(2) ? 0L : rdr.GetInt64(2),
                StoreCount     = rdr.IsDBNull(3) ? 0  : rdr.GetInt32(3),
                AvgUsability   = rdr.IsDBNull(4) ? null : Math.Round(rdr.GetDecimal(4), 1),
            });
        }
        return rows;
    }

    /// <summary>
    /// Per-box detail with Day, usability, store/division counts. Used by
    /// the "Box Detail" tab on the Production Schedule page.
    /// </summary>
    public async Task<List<ProductionBoxRow>> GetBoxDetailAsync(long batchNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        var schedule = await db.LpmSimProductionSchedules.AsNoTracking()
            .FirstOrDefaultAsync(x => x.LPMBatchNo == batchNo, ct);
        var minUsab = schedule?.MinUsabilityPct ?? 0m;

        var rows = new List<ProductionBoxRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
-- Reporting query — READ UNCOMMITTED to avoid deadlocking with concurrent
-- SIM Generate / Production Schedule runs (see GetDaySummaryAsync note).
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

WITH BoxAgg AS (
    SELECT s.BoxNo,
           MAX(s.LPMDt)                       AS LPMDt,
           MAX(s.[Day])                       AS [Day],
           SUM(CAST(s.Qty AS bigint))         AS BoxQty,
           COUNT(DISTINCT s.StoreID)          AS StoreCnt,
           COUNT(DISTINCT s.Itemcode)         AS ItemCnt
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.BoxNo
),
BoxCap AS (
    SELECT b.BoxNo, ISNULL(SUM(CAST(w.Qty AS bigint)), 0) AS Capacity
      FROM (SELECT DISTINCT BoxNo FROM BoxAgg) b
      LEFT JOIN racks.dbo.whboxitems w ON w.BoxNo = b.BoxNo
     GROUP BY b.BoxNo
)
SELECT ba.BoxNo,
       ba.LPMDt,
       ba.[Day],
       ba.BoxQty,
       ISNULL(bc.Capacity, 0)               AS Capacity,
       UsabilityPct = CASE WHEN bc.Capacity > 0
                           THEN CAST(ba.BoxQty AS decimal(18,4)) * 100 / bc.Capacity
                           ELSE NULL END,
       ba.StoreCnt,
       ba.ItemCnt
  FROM BoxAgg ba
  LEFT JOIN BoxCap bc ON bc.BoxNo = ba.BoxNo
 ORDER BY ba.[Day], ba.BoxNo;";
        cmd.Parameters.Add(new SqlParameter("@batchNo", batchNo));
        cmd.CommandTimeout = 300;
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var dayVal = rdr.IsDBNull(2) ? (int?)null : rdr.GetInt32(2);
            var usab   = rdr.IsDBNull(5) ? (decimal?)null : Math.Round(rdr.GetDecimal(5), 1);
            string? status = dayVal.HasValue
                ? "Scheduled"
                : ((usab ?? 0m) < minUsab ? "Dropbox (low usability)" : "Deferred");
            rows.Add(new ProductionBoxRow
            {
                BoxNo         = rdr.GetString(0),
                LPMDt         = rdr.IsDBNull(1) ? null : rdr.GetDateTime(1),
                Day           = dayVal,
                BoxQty        = rdr.IsDBNull(3) ? 0L : rdr.GetInt64(3),
                Capacity      = rdr.IsDBNull(4) ? 0L : rdr.GetInt64(4),
                UsabilityPct  = usab,
                StoreCount    = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6),
                DivisionCount = rdr.IsDBNull(7) ? 0 : rdr.GetInt32(7),
                Status        = status,
            });
        }
        return rows;
    }

    /// <summary>
    /// Per-Division rollup for the schedule. Attributes each LPMSIM_Output
    /// row's qty to its item's Division — so a box containing items from
    /// multiple divisions contributes proportionally to each, not just to
    /// the box's "dominant" division.
    ///
    /// Optional filters:
    ///   • <paramref name="dayFilter"/>:
    ///       null = all rows (default)
    ///       0    = only Day IS NULL ("Unscheduled")
    ///       1..N = only rows with Day = N
    ///   • <paramref name="statusFilter"/>:
    ///       "" or null  = all rows
    ///       "Scheduled" = Day IS NOT NULL
    ///       "Deferred"  = Day IS NULL AND box usability ≥ MinUsabilityPct
    ///       "Dropbox"   = Day IS NULL AND box usability &lt; MinUsabilityPct
    /// </summary>
    public async Task<List<ProductionDivisionRow>> GetDivisionSummaryAsync(
        long batchNo,
        int? dayFilter = null,
        string? statusFilter = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var batch = await db.LpmSimBatches.AsNoTracking()
            .FirstOrDefaultAsync(x => x.LPMBatchNo == batchNo, ct);
        if (batch is null) return new();

        var schedule = await db.LpmSimProductionSchedules.AsNoTracking()
            .FirstOrDefaultAsync(x => x.LPMBatchNo == batchNo, ct);
        var minUsab = schedule?.MinUsabilityPct ?? 0m;

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);

        var rows = new List<ProductionDivisionRow>();
        using var cmd = conn.CreateCommand();
        // BoxUsab = per-box usability % (BoxQty / Capacity × 100). Drives
        //   the Status filter (Deferred vs Dropbox split).
        // FilteredOutput = LPMSIM_Output filtered by the optional Day /
        //   Status criteria. Everything downstream rolls up from here.
        // DivBox  = per (Div, Box) qty rollup — used for distinct box counts.
        // DivStore = distinct count of stores per division (NOT summed from
        //   per-box StoreCnts — that double-counts when one store receives
        //   qty for the same division from multiple boxes).
        // EomDiv  = Σ Merch Need (Week) and (Day) from LPM_EOM_Output for the
        //   batch's (Country, Year, Month). EOM totals are independent of
        //   the Day/Status filters — they always show the period plan.
        cmd.CommandText = @"
-- Reporting query — READ UNCOMMITTED to avoid deadlocking with concurrent
-- SIM Generate / Production Schedule runs (see GetDaySummaryAsync note).
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

WITH BatchItems AS (
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
BoxUsab AS (
    SELECT s.BoxNo,
           BoxQty   = SUM(CAST(s.Qty AS bigint)),
           Capacity = ISNULL((SELECT SUM(CAST(w.Qty AS bigint))
                                FROM racks.dbo.whboxitems w
                               WHERE w.BoxNo = s.BoxNo), 0)
      FROM dbo.LPMSIM_Output s
     WHERE s.LPMBatchNo = @batchNo
     GROUP BY s.BoxNo
),
BoxStatus AS (
    SELECT BoxNo,
           UsabilityPct = CASE WHEN Capacity > 0
                                THEN CAST(BoxQty AS decimal(18,4)) * 100 / Capacity
                                ELSE 0 END
      FROM BoxUsab
),
FilteredOutput AS (
    SELECT s.*
      FROM dbo.LPMSIM_Output s
      INNER JOIN BoxStatus bs ON bs.BoxNo = s.BoxNo
     WHERE s.LPMBatchNo = @batchNo
       -- Day filter: NULL = all, 0 = Unscheduled (Day IS NULL), 1..N = specific day
       AND (@dayFilter IS NULL
            OR (@dayFilter = 0 AND s.[Day] IS NULL)
            OR s.[Day] = @dayFilter)
       -- Status filter: empty = all, else Scheduled / Deferred / Dropbox
       AND (@status = ''
            OR (@status = 'Scheduled' AND s.[Day] IS NOT NULL)
            OR (@status = 'Deferred'  AND s.[Day] IS NULL AND bs.UsabilityPct >= @minUsab)
            OR (@status = 'Dropbox'   AND s.[Day] IS NULL AND bs.UsabilityPct <  @minUsab))
),
DivBox AS (
    SELECT id.DivCode, fo.BoxNo,
           Qty            = SUM(CAST(fo.Qty AS bigint)),
           ScheduledQty   = SUM(CASE WHEN fo.[Day] IS NOT NULL THEN CAST(fo.Qty AS bigint) ELSE 0 END),
           DeferredQty    = SUM(CASE WHEN fo.[Day] IS NULL     THEN CAST(fo.Qty AS bigint) ELSE 0 END),
           HasScheduled   = MAX(CASE WHEN fo.[Day] IS NOT NULL THEN 1 ELSE 0 END),
           HasDeferred    = MAX(CASE WHEN fo.[Day] IS NULL     THEN 1 ELSE 0 END)
      FROM FilteredOutput fo
      INNER JOIN ItemDiv id ON id.Itemcode = fo.Itemcode
     GROUP BY id.DivCode, fo.BoxNo
),
DivStore AS (
    SELECT id.DivCode,
           StoreCount = COUNT(DISTINCT fo.StoreID)
      FROM FilteredOutput fo
      INNER JOIN ItemDiv id ON id.Itemcode = fo.Itemcode
     GROUP BY id.DivCode
),
EomDiv AS (
    SELECT eo.DivCode,
           MerchNeedWeek = SUM(CAST(ISNULL(eo.MerchNeedWeek, 0) AS bigint)),
           MerchNeedDay  = SUM(CAST(ISNULL(eo.MerchNeedDay,  0) AS bigint))
      FROM dbo.LPM_EOM_Output eo
     WHERE eo.Country = @country AND eo.Year1 = @y AND eo.Month1 = @m
     GROUP BY eo.DivCode
)
SELECT db.DivCode,
       div.Division                                    AS DivisionName,
       TotalQty       = SUM(db.Qty),
       ScheduledQty   = SUM(db.ScheduledQty),
       DeferredQty    = SUM(db.DeferredQty),
       BoxCount       = COUNT(*),
       ScheduledBoxes = SUM(db.HasScheduled),
       DeferredBoxes  = SUM(CASE WHEN db.HasScheduled = 0 AND db.HasDeferred = 1 THEN 1 ELSE 0 END),
       StoreCount     = ISNULL(MAX(ds.StoreCount), 0),
       MerchNeedWeek  = ISNULL(MAX(ed.MerchNeedWeek), 0),
       MerchNeedDay   = ISNULL(MAX(ed.MerchNeedDay),  0)
  FROM DivBox db
  LEFT JOIN dbo.Division div ON div.DivCode = db.DivCode
  LEFT JOIN DivStore ds      ON ds.DivCode  = db.DivCode
  LEFT JOIN EomDiv   ed      ON ed.DivCode  = db.DivCode
 GROUP BY db.DivCode, div.Division
 ORDER BY div.Division;";
        cmd.Parameters.Add(new SqlParameter("@batchNo",   batchNo));
        cmd.Parameters.Add(new SqlParameter("@country",   batch.Country));
        cmd.Parameters.Add(new SqlParameter("@y",         batch.RunYear));
        cmd.Parameters.Add(new SqlParameter("@m",         batch.RunMonth));
        cmd.Parameters.Add(new SqlParameter("@dayFilter", (object?)dayFilter ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@status",    statusFilter ?? ""));
        cmd.Parameters.Add(new SqlParameter("@minUsab",   minUsab));
        cmd.CommandTimeout = 300;
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ProductionDivisionRow
            {
                DivCode        = rdr.IsDBNull(0)  ? 0  : rdr.GetInt32(0),
                DivisionName   = rdr.IsDBNull(1)  ? "" : rdr.GetString(1),
                TotalQty       = rdr.IsDBNull(2)  ? 0L : rdr.GetInt64(2),
                ScheduledQty   = rdr.IsDBNull(3)  ? 0L : rdr.GetInt64(3),
                DeferredQty    = rdr.IsDBNull(4)  ? 0L : rdr.GetInt64(4),
                BoxCount       = rdr.IsDBNull(5)  ? 0  : rdr.GetInt32(5),
                ScheduledBoxes = rdr.IsDBNull(6)  ? 0  : rdr.GetInt32(6),
                DeferredBoxes  = rdr.IsDBNull(7)  ? 0  : rdr.GetInt32(7),
                StoreCount     = rdr.IsDBNull(8)  ? 0  : rdr.GetInt32(8),
                MerchNeedWeek  = rdr.IsDBNull(9)  ? 0L : rdr.GetInt64(9),
                MerchNeedDay   = rdr.IsDBNull(10) ? 0L : rdr.GetInt64(10),
            });
        }

        // Compute PctOfTotal as ScheduledQty / sum of all ScheduledQty.
        // Done in C# so the math doesn't depend on SQL window-function support.
        var grandScheduled = rows.Sum(r => r.ScheduledQty);
        if (grandScheduled > 0)
        {
            foreach (var r in rows)
                r.PctOfTotal = Math.Round((decimal)r.ScheduledQty * 100m / grandScheduled, 1);
        }

        return rows;
    }
}
