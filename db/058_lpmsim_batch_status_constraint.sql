-- ============================================================================
-- Migration 058 — LPMSIM_Batch.Status CHECK constraint allows 'Running'
--
-- 1.14.79 hotfix — A `CK_LPMSIM_Batch_Status` CHECK constraint was added to
-- dbo.LPMSIM_Batch outside the application's migration history (likely by a
-- DBA reviewing the schema) that only allowed the legacy values
-- ('Draft', 'Approved'). 1.14.67 introduced a new 'Running' intermediate
-- status (insert with 'Running' → flip to 'Draft' after the persist phase
-- commits) — once the constraint went in, every Generate started failing
-- with:
--
--   The INSERT statement conflicted with the CHECK constraint
--   'CK_LPMSIM_Batch_Status'. The conflict occurred in database 'LPMSIM',
--   table 'dbo.LPMSIM_Batch', column 'Status'.
--
-- This migration drops the old constraint (if present) and recreates it to
-- accept the full status lifecycle: Draft / Approved / Running. Idempotent.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
     WHERE name = 'CK_LPMSIM_Batch_Status'
       AND parent_object_id = OBJECT_ID('dbo.LPMSIM_Batch')
)
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch DROP CONSTRAINT CK_LPMSIM_Batch_Status;
    PRINT 'Dropped existing CK_LPMSIM_Batch_Status constraint.';
END
ELSE
    PRINT 'No existing CK_LPMSIM_Batch_Status to drop.';
GO

ALTER TABLE dbo.LPMSIM_Batch
  ADD CONSTRAINT CK_LPMSIM_Batch_Status
  CHECK (Status IN ('Draft', 'Approved', 'Running'));
PRINT 'Created CK_LPMSIM_Batch_Status with (Draft, Approved, Running).';
GO

PRINT 'Migration 058 complete.';
