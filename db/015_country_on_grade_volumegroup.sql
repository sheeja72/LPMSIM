-- ============================================================================
-- Migration 015 — Add Country to LPM_StoreGrade and LPM_VolumeGroup so each
-- territory can configure its own grade share-% / markup-% and volume-group
-- splits. Existing rows are tagged with 'UAE' (the only country today) so the
-- change is non-breaking.
-- PK is widened to (Country, GradeCode/GroupCode).
-- ============================================================================
SET XACT_ABORT ON;
GO

-- ----- LPM_StoreGrade -----------------------------------------------------
IF COL_LENGTH('dbo.LPM_StoreGrade','Country') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_StoreGrade ADD Country varchar(20) NULL;
    PRINT 'LPM_StoreGrade: added Country';
END
GO
-- Default existing rows to UAE so they stay valid.
UPDATE dbo.LPM_StoreGrade SET Country = 'UAE' WHERE Country IS NULL;
GO
ALTER TABLE dbo.LPM_StoreGrade ALTER COLUMN Country varchar(20) NOT NULL;
GO

-- Recreate PK as (Country, GradeCode).
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'PK_LPM_StoreGrade' AND object_id = OBJECT_ID('dbo.LPM_StoreGrade'))
BEGIN
    ALTER TABLE dbo.LPM_StoreGrade DROP CONSTRAINT PK_LPM_StoreGrade;
    PRINT 'Dropped old PK_LPM_StoreGrade';
END
GO
ALTER TABLE dbo.LPM_StoreGrade
    ADD CONSTRAINT PK_LPM_StoreGrade PRIMARY KEY CLUSTERED (Country, GradeCode);
PRINT 'Re-added PK_LPM_StoreGrade (Country, GradeCode)';
GO

-- ----- LPM_VolumeGroup ----------------------------------------------------
IF COL_LENGTH('dbo.LPM_VolumeGroup','Country') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_VolumeGroup ADD Country varchar(20) NULL;
    PRINT 'LPM_VolumeGroup: added Country';
END
GO
UPDATE dbo.LPM_VolumeGroup SET Country = 'UAE' WHERE Country IS NULL;
GO
ALTER TABLE dbo.LPM_VolumeGroup ALTER COLUMN Country varchar(20) NOT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'PK_LPM_VolumeGroup' AND object_id = OBJECT_ID('dbo.LPM_VolumeGroup'))
BEGIN
    ALTER TABLE dbo.LPM_VolumeGroup DROP CONSTRAINT PK_LPM_VolumeGroup;
    PRINT 'Dropped old PK_LPM_VolumeGroup';
END
GO
ALTER TABLE dbo.LPM_VolumeGroup
    ADD CONSTRAINT PK_LPM_VolumeGroup PRIMARY KEY CLUSTERED (Country, GroupCode);
PRINT 'Re-added PK_LPM_VolumeGroup (Country, GroupCode)';
GO

PRINT 'Migration 015 complete.';
