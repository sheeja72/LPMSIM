-- ============================================================================
-- Migration 019 — Add FillStrategy snapshot column to LPMSIM_Batch
--
-- Records which Phase-1a/2a fill strategy produced this batch:
--   'EqualPerStore'  — 1 unit per store per cycle (default, fairest by qty)
--   'EqualFillRate'  — give next unit to the store with lowest current
--                      FillRate% (fairest by Division-level fill share)
--
-- The column is nullable so old batches (pre-migration) round-trip cleanly.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPMSIM_Batch', 'FillStrategy') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch
        ADD FillStrategy varchar(20) NULL;
    PRINT 'LPMSIM_Batch: added FillStrategy';
END
ELSE
    PRINT 'LPMSIM_Batch.FillStrategy already exists';
GO

-- Mirror on the backup table so Approve→Backup round-trips include the column.
IF OBJECT_ID('dbo.LPMSIM_Batch_Backup','U') IS NOT NULL
   AND COL_LENGTH('dbo.LPMSIM_Batch_Backup', 'FillStrategy') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch_Backup
        ADD FillStrategy varchar(20) NULL;
    PRINT 'LPMSIM_Batch_Backup: added FillStrategy';
END
GO

PRINT 'Migration 019 complete.';
