-- ============================================================================
-- Migration 047 — extend LPMSIM_UnallocatedDiagnostic.TopReason taxonomy
--
-- 1.14.28 adds the EXCLUDED_BY_RULE TopReason code to the Allocation Gap
-- diagnostic so boxes where the unallocated qty is attributable to a
-- SKU Max = 0 exclusion (set by Rules 1-7: ExcludeExport_Planning,
-- ExcludeSubclass, RemoveItemsFromTransfer, ExcludeItemsMFCS,
-- DeptPriceMaxQty_MH4 with maxqty=0, LPM_StoreDivAccess deactivation,
-- LPM_StoreDeptAccess deactivation) can be distinguished from generic
-- CAP saturation (SKU Max ceiling or EOM Merch Need ceiling hit).
--
-- Migration 046 defined CK_LSUD_TopReason with five values:
--   ('FILTERED_SEASON','SKIP_NO_DIV','SKIP_NO_EOM','CAP','UNKNOWN')
-- This migration drops that constraint and re-adds it with the
-- additional 'EXCLUDED_BY_RULE' value.
--
-- Idempotent — the DROP is guarded by sys.check_constraints lookup.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPMSIM_UnallocatedDiagnostic', 'U') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.check_constraints
         WHERE name = 'CK_LSUD_TopReason'
           AND parent_object_id = OBJECT_ID('dbo.LPMSIM_UnallocatedDiagnostic')
    )
    BEGIN
        ALTER TABLE dbo.LPMSIM_UnallocatedDiagnostic
            DROP CONSTRAINT CK_LSUD_TopReason;
        PRINT 'Dropped old CK_LSUD_TopReason';
    END

    -- Re-add with the extended taxonomy. Idempotent guard against re-add.
    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
         WHERE name = 'CK_LSUD_TopReason'
           AND parent_object_id = OBJECT_ID('dbo.LPMSIM_UnallocatedDiagnostic')
    )
    BEGIN
        ALTER TABLE dbo.LPMSIM_UnallocatedDiagnostic
            ADD CONSTRAINT CK_LSUD_TopReason CHECK
                (TopReason IN
                    ('FILTERED_SEASON',
                     'SKIP_NO_DIV',
                     'SKIP_NO_EOM',
                     'EXCLUDED_BY_RULE',
                     'CAP',
                     'UNKNOWN'));
        PRINT 'Added CK_LSUD_TopReason with EXCLUDED_BY_RULE in the allow list';
    END
END
ELSE
    PRINT 'LPMSIM_UnallocatedDiagnostic table not found — skipping (apply migration 046 first)';
GO

PRINT 'Migration 047 complete.';
