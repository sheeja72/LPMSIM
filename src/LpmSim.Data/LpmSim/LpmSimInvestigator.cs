using LpmSim.Data.Warehouse;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data.LpmSim;

/// <summary>
/// One investigation result. Business-friendly Q&amp;A for the SIM run output.
///   <see cref="Headline"/>  — one-sentence answer.
///   <see cref="Findings"/>  — bullet points (rules, observations).
///   <see cref="Metrics"/>   — labelled numbers shown as cards.
///   <see cref="Columns"/> + <see cref="Rows"/> — supporting tabular data.
/// </summary>
public record InvestigationResult(
    string Question,
    string Headline,
    List<string> Findings,
    List<(string Label, string Value, string Accent)> Metrics,
    List<string> Columns,
    List<List<string>> Rows);

/// <summary>
/// Q&amp;A console backend. Each method takes the batch + (optionally) a focus
/// (store, item, box) and returns a structured answer the UI can render.
/// </summary>
public class LpmSimInvestigator(IDbContextFactory<LpmDbContext> dbFactory)
{
    private async Task<(SqlConnection conn, LpmDbContext db)> OpenAsync(CancellationToken ct)
    {
        var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (SqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        return (conn, db);
    }

    // ====================================================================
    // Q1 — Why didn't (Store, Division) get more SIM Qty?
    //      Looks at: EOM headroom, SKUMax cap state, item availability in
    //      boxes, SKIP trace counts (if VerboseTrace was on).
    // ====================================================================
    public async Task<InvestigationResult> StoreDivisionGapAsync(long batchNo, string storeID, int divCode, CancellationToken ct = default)
    {
        var (conn, db) = await OpenAsync(ct);
        await using var _ = db;

        // 1. Plan + state at this (Store, Div)
        decimal targetEom = 0; int divSoh = 0; int? skuMax = null;
        int p1n = 0, p1r = 0, p2n = 0, p2r = 0, total = 0; decimal divBalRemain = 0;
        string divName = "", storeName = "";

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TargetEOM = ISNULL(eo.TargetEOM, 0),
                       SKUMax    = eo.SKUMax,
                       DivName   = div.Division,
                       StoreName = ds.PBFullname,
                       DivSOH    = ISNULL(sdb.DivSOH, 0),
                       P1n       = ISNULL(sdb.P1_NormalAlloc, 0),
                       P1r       = ISNULL(sdb.P1_RR, 0),
                       P2n       = ISNULL(sdb.P2_NormalAlloc, 0),
                       P2r       = ISNULL(sdb.P2_RR, 0),
                       Tot       = ISNULL(sdb.TotalAlloc, 0),
                       DivBalRemain = ISNULL(sdb.DivBalanceRemaining, eo.TargetEOM)
                  FROM dbo.LPMSIM_Batch b
                  CROSS JOIN dbo.Division div
                  LEFT JOIN dbo.LPM_EOM_Output eo ON eo.Country = b.Country AND eo.StoreID = @s
                                                   AND eo.DivCode = @d AND eo.Year1 = b.RunYear AND eo.Month1 = b.RunMonth
                  LEFT JOIN dbo.LPMSIM_StoreDivBalance sdb ON sdb.LPMBatchNo = b.LPMBatchNo
                                                            AND sdb.StoreID = @s AND sdb.DivCode = @d
                  LEFT JOIN dbo.DataSettings ds ON ds.StoreID = @s
                 WHERE b.LPMBatchNo = @bn AND div.DivCode = @d;";
            cmd.Parameters.AddWithValue("@bn", batchNo);
            cmd.Parameters.AddWithValue("@s", storeID);
            cmd.Parameters.AddWithValue("@d", divCode);
            cmd.CommandTimeout = 60;
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                targetEom    = rdr.IsDBNull(0) ? 0 : rdr.GetDecimal(0);
                skuMax       = rdr.IsDBNull(1) ? null : rdr.GetInt32(1);
                divName      = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                storeName    = rdr.IsDBNull(3) ? "" : rdr.GetString(3);
                divSoh       = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
                p1n          = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5);
                p1r          = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6);
                p2n          = rdr.IsDBNull(7) ? 0 : rdr.GetInt32(7);
                p2r          = rdr.IsDBNull(8) ? 0 : rdr.GetInt32(8);
                total        = rdr.IsDBNull(9) ? 0 : rdr.GetInt32(9);
                divBalRemain = rdr.IsDBNull(10) ? 0 : rdr.GetDecimal(10);
            }
        }

        // 2. SKIP counts from trace, if available
        int skipSku = 0, skipTgt = 0, skipNoEom = 0, skipNoDiv = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT Decision, Cnt = COUNT(*)
                  FROM dbo.LPMSIM_AllocTrace
                 WHERE LPMBatchNo = @bn AND StoreID = @s AND DivCode = @d
                 GROUP BY Decision;";
            cmd.Parameters.AddWithValue("@bn", batchNo);
            cmd.Parameters.AddWithValue("@s", storeID);
            cmd.Parameters.AddWithValue("@d", divCode);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var dec = rdr.GetString(0); var c = rdr.GetInt32(1);
                switch (dec)
                {
                    case "SKIP_SKUMAX": skipSku   = c; break;
                    case "SKIP_TARGET": skipTgt   = c; break;
                    case "SKIP_NO_EOM": skipNoEom = c; break;
                    case "SKIP_NO_DIV": skipNoDiv = c; break;
                }
            }
        }

        // 3. Top items the engine considered for this (Store, Div) — what got allocated
        var rows = new List<List<string>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TOP 15 sib.ItemCode,
                       sib.SKUMax, sib.SOH_Item, sib.TotalAlloc, sib.SkuBalanceRemaining,
                       Phases = CONCAT('P1n=',sib.P1_NormalAlloc,' P1r=',sib.P1_RR,' P2n=',sib.P2_NormalAlloc,' P2r=',sib.P2_RR)
                  FROM dbo.LPMSIM_StoreItemBalance sib
                 WHERE sib.LPMBatchNo = @bn AND sib.StoreID = @s AND sib.DivCode = @d
                 ORDER BY sib.TotalAlloc DESC, sib.ItemCode;";
            cmd.Parameters.AddWithValue("@bn", batchNo);
            cmd.Parameters.AddWithValue("@s", storeID);
            cmd.Parameters.AddWithValue("@d", divCode);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new List<string> {
                    rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                    rdr.IsDBNull(1) ? "" : rdr.GetInt32(1).ToString("N0"),
                    rdr.IsDBNull(2) ? "" : rdr.GetInt32(2).ToString("N0"),
                    rdr.IsDBNull(3) ? "" : rdr.GetInt32(3).ToString("N0"),
                    rdr.IsDBNull(4) ? "" : rdr.GetInt32(4).ToString("N0"),
                    rdr.IsDBNull(5) ? "" : rdr.GetString(5),
                });
            }
        }

        // Build the narrative
        var headroom = targetEom - divSoh;
        var pctFilled = headroom > 0 ? (decimal)total * 100m / headroom : 0m;

        var findings = new List<string>();
        if (targetEom == 0)
            findings.Add($"⛔ No EOM plan exists for ({storeID}, {divName}). Run **EOM Generate** for this country/period.");
        else if (headroom <= 0)
            findings.Add($"⚠ Store SOH ({divSoh:N0}) already meets or exceeds the EOM plan ({targetEom:N0}). Engine has zero headroom — no normal allocation possible.");
        else if (total >= headroom)
            findings.Add($"✅ Headroom fully used: {total:N0} / {headroom:N0} ({pctFilled:0.0}%). The plan was met.");
        else
            findings.Add($"📊 Headroom: {headroom:N0}; allocated: {total:N0} ({pctFilled:0.0}%). Gap: {headroom - total:N0} units.");

        if (skuMax is null || skuMax == 0)
            findings.Add($"⚠ **SKUMax = {skuMax?.ToString() ?? "NULL"}** for this (Store, Div). No item could pass the SKU cap test in normal allocation. Add SKUMax rules for division **{divName}** and re-run EOM.");
        else
            findings.Add($"SKUMax per item at this store: {skuMax:N0}. Items already at/above this SOH are blocked from normal alloc.");

        if (skipSku > 0 || skipTgt > 0 || skipNoEom > 0 || skipNoDiv > 0)
            findings.Add($"Trace decisions for this (Store, Div): SKIP_SKUMAX={skipSku:N0}, SKIP_TARGET={skipTgt:N0}, SKIP_NO_EOM={skipNoEom:N0}, SKIP_NO_DIV={skipNoDiv:N0}.");
        else
            findings.Add("No SKIP-level trace rows — either Verbose Trace was off when generating, or no candidate items appeared in eligible boxes for this division.");

        if (p1r > 0 || p2r > 0)
            findings.Add($"Round-robin contributed {p1r + p2r:N0} units of the {total:N0} total ({(decimal)(p1r + p2r) * 100m / Math.Max(total, 1):0.0}%).");

        var headline = total == 0
            ? $"({storeName}, {divName}) received 0 units. See findings below."
            : $"({storeName}, {divName}) received {total:N0} units against {headroom:N0} of plan headroom ({pctFilled:0.0}% filled).";

        var metrics = new List<(string, string, string)>
        {
            ("Target EOM",    targetEom.ToString("N0"),  "lpm-accent-blue"),
            ("Div SOH",       divSoh.ToString("N0"),     "lpm-accent-purple"),
            ("Headroom",      headroom.ToString("N0"),   headroom > 0 ? "lpm-accent-green" : "lpm-accent-rose"),
            ("Allocated",     total.ToString("N0"),      "lpm-accent-amber"),
            ("Gap",           Math.Max(0, headroom - total).ToString("N0"), "lpm-accent-rose"),
            ("SKUMax/Item",   skuMax?.ToString("N0") ?? "—", skuMax > 0 ? "lpm-accent-green" : "lpm-accent-rose"),
        };

        return new InvestigationResult(
            Question:  $"Why did ({storeID}, {divName}) get {total:N0} of {headroom:N0} headroom?",
            Headline:  headline,
            Findings:  findings,
            Metrics:   metrics,
            Columns:   new List<string> { "ItemCode", "SKUMax", "SOH(Item)", "Allocated", "SkuBalRemain", "Phases" },
            Rows:      rows);
    }

    // ====================================================================
    // Q2 — Why is this Box under-utilised?
    // ====================================================================
    public async Task<InvestigationResult> BoxUtilisationAsync(long batchNo, string boxNo, CancellationToken ct = default)
    {
        var (conn, db) = await OpenAsync(ct);
        await using var _ = db;

        // 1.14.61 — Country-aware whboxitems source. Look up the batch's
        // country first so the BoxQty subquery hits the right table.
        var country = await db.LpmSimBatches.AsNoTracking()
            .Where(b => b.LPMBatchNo == batchNo)
            .Select(b => b.Country)
            .FirstOrDefaultAsync(ct) ?? "UAE";
        var whSrc = await WhBoxItemsSource.ResolveAsync((SqlConnection)conn, country, ct);

        long boxQty = 0, simQty = 0, rrQty = 0; int distinctStores = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT BoxQty   = (SELECT SUM(CAST(Qty AS bigint)) FROM {whSrc} WHERE BoxNo = @b),
                       SimQty   = ISNULL(SUM(CAST(o.Qty AS bigint)), 0),
                       RrQty    = ISNULL(SUM(CASE WHEN o.IsRoundRobin=1 THEN CAST(o.Qty AS bigint) ELSE 0 END), 0),
                       Stores   = COUNT(DISTINCT o.StoreID)
                  FROM dbo.LPMSIM_Output o
                 WHERE o.LPMBatchNo = @bn AND o.BoxNo = @b;";
            cmd.Parameters.AddWithValue("@bn", batchNo);
            cmd.Parameters.AddWithValue("@b", boxNo);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                boxQty          = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0);
                simQty          = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1);
                rrQty           = rdr.IsDBNull(2) ? 0 : rdr.GetInt64(2);
                distinctStores  = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3);
            }
        }

        var rows = new List<List<string>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TOP 30 t.ItemCode,
                       LineQty  = MAX(t.LineQty),
                       Allocated= SUM(CASE WHEN t.Decision IN ('ALLOC','ALLOC_RR') THEN t.Take ELSE 0 END),
                       SkipSku  = SUM(CASE WHEN t.Decision = 'SKIP_SKUMAX' THEN 1 ELSE 0 END),
                       SkipTgt  = SUM(CASE WHEN t.Decision = 'SKIP_TARGET' THEN 1 ELSE 0 END),
                       NoDiv    = SUM(CASE WHEN t.Decision = 'SKIP_NO_DIV' THEN 1 ELSE 0 END),
                       NoEom    = SUM(CASE WHEN t.Decision = 'SKIP_NO_EOM' THEN 1 ELSE 0 END)
                  FROM dbo.LPMSIM_AllocTrace t
                 WHERE t.LPMBatchNo = @bn AND t.BoxNo = @b
                 GROUP BY t.ItemCode
                 ORDER BY MAX(t.LineQty) DESC;";
            cmd.Parameters.AddWithValue("@bn", batchNo);
            cmd.Parameters.AddWithValue("@b", boxNo);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new List<string> {
                    rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                    rdr.IsDBNull(1) ? "" : rdr.GetInt32(1).ToString("N0"),
                    rdr.IsDBNull(2) ? "" : rdr.GetInt32(2).ToString("N0"),
                    rdr.IsDBNull(3) ? "" : rdr.GetInt32(3).ToString("N0"),
                    rdr.IsDBNull(4) ? "" : rdr.GetInt32(4).ToString("N0"),
                    rdr.IsDBNull(5) ? "" : rdr.GetInt32(5).ToString("N0"),
                    rdr.IsDBNull(6) ? "" : rdr.GetInt32(6).ToString("N0"),
                });
            }
        }

        var pct      = boxQty > 0 ? (decimal)simQty * 100m / boxQty : 0m;
        var findings = new List<string>();

        if (boxQty == 0)
            findings.Add($"⛔ Box **{boxNo}** has no rows in racks.whboxitems — it may not exist or may be filtered out (PalletCategory ≠ ELIGIBLE, ShopEligible = E, etc.).");
        else
        {
            findings.Add($"Total box qty: {boxQty:N0}. Allocated: {simQty:N0} ({pct:0.0}%) across {distinctStores} stores.");
            if (rrQty > 0) findings.Add($"Round-robin contributed {rrQty:N0} of the allocated units ({(decimal)rrQty * 100m / Math.Max(simQty, 1):0.0}%).");
            if (rows.Count > 0 && rows.Any(r => int.Parse(r[5]) > 0))
                findings.Add($"⚠ Some items in this box have no division mapping (SKIP_NO_DIV) — those qty are lost.");
            if (rows.Count > 0 && rows.Sum(r => int.Parse(r[3])) > rows.Sum(r => int.Parse(r[2])))
                findings.Add($"SKIP_SKUMAX dominates — most candidate stores are already at SKU cap. Increase SKUMax rules or improve item mix.");
        }

        var metrics = new List<(string, string, string)>
        {
            ("Box Qty",       boxQty.ToString("N0"),         "lpm-accent-blue"),
            ("SIM Qty",       simQty.ToString("N0"),         "lpm-accent-amber"),
            ("Usability %",   pct.ToString("0.0") + "%",     pct >= 80 ? "lpm-accent-green" : pct >= 50 ? "lpm-accent-amber" : "lpm-accent-rose"),
            ("RR Qty",        rrQty.ToString("N0"),          "lpm-accent-purple"),
            ("Stores",        distinctStores.ToString("N0"), "lpm-accent-cyan"),
        };

        return new InvestigationResult(
            Question:  $"Why is box {boxNo} only {pct:0.0}% utilised?",
            Headline:  boxQty == 0 ? $"Box {boxNo} not found in source." : $"Box {boxNo} placed {simQty:N0} of {boxQty:N0} units ({pct:0.0}%) across {distinctStores} stores.",
            Findings:  findings,
            Metrics:   metrics,
            Columns:   new List<string> { "ItemCode", "Line Qty", "Allocated", "SKIP_SKUMAX", "SKIP_TARGET", "SKIP_NO_DIV", "SKIP_NO_EOM" },
            Rows:      rows);
    }

    // ====================================================================
    // Q3 — What did this Store receive in this run?
    // ====================================================================
    public async Task<InvestigationResult> StoreOverviewAsync(long batchNo, string storeID, CancellationToken ct = default)
    {
        var (conn, db) = await OpenAsync(ct);
        await using var _ = db;

        var rows = new List<List<string>>();
        long total = 0, totalRr = 0; int divs = 0;
        string storeName = "";

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT div.Division                          AS DivName,
                       sdb.DivCode,
                       SOH        = ISNULL(sdb.DivSOH, 0),
                       TgtEOM     = ISNULL(sdb.TargetEOM, 0),
                       Headroom   = ISNULL(sdb.TargetEOM - sdb.DivSOH, 0),
                       SimQty     = ISNULL(sdb.TotalAlloc, 0),
                       RrQty      = ISNULL(sdb.P1_RR + sdb.P2_RR, 0),
                       FillPct    = CASE WHEN sdb.TargetEOM - sdb.DivSOH > 0
                                         THEN CAST(sdb.TotalAlloc * 100.0 / (sdb.TargetEOM - sdb.DivSOH) AS decimal(6,1))
                                         ELSE NULL END,
                       StoreName  = ds.PBFullname
                  FROM dbo.LPMSIM_StoreDivBalance sdb
                  LEFT JOIN dbo.Division div ON div.DivCode = sdb.DivCode
                  LEFT JOIN dbo.DataSettings ds ON ds.StoreID = sdb.StoreID
                 WHERE sdb.LPMBatchNo = @bn AND sdb.StoreID = @s
                 ORDER BY sdb.TotalAlloc DESC;";
            cmd.Parameters.AddWithValue("@bn", batchNo);
            cmd.Parameters.AddWithValue("@s", storeID);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                if (string.IsNullOrEmpty(storeName)) storeName = rdr.IsDBNull(8) ? "" : rdr.GetString(8);
                var divName = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                var divc    = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                var soh     = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
                var tgtEom  = rdr.IsDBNull(3) ? 0m: rdr.GetDecimal(3);
                var headrm  = rdr.IsDBNull(4) ? 0m: rdr.GetDecimal(4);
                var sim     = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5);
                var rr      = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6);
                var pct     = rdr.IsDBNull(7) ? (decimal?)null : rdr.GetDecimal(7);
                rows.Add(new List<string> {
                    divName,
                    soh.ToString("N0"),
                    tgtEom.ToString("N0"),
                    headrm.ToString("N0"),
                    sim.ToString("N0"),
                    rr  > 0 ? rr.ToString("N0") : "—",
                    pct.HasValue ? pct.Value.ToString("0.0") + "%" : "—",
                });
                total += sim; totalRr += rr; divs++;
            }
        }

        var findings = new List<string>();
        if (rows.Count == 0)
            findings.Add($"⛔ Store **{storeID}** has no per-(Store, Div) snapshot for this batch. Either the store wasn't in scope, or the batch produced no allocations to it.");
        else
        {
            findings.Add($"{divs} divisions touched. Total allocated: {total:N0} (RR: {totalRr:N0}, {(decimal)totalRr * 100m / Math.Max(total, 1):0.0}%).");
            var top = rows.Take(3).Select(r => r[0]).ToList();
            if (top.Count > 0) findings.Add($"Top divisions by SIM Qty: {string.Join(", ", top)}.");
            var emptyDivs = rows.Count(r => int.Parse(r[4].Replace(",", "")) == 0);
            if (emptyDivs > 0) findings.Add($"{emptyDivs} divisions saw 0 allocation (despite plan headroom).");
        }

        return new InvestigationResult(
            Question:  $"What did {storeID} receive?",
            Headline:  rows.Count == 0 ? $"No allocations for {storeID}." : $"{storeName} ({storeID}) received {total:N0} units across {divs} divisions ({totalRr:N0} via RR).",
            Findings:  findings,
            Metrics:   new List<(string, string, string)>
            {
                ("Total SIM",   total.ToString("N0"),     "lpm-accent-amber"),
                ("RR Qty",      totalRr.ToString("N0"),   "lpm-accent-purple"),
                ("Divisions",   divs.ToString("N0"),      "lpm-accent-blue"),
                ("RR %",        ((decimal)totalRr * 100m / Math.Max(total, 1)).ToString("0.0") + "%", "lpm-accent-cyan"),
            },
            Columns:   new List<string> { "Division", "SOH", "Tgt EOM", "Headroom", "SIM Qty", "RR Qty", "Fill %" },
            Rows:      rows);
    }

    // ====================================================================
    // Q4 — What's blocking allocation overall in this batch?
    // ====================================================================
    public async Task<InvestigationResult> BatchBlockersAsync(long batchNo, CancellationToken ct = default)
    {
        var (conn, db) = await OpenAsync(ct);
        await using var _ = db;

        long inputQty = 0, allocQty = 0, rrQty = 0;
        int totalLines = 0; int totalBoxes = 0; int totalStores = 0;
        long discardedNoDiv = 0, discardedNoEom = 0;
        int skipSku = 0, skipTgt = 0;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT
                    AllocQty       = SUM(CAST(Qty AS bigint)),
                    RrQty          = SUM(CASE WHEN IsRoundRobin = 1 THEN CAST(Qty AS bigint) ELSE 0 END),
                    Lines          = COUNT(*),
                    Boxes          = COUNT(DISTINCT BoxNo),
                    Stores         = COUNT(DISTINCT StoreID)
                  FROM dbo.LPMSIM_Output
                 WHERE LPMBatchNo = @bn;";
            cmd.Parameters.AddWithValue("@bn", batchNo);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                allocQty    = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0);
                rrQty       = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1);
                totalLines  = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
                totalBoxes  = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3);
                totalStores = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT Decision,
                       Cnt = COUNT(*),
                       LineQty = SUM(CAST(LineQty AS bigint))
                  FROM dbo.LPMSIM_AllocTrace
                 WHERE LPMBatchNo = @bn
                 GROUP BY Decision;";
            cmd.Parameters.AddWithValue("@bn", batchNo);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var dec  = rdr.GetString(0);
                var cnt  = rdr.GetInt32(1);
                var lq   = rdr.IsDBNull(2) ? 0 : rdr.GetInt64(2);
                switch (dec)
                {
                    case "SKIP_NO_DIV": discardedNoDiv = lq;  break;
                    case "SKIP_NO_EOM": discardedNoEom = lq;  break;
                    case "SKIP_SKUMAX": skipSku        = cnt; break;
                    case "SKIP_TARGET": skipTgt        = cnt; break;
                }
            }
        }

        // Per-division headroom utilisation
        var rows = new List<List<string>>();
        using (var cmd = conn.CreateCommand())
        {
            // The SKUMax-zero count comes from LPMSIM_StoreItemBalance (one row
            // per Store-Item — hundreds per Store-Div). Pre-aggregate it to one
            // row per (Store, Div) BEFORE joining to LPMSIM_StoreDivBalance, so
            // the SUM of (TargetEOM - DivSOH) doesn't get multiplied by the
            // item count. Without this CTE, Headroom for a div with 100 items
            // per store reads 100× the real number.
            cmd.CommandText = @"
                WITH SkuMaxZeros AS (
                    SELECT LPMBatchNo, StoreID, DivCode,
                           ZeroCount = SUM(CASE WHEN ISNULL(NULLIF(SKUMax,0),0) = 0 THEN 1 ELSE 0 END)
                      FROM dbo.LPMSIM_StoreItemBalance
                     WHERE LPMBatchNo = @bn
                     GROUP BY LPMBatchNo, StoreID, DivCode
                )
                SELECT TOP 25 div.Division,
                       Headroom = SUM(CASE WHEN sdb.TargetEOM > sdb.DivSOH THEN sdb.TargetEOM - sdb.DivSOH ELSE 0 END),
                       Filled   = SUM(sdb.TotalAlloc),
                       FillPct  = CASE WHEN SUM(CASE WHEN sdb.TargetEOM > sdb.DivSOH THEN sdb.TargetEOM - sdb.DivSOH ELSE 0 END) > 0
                                       THEN CAST(SUM(sdb.TotalAlloc) * 100.0 / SUM(CASE WHEN sdb.TargetEOM > sdb.DivSOH THEN sdb.TargetEOM - sdb.DivSOH ELSE 0 END) AS decimal(6,1))
                                       ELSE NULL END,
                       NeedSkuMax = SUM(ISNULL(skz.ZeroCount, 0))
                  FROM dbo.LPMSIM_StoreDivBalance sdb
                  LEFT JOIN dbo.Division div ON div.DivCode = sdb.DivCode
                  LEFT JOIN SkuMaxZeros skz ON skz.LPMBatchNo = sdb.LPMBatchNo
                                            AND skz.StoreID  = sdb.StoreID
                                            AND skz.DivCode  = sdb.DivCode
                 WHERE sdb.LPMBatchNo = @bn
                 GROUP BY div.Division
                HAVING SUM(CASE WHEN sdb.TargetEOM > sdb.DivSOH THEN sdb.TargetEOM - sdb.DivSOH ELSE 0 END) > 0
                 ORDER BY (SUM(CASE WHEN sdb.TargetEOM > sdb.DivSOH THEN sdb.TargetEOM - sdb.DivSOH ELSE 0 END) - SUM(sdb.TotalAlloc)) DESC;";
            cmd.Parameters.AddWithValue("@bn", batchNo);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new List<string> {
                    rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                    (rdr.IsDBNull(1) ? 0m : rdr.GetDecimal(1)).ToString("N0"),
                    (rdr.IsDBNull(2) ? 0  : rdr.GetInt32(2)).ToString("N0"),
                    rdr.IsDBNull(3) ? "—" : rdr.GetDecimal(3).ToString("0.0") + "%",
                    (rdr.IsDBNull(4) ? 0  : rdr.GetInt32(4)).ToString("N0"),
                });
            }
        }

        var findings = new List<string>();
        findings.Add($"Allocated **{allocQty:N0}** units across {totalLines:N0} lines, {totalBoxes:N0} boxes, {totalStores} stores ({rrQty:N0} via RR = {(decimal)rrQty*100m/Math.Max(allocQty,1):0.0}%).");

        if (discardedNoDiv > 0)
            findings.Add($"⚠ **{discardedNoDiv:N0}** units of qty discarded due to **no division mapping** (items not in racks.upc_subclass / not in LocStock.DivCode). Investigate the items.");
        if (discardedNoEom > 0)
            findings.Add($"⚠ **{discardedNoEom:N0}** units of qty discarded — items map to a division **with no EOM rows** for this run.");
        if (skipSku > 0)
            findings.Add($"SKIP_SKUMAX hit {skipSku:N0} times — stores frequently at the per-item SKU cap. Consider raising SKUMax rules.");
        if (skipTgt > 0)
            findings.Add($"SKIP_TARGET hit {skipTgt:N0} times — store divisions frequently at the EOM cap. Increase Planned EOM if more push is needed.");

        if (rows.Count > 0)
            findings.Add($"Top under-filled divisions are listed below — those are where adding SKUMax rules / boosting Planned EOM would help most.");

        return new InvestigationResult(
            Question:  "What's blocking allocation in this batch?",
            Headline:  $"Batch #{batchNo}: {allocQty:N0} units allocated. Top blockers: SKIP_SKUMAX={skipSku:N0}, SKIP_TARGET={skipTgt:N0}, NoDiv={discardedNoDiv:N0} qty discarded.",
            Findings:  findings,
            Metrics:   new List<(string, string, string)>
            {
                ("Allocated",       allocQty.ToString("N0"),       "lpm-accent-amber"),
                ("RR Qty",          rrQty.ToString("N0"),          "lpm-accent-purple"),
                ("Discarded NoDiv", discardedNoDiv.ToString("N0"), "lpm-accent-rose"),
                ("Discarded NoEom", discardedNoEom.ToString("N0"), "lpm-accent-rose"),
                ("SKIP_SKUMAX",     skipSku.ToString("N0"),        "lpm-accent-blue"),
                ("SKIP_TARGET",     skipTgt.ToString("N0"),        "lpm-accent-blue"),
            },
            Columns:   new List<string> { "Division", "Headroom", "Filled", "Fill %", "Stores w/ SKUMax=0" },
            Rows:      rows);
    }
}
