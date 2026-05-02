-- ============================================================================
-- Migration 020 — Route LPMSIM.dbo.DataSettings to bfldata.dbo.DataSettings
--
-- The application reads DataSettings via the local name `dbo.DataSettings`
-- (both EF and raw SQL throughout the codebase). Instead of changing every
-- query, we replace the local table with a SYNONYM pointing at the master
-- copy in BFLDATA. After this migration:
--
--    LPMSIM.dbo.DataSettings  ──[synonym]──▶  bfldata.dbo.DataSettings
--
-- All existing reads work transparently. SSMS will show the synonym in the
-- LPMSIM database; underlying data lives in BFLDATA, fully maintained by
-- whatever ETL populates that master copy.
--
-- IMPORTANT — RUN-ONCE: this drops the LOCAL DataSettings table (or existing
-- synonym) and recreates the synonym. Make sure bfldata.dbo.DataSettings has
-- the columns LPM SIM uses: StoreID, PBFullname, Country, ActiveStore.
-- ============================================================================
SET XACT_ABORT ON;
GO

-- 1. Sanity check: bfldata.dbo.DataSettings must exist before we can point at it.
IF OBJECT_ID('bfldata.dbo.DataSettings', 'U') IS NULL
BEGIN
    RAISERROR('bfldata.dbo.DataSettings is missing — create / populate it before running this migration.', 16, 1);
    RETURN;
END
GO

-- 2. If a real local table exists, drop it. (Data is presumed to live now in BFLDATA.)
IF OBJECT_ID('dbo.DataSettings', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.DataSettings;
    PRINT 'Dropped local table LPMSIM.dbo.DataSettings (data sourced from bfldata going forward).';
END
GO

-- 3. If an old synonym exists (re-run scenario), drop and recreate.
IF OBJECT_ID('dbo.DataSettings', 'SN') IS NOT NULL
BEGIN
    DROP SYNONYM dbo.DataSettings;
    PRINT 'Dropped existing synonym dbo.DataSettings — will recreate.';
END
GO

-- 4. Create the synonym.
CREATE SYNONYM dbo.DataSettings FOR bfldata.dbo.DataSettings;
PRINT 'Created synonym LPMSIM.dbo.DataSettings -> bfldata.dbo.DataSettings';
GO

-- 5. Smoke test — ensure the synonym responds.
DECLARE @cnt int;
SELECT @cnt = COUNT(*) FROM dbo.DataSettings;
PRINT CONCAT('dbo.DataSettings now reads from bfldata.dbo.DataSettings — ', @cnt, ' rows visible.');
GO

PRINT 'Migration 020 complete.';
