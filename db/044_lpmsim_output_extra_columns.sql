-- ============================================================================
-- Migration 044 — add Season / BoxQty / BoxItemQty / UsabilityPct / DivCode
-- columns to dbo.LPMSIM_Output (+ backup table)
--
-- Per-row context that previously required joining whboxitems + the division
-- master chain (upc_subclass × subclassmaster × Division). Surfaces:
--   • Season       — 'S' or 'W' from whboxitems.Season (item-level seasonality)
--   • BoxQty       — total qty in the source box (same value on every row of
--                    the same BoxNo within a batch)
--   • BoxItemQty   — source qty of THIS item in the box (distinct from
--                    LPMSIM_Output.Qty which is the allocated qty to a store)
--   • UsabilityPct — per-box: SUM(LPMSIM_Output.Qty for this BoxNo) / BoxQty × 100.
--                    Same value repeated on every row of the same box.
--   • DivCode      — item's division (from upc_subclass → subclassmaster → Division)
--
-- All columns are NULL-able and additive. Existing rows are backfilled at
-- the end of this migration:
--   • UAE batches  → JOIN to racks.dbo.whboxitems (the historical source)
--   • Non-UAE batches → stay NULL by default. Run the backfill block at the
--                       bottom manually per country once DataName is confirmed.
--
-- Backup-table backfill is intentionally skipped — backup rows are archived
-- snapshots and the backfill would have to chase historical whboxitems state
-- which may have rotated. The columns exist on the backup table so backup-
-- then-restore flows don't fail on shape mismatch.
--
-- Idempotent: every step is guarded by COL_LENGTH / IS NULL checks so the
-- script can be re-run safely.
-- ============================================================================
SET XACT_ABORT ON;
GO

-- ============================================================================
-- Step 1: ALTER TABLE on the main table
-- ============================================================================
IF OBJECT_ID('dbo.LPMSIM_Output', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.LPMSIM_Output', 'Season') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output ADD Season char(1) NULL;
        PRINT 'Added LPMSIM_Output.Season';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output', 'BoxQty') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output ADD BoxQty bigint NULL;
        PRINT 'Added LPMSIM_Output.BoxQty';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output', 'BoxItemQty') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output ADD BoxItemQty int NULL;
        PRINT 'Added LPMSIM_Output.BoxItemQty';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output', 'UsabilityPct') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output ADD UsabilityPct decimal(5,2) NULL;
        PRINT 'Added LPMSIM_Output.UsabilityPct';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output', 'DivCode') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output ADD DivCode int NULL;
        PRINT 'Added LPMSIM_Output.DivCode';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output', 'SKUMax') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output ADD SKUMax int NULL;
        PRINT 'Added LPMSIM_Output.SKUMax';
    END
END
GO

-- ============================================================================
-- Step 2: ALTER TABLE on the backup table (same columns)
-- ============================================================================
IF OBJECT_ID('dbo.LPMSIM_Output_Backup', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.LPMSIM_Output_Backup', 'Season') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output_Backup ADD Season char(1) NULL;
        PRINT 'Added LPMSIM_Output_Backup.Season';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output_Backup', 'BoxQty') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output_Backup ADD BoxQty bigint NULL;
        PRINT 'Added LPMSIM_Output_Backup.BoxQty';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output_Backup', 'BoxItemQty') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output_Backup ADD BoxItemQty int NULL;
        PRINT 'Added LPMSIM_Output_Backup.BoxItemQty';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output_Backup', 'UsabilityPct') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output_Backup ADD UsabilityPct decimal(5,2) NULL;
        PRINT 'Added LPMSIM_Output_Backup.UsabilityPct';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output_Backup', 'DivCode') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output_Backup ADD DivCode int NULL;
        PRINT 'Added LPMSIM_Output_Backup.DivCode';
    END
    IF COL_LENGTH('dbo.LPMSIM_Output_Backup', 'SKUMax') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Output_Backup ADD SKUMax int NULL;
        PRINT 'Added LPMSIM_Output_Backup.SKUMax';
    END
END
GO

-- ============================================================================
-- Step 3: Backfill UAE historical batches from racks.dbo.whboxitems.
-- Each UPDATE is guarded by "WHERE <newcol> IS NULL" so re-runs are no-ops.
-- ============================================================================

-- 3a) Season + BoxItemQty (one round-trip via JOIN on BoxNo + ItemCode)
PRINT 'Backfilling Season + BoxItemQty for UAE batches…';
UPDATE o
   SET o.Season     = CASE WHEN UPPER(ISNULL(w.Season, '')) = 'W' THEN 'W' ELSE 'S' END,
       o.BoxItemQty = CAST(ISNULL(w.Qty, 0) AS int)
  FROM dbo.LPMSIM_Output o
  INNER JOIN dbo.LPMSIM_Batch b ON b.LPMBatchNo = o.LPMBatchNo
  INNER JOIN racks.dbo.whboxitems w ON w.BoxNo = o.BoxNo AND w.ItemCode = o.Itemcode
 WHERE b.Country = 'UAE'
   AND (o.Season IS NULL OR o.BoxItemQty IS NULL);
PRINT CONCAT('Rows updated (Season + BoxItemQty): ', @@ROWCOUNT);
GO

-- 3b) BoxQty (per-box aggregate)
PRINT 'Backfilling BoxQty for UAE batches…';
;WITH BoxAgg AS (
    SELECT BoxNo, SUM(CAST(Qty AS bigint)) AS BoxQty
      FROM racks.dbo.whboxitems
     GROUP BY BoxNo
)
UPDATE o
   SET o.BoxQty = ba.BoxQty
  FROM dbo.LPMSIM_Output o
  INNER JOIN dbo.LPMSIM_Batch b ON b.LPMBatchNo = o.LPMBatchNo
  INNER JOIN BoxAgg ba ON ba.BoxNo = o.BoxNo
 WHERE b.Country = 'UAE'
   AND o.BoxQty IS NULL;
