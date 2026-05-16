-- ============================================================================
-- Migration 048 — add DivisionName / Brand / GroupCode columns to
-- dbo.LPM_SimItemSkuMaxExcluded
--
-- 1.14.32 enriches the SKU Max exclusion audit table so the planner can
-- see, alongside each excluded (Store, Item) row, which division/vendor/
-- merch-group the item belongs to without joining out to Division /
-- upcbarcodes / ItemMaster every time they open the report.
--
--   • DivisionName  — resolved from dbo.Division.Name on DivCode
--   • Brand         — resolved from usa.dbo.upcbarcodes.Vendor on itemcode
--                     (TOP 1 per item — upcbarcodes can have multiple rows
--                     per item; we pick one for the display label)
--   • GroupCode     — resolved from Hodata.dbo.ItemMaster.GroupCode on
--                     itemcode
--
-- All three columns are NULL-able and additive. Existing rows stay NULL
-- until the next BuildSkuMax run (which re-populates the audit table from
-- scratch — see the DELETE FROM ... WHERE Country = @c AND Year1 = @y AND
-- Month1 = @m + INSERT pattern inside LpmSimGenerator's apply phase).
--
-- Idempotent: each ALTER guarded by COL_LENGTH IS NULL.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_SimItemSkuMaxExcluded', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.LPM_SimItemSkuMaxExcluded', 'DivisionName') IS NULL
    BEGIN
        ALTER TABLE dbo.LPM_SimItemSkuMaxExcluded ADD DivisionName varchar(80) NULL;
        PRINT 'Added LPM_SimItemSkuMaxExcluded.DivisionName';
    END
    IF COL_LENGTH('dbo.LPM_SimItemSkuMaxExcluded', 'Brand') IS NULL
    BEGIN
        ALTER TABLE dbo.LPM_SimItemSkuMaxExcluded ADD Brand varchar(80) NULL;
        PRINT 'Added LPM_SimItemSkuMaxExcluded.Brand';
    END
    IF COL_LENGTH('dbo.LPM_SimItemSkuMaxExcluded', 'GroupCode') IS NULL
    BEGIN
        ALTER TABLE dbo.LPM_SimItemSkuMaxExcluded ADD GroupCode varchar(50) NULL;
        PRINT 'Added LPM_SimItemSkuMaxExcluded.GroupCode';
    END
END
ELSE
    PRINT 'LPM_SimItemSkuMaxExcluded table not found — skipping (run migration 034 first).';
GO

PRINT 'Migration 048 complete.';
