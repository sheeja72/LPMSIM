-- ============================================================================
-- Migration 028 — IsOverride flag on SIM allocation rows
--
-- Phase 1b / Phase 2b round-robin now runs in OVERRIDE mode when a box's
-- post-normal usability % crosses the user-defined "Box %" threshold:
-- it bypasses SKU Max AND Merch Need (Week) caps to push the box toward
-- 100% utilisation. Each row produced this way is tagged IsOverride = 1
-- so reports can roll up "Override Qty" separately from regular SIM Qty.
--
-- Adds columns to:
--   dbo.LPMSIM_Output         (final allocation lines)
--   dbo.LPMSIM_Output_Backup  (archive — keeps historical override flag)
--   dbo.LPMSIM_AllocTrace     (per-attempt trace)
--
-- All defaults are 0 / false so existing rows continue to read as
-- non-override.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPMSIM_Output', 'IsOverride') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output
        ADD IsOverride bit NOT NULL CONSTRAINT DF_LPMSIM_Output_IsOverride DEFAULT (0);
    PRINT 'LPMSIM_Output: added IsOverride';
END
ELSE
    PRINT 'LPMSIM_Output.IsOverride already exists';
GO

IF COL_LENGTH('dbo.LPMSIM_Output_Backup', 'IsOverride') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output_Backup
        ADD IsOverride bit NOT NULL CONSTRAINT DF_LPMSIM_Output_Backup_IsOverride DEFAULT (0);
    PRINT 'LPMSIM_Output_Backup: added IsOverride';
END
ELSE
    PRINT 'LPMSIM_Output_Backup.IsOverride already exists';
GO

IF COL_LENGTH('dbo.LPMSIM_AllocTrace', 'IsOverride') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_AllocTrace
        ADD IsOverride bit NOT NULL CONSTRAINT DF_LPMSIM_AllocTrace_IsOverride DEFAULT (0);
    PRINT 'LPMSIM_AllocTrace: added IsOverride';
END
ELSE
    PRINT 'LPMSIM_AllocTrace.IsOverride already exists';
GO

PRINT 'Migration 028 complete.';
