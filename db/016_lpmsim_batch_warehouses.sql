-- ============================================================================
-- Migration 016 — Snapshot the Warehouse(s) selected at Generate time on the
-- LPMSIM_Batch row. Stored as a comma-separated list for simplicity (we don't
-- expect large numbers of warehouses, ~7 today). Mirror on the backup.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPMSIM_Batch','Warehouses') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch ADD Warehouses varchar(200) NULL;
    PRINT 'LPMSIM_Batch: added Warehouses';
END
GO

IF COL_LENGTH('dbo.LPMSIM_Batch_Backup','Warehouses') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch_Backup ADD Warehouses varchar(200) NULL;
    PRINT 'LPMSIM_Batch_Backup: added Warehouses';
END
GO

PRINT 'Migration 016 complete.';
