-- ============================================================================
-- Migration 026 — Add SOH, MerchNeedMonth, MerchNeedWeek to LPM_EOM_Output
--
-- SOH (per Store × Division) is the location-level stock from
-- racks.dbo.LPM_LocStock summed across items for that division. Already
-- used internally by SIM Generate; EOM now persists it on output so
-- planners can read it directly without a join.
--
-- Merch Need is the open-to-receive quantity — what the warehouse must
-- ship to the store this period to end at the EOM target after selling
-- TargetSales:
--
--    MerchNeedMonth = TargetEOM − SOH + TargetSales
--    MerchNeedWeek  = MerchNeedMonth / 4   (4 production weeks per month)
--
-- Replaces the legacy "EOM Balance = TargetEOM − SOH" view used by SIM.
-- All three columns are NULL-able so legacy rows (pre-migration) read as
-- NULL — the EOM Calculator backfills them on the next Approve.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'SOH') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD SOH int NULL;
    PRINT 'LPM_EOM_Output: added SOH';
END
ELSE
    PRINT 'LPM_EOM_Output.SOH already exists';
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'MerchNeedMonth') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD MerchNeedMonth int NULL;
    PRINT 'LPM_EOM_Output: added MerchNeedMonth';
END
ELSE
    PRINT 'LPM_EOM_Output.MerchNeedMonth already exists';
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'MerchNeedWeek') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD MerchNeedWeek int NULL;
    PRINT 'LPM_EOM_Output: added MerchNeedWeek';
END
ELSE
    PRINT 'LPM_EOM_Output.MerchNeedWeek already exists';
GO

PRINT 'Migration 026 complete.';
