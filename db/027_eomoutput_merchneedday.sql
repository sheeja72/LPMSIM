-- ============================================================================
-- Migration 027 — Add MerchNeedDay to LPM_EOM_Output
--
-- Daily slice of Merch Need (Week), divided by a fixed 6 (production
-- days per week). Reference / planning metric — the actual production
-- scheduler picks its own days/week (6 or 7) per run.
--
--    MerchNeedDay = MerchNeedWeek / 6
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'MerchNeedDay') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD MerchNeedDay int NULL;
    PRINT 'LPM_EOM_Output: added MerchNeedDay';
END
ELSE
    PRINT 'LPM_EOM_Output.MerchNeedDay already exists';
GO

PRINT 'Migration 027 complete.';
