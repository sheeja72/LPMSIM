-- ============================================================================
-- Migration 041 — add PalletNo column to dbo.LPMSIM_Output (+ backup table)
--
-- Persists the source PalletNo on every allocation line so direct SSMS queries
-- on LPMSIM_Output show pallet-level info without joining whboxitems. The
-- backup table mirrors the main table's schema, so the column is added there
-- too — otherwise the backup-then-restore flow would fail on the next run.
--
-- Additive, NULL-able. Existing rows keep NULL on this column; new allocations
-- after 1.14.12 deploys will populate it from whboxitems.PalletNo. The report
-- views (SIM Boxes / Item Details / Box Detail report) read PalletNo via a
-- JOIN to whboxitems regardless, so backfill is NOT required for the UI to
-- display PalletNo on historical batches.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPMSIM_Output', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.LPMSIM_Output', 'PalletNo') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output ADD PalletNo varchar(50) NULL;
    PRINT 'Added PalletNo column to dbo.LPMSIM_Output';
END
ELSE IF COL_LENGTH('dbo.LPMSIM_Output', 'PalletNo') IS NOT NULL
    PRINT 'dbo.LPMSIM_Output.PalletNo already exists';

GO

IF OBJECT_ID('dbo.LPMSIM_Output_Backup', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.LPMSIM_Output_Backup', 'PalletNo') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output_Backup ADD PalletNo varchar(50) NULL;
    PRINT 'Added PalletNo column to dbo.LPMSIM_Output_Backup';
END
ELSE IF COL_LENGTH('dbo.LPMSIM_Output_Backup', 'PalletNo') IS NOT NULL
    PRINT 'dbo.LPMSIM_Output_Backup.PalletNo already exists';

GO

PRINT 'Migration 041 complete.';
