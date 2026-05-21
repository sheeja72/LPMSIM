-- ============================================================================
-- Migration 059 — LPMSIM_Batch.PalletCategories + LpmMonths
--
-- 1.14.98 — Persist the planner's PalletCategory and LPM Months filter
-- selections on the batch row so the Gap by UPC report can replay exactly
-- what the allocator saw at Generate time.
--
-- Background: pre-1.14.98 the batch row only stored Sources / Seasons /
-- Warehouses / OverrideUsabilityPct / WeekNo / FillStrategy. PalletCategories
-- and LpmMonths were applied by the allocator from the page request but
-- never written to LPMSIM_Batch. The 1.14.96 Gap by UPC report needs to
-- replay those filters against whboxitems to compute EligibleWhQty per
-- item — without persisting them, it over-counts the eligible universe
-- (typically by 50% or more, because the page default 'ELIGIBLE'-only
-- pallet category gets ignored on replay).
--
-- Both columns are NULLable:
--   • NULL on pre-1.14.98 batches — the Gap by UPC report will use NO
--     filter for those rows and surface a "may over-count" caveat in the
--     description.
--   • Populated on 1.14.98+ batches with the comma-separated snapshot.
--
-- Idempotent: each ALTER guarded by COL_LENGTH IS NULL.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPMSIM_Batch', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.LPMSIM_Batch', 'PalletCategories') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Batch
            ADD PalletCategories varchar(500) NULL;
        PRINT 'Added LPMSIM_Batch.PalletCategories (varchar(500) NULL).';
    END
    ELSE
        PRINT 'LPMSIM_Batch.PalletCategories already exists.';

    IF COL_LENGTH('dbo.LPMSIM_Batch', 'LpmMonths') IS NULL
    BEGIN
        ALTER TABLE dbo.LPMSIM_Batch
            ADD LpmMonths varchar(200) NULL;
        PRINT 'Added LPMSIM_Batch.LpmMonths (varchar(200) NULL).';
    END
    ELSE
        PRINT 'LPMSIM_Batch.LpmMonths already exists.';
END
ELSE
    PRINT 'LPMSIM_Batch table not found — skipping.';
GO

PRINT 'Migration 059 complete.';
