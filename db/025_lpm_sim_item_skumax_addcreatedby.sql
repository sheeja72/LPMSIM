-- ============================================================================
-- Migration 025 — Add CreatedBy column to LPM_SimItemSkuMax
--
-- The SKU Max build is now a separate user-driven step (no longer triggered
-- automatically on every SIM Generate). Capturing the username on each row
-- lets the UI show "last built by X at Y" without joining a separate audit
-- table.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPM_SimItemSkuMax', 'CreatedBy') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_SimItemSkuMax
        ADD CreatedBy nvarchar(128) NULL;
    PRINT 'Added CreatedBy to dbo.LPM_SimItemSkuMax';
END
ELSE
    PRINT 'CreatedBy already present on dbo.LPM_SimItemSkuMax';
GO

PRINT 'Migration 025 complete.';
