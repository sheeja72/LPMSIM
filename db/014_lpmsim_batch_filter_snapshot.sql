-- ============================================================================
-- Migration 014 — Persist the filters used at Generate time on LPMSIM_Batch
-- so the Result preview always shows what produced the batch (not what's
-- currently ticked in the checkboxes). Mirror columns on the backup table.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPMSIM_Batch','Sources') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch
        ADD Sources varchar(20) NULL,            -- 'LPM' / 'Non-LPM' / 'LPM+Non-LPM'
            Seasons varchar(20) NULL,            -- 'Summer' / 'Winter' / 'Summer+Winter'
            OverrideUsabilityPct int NULL;
    PRINT 'LPMSIM_Batch: added Sources / Seasons / OverrideUsabilityPct';
END
GO

IF COL_LENGTH('dbo.LPMSIM_Batch_Backup','Sources') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch_Backup
        ADD Sources varchar(20) NULL,
            Seasons varchar(20) NULL,
            OverrideUsabilityPct int NULL;
    PRINT 'LPMSIM_Batch_Backup: added Sources / Seasons / OverrideUsabilityPct';
END
GO

PRINT 'Migration 014 complete.';
