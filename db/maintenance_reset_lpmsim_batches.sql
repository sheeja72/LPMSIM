-- ============================================================================
-- MAINTENANCE — Reset LPM SIM batch tables
--
-- Purpose: wipe all SIM batch data and reseed LPMSIM_Batch.LPMBatchNo so the
-- next Generate produces batch #1.
--
-- Tables cleared:
--   LPMSIM_Output           (allocation lines)
--   LPMSIM_AllocTrace       (every (Box × Item × Store) decision)
--   LPMSIM_StoreItemBalance (per-(Store, Item) snapshot)
--   LPMSIM_StoreDivBalance  (per-(Store, Div) snapshot)
--   LPMSIM_BoxBalance       (per-(Box) usability snapshot)
--   LPMSIM_Batch            (parent — IDENTITY reseeded)
--   LPMSIM_Batch_Backup     (archived headers — clear by default)
--   LPMSIM_Output_Backup    (archived lines   — clear by default)
--
-- NOT touched (preserved):
--   LPM_EOM_Output          (EOM batch — separate, no batch number)
--   LPM_SimItemSkuMax       (per-period snapshot — keep latest build)
--   LPMUser, LPMUserRole    (users)
--   LPM_SKUMaxRule          (rules)
--   LPM_StoreDivAccess      (store/div overrides)
--   AuditLog                (audit trail — never auto-purged)
--
-- Idempotent: re-running on already-empty tables is a no-op.
-- Wrapped in a transaction — rolls back on any failure.
-- ============================================================================
SET XACT_ABORT ON;
SET NOCOUNT ON;

USE LPMSIM;
GO

-- ── Knobs (set to 0 to skip a step) ───────────────────────────────────────
DECLARE @WipeBackups bit  = 1;   -- clears LPMSIM_*_Backup tables
DECLARE @ResetIdent  bit  = 1;   -- DBCC CHECKIDENT … RESEED so next batch = 1
DECLARE @ShowCounts  bit  = 1;   -- print row counts before / after

PRINT '─── BEFORE ─────────────────────────────────────────────────';
IF @ShowCounts = 1
BEGIN
    SELECT
        Tbl       = 'LPMSIM_Batch',            Rows = COUNT_BIG(*) FROM dbo.LPMSIM_Batch UNION ALL
    SELECT 'LPMSIM_Output',                    COUNT_BIG(*) FROM dbo.LPMSIM_Output UNION ALL
    SELECT 'LPMSIM_AllocTrace',                COUNT_BIG(*) FROM dbo.LPMSIM_AllocTrace UNION ALL
    SELECT 'LPMSIM_StoreItemBalance',          COUNT_BIG(*) FROM dbo.LPMSIM_StoreItemBalance UNION ALL
    SELECT 'LPMSIM_StoreDivBalance',           COUNT_BIG(*) FROM dbo.LPMSIM_StoreDivBalance UNION ALL
    SELECT 'LPMSIM_BoxBalance',                COUNT_BIG(*) FROM dbo.LPMSIM_BoxBalance UNION ALL
    SELECT 'LPMSIM_Batch_Backup',              COUNT_BIG(*) FROM dbo.LPMSIM_Batch_Backup UNION ALL
    SELECT 'LPMSIM_Output_Backup',             COUNT_BIG(*) FROM dbo.LPMSIM_Output_Backup;
END

BEGIN TRANSACTION;

