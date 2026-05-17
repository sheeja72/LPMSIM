-- ============================================================================
-- Migration 051 — LPM_VolumeGroup goes per-(Country, Division, GroupCode)
--
-- 1.14.39 — Volume Groups previously applied to ALL divisions in a country
-- equally. The same 25/20/20/15/20 share % applied whether the planner was
-- bucketing ACCESSORIES, BAGS or BFL SERVICES. Per the new requirement, each
-- division now carries its own bucket distribution so e.g. ACCESSORIES can
-- be 0/0/100/0/0 while BAGS keeps the legacy split.
--
-- Schema change:
--   • Add DivCode column (int NOT NULL)
--   • Drop old PK (Country, GroupCode) and FK from LPM_SKUMaxRule
--   • Add new PK (Country, DivCode, GroupCode)
--   • Add FK to dbo.Division(DivCode)
--
-- Data strategy: WIPE + RELOAD (user's chosen migration path).
-- All existing LPM_VolumeGroup rows are DELETEd as part of this migration.
-- After this runs, EOM Generate WILL FAIL until the planner re-uploads
-- Volume Groups via the Uploads page with the new Division column.
--
-- ⚠ COORDINATION REQUIRED — apply this migration ONLY when:
--   1) 1.14.39 has been deployed (new Upload page accepts the Division column)
--   2) The planner is ready to immediately upload the new per-Division file
--   3) No SIM Generate / EOM Generate is in flight
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_VolumeGroup', 'U') IS NULL
BEGIN
    PRINT 'LPM_VolumeGroup table not found — run migration 006 first.';
    SET NOEXEC ON;
END
GO

-- ────────────────────────────────────────────────────────────────────────────
-- 1) Drop FK from LPM_SKUMaxRule to LPM_VolumeGroup(GroupCode)
--    The old FK referenced just GroupCode. The new composite PK would need
--    a (Country, DivCode, GroupCode) match, which LPM_SKUMaxRule already
--    has — but rebuilding the FK adds maintenance overhead. App-level
--    validation (Volume Groups upload + SKU Max Rules upload) already
--    enforces (Country, DivCode, GroupCode) existence. Drop the FK.
-- ────────────────────────────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.foreign_keys
            WHERE name = 'FK_LPM_SKUMaxRule_Group'
              AND parent_object_id = OBJECT_ID('dbo.LPM_SKUMaxRule'))
BEGIN
    ALTER TABLE dbo.LPM_SKUMaxRule DROP CONSTRAINT FK_LPM_SKUMaxRule_Group;
    PRINT 'Dropped FK_LPM_SKUMaxRule_Group.';
END
ELSE
    PRINT 'FK_LPM_SKUMaxRule_Group not present — skipping.';
GO

-- ────────────────────────────────────────────────────────────────────────────
-- 2) Wipe existing rows.
--    User explicitly chose "Wipe and reload" over cartesian backfill.
-- ────────────────────────────────────────────────────────────────────────────
DECLARE @before int = (SELECT COUNT(*) FROM dbo.LPM_VolumeGroup);
DELETE FROM dbo.LPM_VolumeGroup;
PRINT CONCAT('Wiped LPM_VolumeGroup — ', @before, ' rows removed. Re-upload via the Uploads page.');
GO

-- ────────────────────────────────────────────────────────────────────────────
-- 3) Drop old PK (Country, GroupCode)
-- ────────────────────────────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = 'PK_LPM_VolumeGroup'
              AND object_id = OBJECT_ID('dbo.LPM_VolumeGroup'))
BEGIN
    ALTER TABLE dbo.LPM_VolumeGroup DROP CONSTRAINT PK_LPM_VolumeGroup;
    PRINT 'Dropped old PK_LPM_VolumeGroup (Country, GroupCode).';
END
ELSE
    PRINT 'PK_LPM_VolumeGroup not present — skipping.';
GO

-- ────────────────────────────────────────────────────────────────────────────
-- 4) Add DivCode column
-- ────────────────────────────────────────────────────────────────────────────
IF COL_LENGTH('dbo.LPM_VolumeGroup', 'DivCode') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_VolumeGroup ADD DivCode int NOT NULL CONSTRAINT DF_LPM_VolumeGroup_DivCode DEFAULT (0);
    -- Drop the default — only used to satisfy NOT NULL on the (empty) table.
    -- All future inserts must explicitly supply DivCode.
    ALTER TABLE dbo.LPM_VolumeGroup DROP CONSTRAINT DF_LPM_VolumeGroup_DivCode;
    PRINT 'Added LPM_VolumeGroup.DivCode (int NOT NULL, no default — uploads must supply).';
END
ELSE
    PRINT 'LPM_VolumeGroup.DivCode already exists — skipping.';
GO

-- ────────────────────────────────────────────────────────────────────────────
-- 5) New composite PK (Country, DivCode, GroupCode)
-- ────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE name = 'PK_LPM_VolumeGroup'
                  AND object_id = OBJECT_ID('dbo.LPM_VolumeGroup'))
BEGIN
    ALTER TABLE dbo.LPM_VolumeGroup
        ADD CONSTRAINT PK_LPM_VolumeGroup
            PRIMARY KEY CLUSTERED (Country, DivCode, GroupCode);
    PRINT 'Created new PK_LPM_VolumeGroup (Country, DivCode, GroupCode).';
END
ELSE
    PRINT 'PK_LPM_VolumeGroup already exists — skipping.';
GO

-- ────────────────────────────────────────────────────────────────────────────
-- 6) New FK on DivCode → dbo.Division(DivCode)
-- ────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
                WHERE name = 'FK_LPM_VolumeGroup_Division'
                  AND parent_object_id = OBJECT_ID('dbo.LPM_VolumeGroup'))
BEGIN
    ALTER TABLE dbo.LPM_VolumeGroup
        ADD CONSTRAINT FK_LPM_VolumeGroup_Division
            FOREIGN KEY (DivCode) REFERENCES dbo.Division(DivCode);
    PRINT 'Added FK_LPM_VolumeGroup_Division (DivCode → dbo.Division).';
END
ELSE
    PRINT 'FK_LPM_VolumeGroup_Division already exists — skipping.';
GO

SET NOEXEC OFF;
GO

PRINT 'Migration 051 complete. Re-upload Volume Groups via the Uploads page (new Division column required).';
