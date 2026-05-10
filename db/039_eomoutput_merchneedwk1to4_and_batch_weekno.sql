-- ============================================================================
-- Migration 039 — Per-week Merch Need columns + SIM batch WeekNo
--
-- 1) LPM_EOM_Output gains four nullable int columns (MerchNeedWeek1..4).
--    Filled by the EomCalculator at Approve time using the new formula:
--
--      MerchNeedWeekN = (TargetEOM − SOH) / 4
--                     + (TargetSales × SplitPct[N] / 100)        for N = 1..4
--
--    The legacy MerchNeedWeek column stays put — it now mirrors
--    MerchNeedWeek1 so existing readers (ADM, ProductionScheduler, the
--    Reports queries) continue to work without a wider refactor.
--
-- 2) LPMSIM_Batch gains WeekNo (tinyint NULL). SIM Generate now requires
--    the planner to pick a week (1..4); the chosen week is stamped onto
--    the batch and drives the allocator's MerchNeedWeek cap (it reads
--    MerchNeedWeek{N} per the chosen week instead of the legacy column).
--
--    NULL on existing pre-migration batches is treated as "Week 1" by the
--    UI for display purposes. No re-allocation happens automatically.
-- ============================================================================
SET XACT_ABORT ON;
GO

----------------------------------------------------------------------
-- 1) LPM_EOM_Output: add MerchNeedWeek1..4
----------------------------------------------------------------------
IF COL_LENGTH('dbo.LPM_EOM_Output', 'MerchNeedWeek1') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD MerchNeedWeek1 int NULL;
    PRINT 'LPM_EOM_Output: added MerchNeedWeek1';
END
ELSE
    PRINT 'LPM_EOM_Output.MerchNeedWeek1 already exists';
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'MerchNeedWeek2') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD MerchNeedWeek2 int NULL;
    PRINT 'LPM_EOM_Output: added MerchNeedWeek2';
END
ELSE
    PRINT 'LPM_EOM_Output.MerchNeedWeek2 already exists';
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'MerchNeedWeek3') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD MerchNeedWeek3 int NULL;
    PRINT 'LPM_EOM_Output: added MerchNeedWeek3';
END
ELSE
    PRINT 'LPM_EOM_Output.MerchNeedWeek3 already exists';
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'MerchNeedWeek4') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD MerchNeedWeek4 int NULL;
    PRINT 'LPM_EOM_Output: added MerchNeedWeek4';
END
ELSE
    PRINT 'LPM_EOM_Output.MerchNeedWeek4 already exists';
GO

----------------------------------------------------------------------
-- 2) LPMSIM_Batch: add WeekNo
----------------------------------------------------------------------
IF COL_LENGTH('dbo.LPMSIM_Batch', 'WeekNo') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch ADD WeekNo tinyint NULL;
    PRINT 'LPMSIM_Batch: added WeekNo';
END
ELSE
    PRINT 'LPMSIM_Batch.WeekNo already exists';
GO

PRINT 'Migration 039 complete.';