BEGIN TRY
    -- 1) Children first. TRUNCATE is set-based and orders of magnitude
    --    faster than DELETE on multi-million-row tables. The FK CASCADE
    --    rules don't trigger because TRUNCATE skips them — that's fine
    --    here since we're emptying both sides anyway.
    --
    --    Note: TRUNCATE on a table that is REFERENCED by another FK is
    --    not allowed, but children referencing a parent (the case here)
    --    truncate freely.
    TRUNCATE TABLE dbo.LPMSIM_AllocTrace;
    TRUNCATE TABLE dbo.LPMSIM_Output;
    TRUNCATE TABLE dbo.LPMSIM_StoreItemBalance;
    TRUNCATE TABLE dbo.LPMSIM_StoreDivBalance;
    TRUNCATE TABLE dbo.LPMSIM_BoxBalance;
    PRINT '  ✓ Cleared 5 child tables.';

    -- 2) Parent. Cannot TRUNCATE because FK constraints reference it.
    --    DELETE works, then RESEED so next IDENTITY value = 1.
    DELETE FROM dbo.LPMSIM_Batch;
    PRINT '  ✓ Cleared LPMSIM_Batch.';

    IF @ResetIdent = 1
    BEGIN
        DBCC CHECKIDENT('dbo.LPMSIM_Batch', RESEED, 0);
        -- Next INSERT will produce LPMBatchNo = 1.
        PRINT '  ✓ Reseeded LPMSIM_Batch IDENTITY to 0 (next batch = 1).';
    END

    -- 3) Optional: archive backup tables.
    IF @WipeBackups = 1
    BEGIN
        TRUNCATE TABLE dbo.LPMSIM_Output_Backup;
        TRUNCATE TABLE dbo.LPMSIM_Batch_Backup;
        PRINT '  ✓ Cleared LPMSIM_Output_Backup + LPMSIM_Batch_Backup.';
    END

    COMMIT TRANSACTION;
    PRINT '─── COMMITTED ──────────────────────────────────────────────';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    PRINT '─── ROLLED BACK ────────────────────────────────────────────';
    THROW;
END CATCH

PRINT '─── AFTER ──────────────────────────────────────────────────';
IF @ShowCounts = 1
BEGIN
    SELECT
        Tbl       = 'LPMSIM_Batch',            Rows = COUNT_BIG(*) FROM dbo.LPMSIM_Batch UNION ALL
    SELECT 'LPMSIM_Output',                    COUNT_BIG(*) FROM dbo.LPMSIM_Output UNION ALL
    SELECT 'LPMSIM_AllocTrace',                COUNT_BIG(*) FROM dbo.LPMSIM_AllocTrace UNION ALL
    SELECT 'LPMSIM_StoreItemBalance',          COUNT_BIG(*) FROM dbo.LPMSIM_StoreItemBalance UNION ALL
    SELECT 'LPMSIM_StoreDivBalance',           COUNT_BIG(*) FROM dbo.LPMSIM_StoreDivBalance UNION ALL
    SELECT 'LPMSIM_BoxBalance',                COUNT_BIG(*) FROM dbo.LPMSIM_BoxBalance UNION ALL
    SELECT 'LPMSIM_Batch_Backup',              COUNT_BIG(*) FROM dbo.LPMSIM_Batch_Backup UNION ALL
    SELECT 'LPMSIM_Output_Backup',             COUNT_BIG(*) FROM dbo.LPMSIM_Output_Backup;
END

-- Show next IDENTITY value (helps visually confirm reseed).
DECLARE @next bigint;
SELECT @next = ISNULL(IDENT_CURRENT('dbo.LPMSIM_Batch'), 0) + 1;
PRINT '  Next LPMSIM_Batch.LPMBatchNo will be: ' + CAST(@next AS varchar(20));

-- ============================================================================
-- OPTIONAL — reclaim disk space (run AFTER the above, separately)
--
-- TRUNCATE/DELETE frees pages back to the table for reuse but doesn't shrink
-- the data file. The space is recovered as new SIM batches grow into it.
-- If you really need to return space to the OS (e.g., to free SSD on a
-- small VM), uncomment the block below — but be aware:
--   • SHRINK causes index fragmentation; rebuild indexes afterwards.
--   • Don't routinely shrink — only when you're way over-provisioned.
-- ============================================================================
/*
USE LPMSIM;
DBCC SHRINKFILE (LPMSIM, 0, NOTRUNCATE);
DBCC SHRINKFILE (LPMSIM, 0, TRUNCATEONLY);

-- Rebuild indexes on the affected tables to undo shrink-induced fragmentation.
ALTER INDEX ALL ON dbo.LPMSIM_Batch            REBUILD;
ALTER INDEX ALL ON dbo.LPMSIM_Output           REBUILD;
ALTER INDEX ALL ON dbo.LPMSIM_AllocTrace       REBUILD;
ALTER INDEX ALL ON dbo.LPMSIM_StoreItemBalance REBUILD;
ALTER INDEX ALL ON dbo.LPMSIM_StoreDivBalance  REBUILD;
ALTER INDEX ALL ON dbo.LPMSIM_BoxBalance       REBUILD;
*/