PRINT CONCAT('Rows updated (BoxQty): ', @@ROWCOUNT);
GO

-- 3c) DivCode — global lookup via upc_subclass × subclassmaster × Division.
-- Same logic the SKU Max Build uses. MIN(DivCode) breaks ties when an item
-- maps to multiple subclasses (rare).
PRINT 'Backfilling DivCode (global, country-independent)…';
;WITH ItemDiv AS (
    SELECT u.itemcode, MIN(d.DivCode) AS DivCode
      FROM Datareporting.dbo.upc_subclass    u
      INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
      INNER JOIN dbo.Division                 d  ON LTRIM(RTRIM(d.Division)) = LTRIM(RTRIM(sm.Division))
     GROUP BY u.itemcode
)
UPDATE o
   SET o.DivCode = id.DivCode
  FROM dbo.LPMSIM_Output o
  INNER JOIN ItemDiv id ON id.itemcode = o.Itemcode
 WHERE o.DivCode IS NULL;
PRINT CONCAT('Rows updated (DivCode): ', @@ROWCOUNT);
GO

-- 3d) SKUMax — looked up from dbo.LPM_SimItemSkuMax. After migration 045
-- the table is keyed by (StoreID, ItemCode) only, so this JOIN is a clean
-- 2-column match. For DBs still on pre-045 schema this UPDATE will see
-- multiple rows per (Store, Item) and pick MAX (defensive — same convention
-- the C# LoadItemSkuMaxAsync uses).
PRINT 'Backfilling SKUMax (global, country-independent)…';
;WITH ItemMax AS (
    SELECT StoreID, ItemCode, MAX(SKUMax) AS SKUMax
      FROM dbo.LPM_SimItemSkuMax
     GROUP BY StoreID, ItemCode
)
UPDATE o
   SET o.SKUMax = im.SKUMax
  FROM dbo.LPMSIM_Output o
  INNER JOIN ItemMax im ON im.StoreID = o.StoreID AND im.ItemCode = o.Itemcode
 WHERE o.SKUMax IS NULL;
PRINT CONCAT('Rows updated (SKUMax): ', @@ROWCOUNT);
GO

-- 3e) UsabilityPct — per-box: SUM(allocated Qty) / BoxQty × 100. Same value
-- repeated on every row of the same (LPMBatchNo, BoxNo). Runs LAST so BoxQty
-- is already populated.
PRINT 'Backfilling UsabilityPct…';
;WITH BoxAlloc AS (
    SELECT LPMBatchNo, BoxNo, SUM(CAST(Qty AS bigint)) AS Allocated
      FROM dbo.LPMSIM_Output
     GROUP BY LPMBatchNo, BoxNo
)
UPDATE o
   SET o.UsabilityPct = CAST((ba.Allocated * 100.0) / o.BoxQty AS decimal(5,2))
  FROM dbo.LPMSIM_Output o
  INNER JOIN BoxAlloc ba ON ba.LPMBatchNo = o.LPMBatchNo AND ba.BoxNo = o.BoxNo
 WHERE o.UsabilityPct IS NULL
   AND o.BoxQty IS NOT NULL
   AND o.BoxQty > 0;
PRINT CONCAT('Rows updated (UsabilityPct): ', @@ROWCOUNT);
GO

-- ============================================================================
-- Non-UAE backfill — RUN MANUALLY per country once you've confirmed the
-- DataName in bfldata.dbo.DataSettings. Example for a country whose data
-- ships in BFLBahrain.dbo.WHBoxItemsExport:
--
--   UPDATE o
--      SET o.Season     = CASE WHEN UPPER(ISNULL(w.Season, '')) = 'W' THEN 'W' ELSE 'S' END,
--          o.BoxItemQty = CAST(ISNULL(w.Qty, 0) AS int)
--     FROM dbo.LPMSIM_Output o
--     INNER JOIN dbo.LPMSIM_Batch b ON b.LPMBatchNo = o.LPMBatchNo
--     INNER JOIN BFLBahrain.dbo.WHBoxItemsExport w ON w.BoxNo = o.BoxNo AND w.ItemCode = o.Itemcode
--    WHERE b.Country = 'Bahrain'
--      AND (o.Season IS NULL OR o.BoxItemQty IS NULL);
--
-- Then repeat 3b (BoxQty) and 3d (UsabilityPct) blocks scoped to that country.
-- DivCode (3c) is country-independent and is already done.
-- ============================================================================

PRINT 'Migration 044 complete.';
