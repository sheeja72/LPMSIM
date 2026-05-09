-- ============================================================================
-- Migration 036 — Drop UQ_LPMSIM_Batch_CountryRunDate
--
-- Migration 009 added a UNIQUE constraint on (Country, RunDate) when the
-- design was strictly "one batch per period" (Draft replaced any existing
-- batch). Generate now KEEPS Approved batches and creates a new Draft
-- alongside (see LpmSimGenerator.GenerateAsync), so multiple rows with
-- the same (Country, RunDate) are valid — and required.
--
-- The unique constraint blocks the new "keep Approved + new Draft" flow
-- with: "Violation of UNIQUE KEY constraint 'UQ_LPMSIM_Batch_CountryRunDate'.
-- Cannot insert duplicate key in object 'dbo.LPMSIM_Batch'."
--
-- Drop the constraint. A regular non-unique index on (Country, RunDate)
-- is still useful for the per-period lookups in CheckAsync /
-- GetBatchesForPeriodAsync, so we add one back to keep query plans fast.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF EXISTS (
    SELECT 1
      FROM sys.objects
     WHERE name = 'UQ_LPMSIM_Batch_CountryRunDate'
       AND parent_object_id = OBJECT_ID('dbo.LPMSIM_Batch')
)
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch
        DROP CONSTRAINT UQ_LPMSIM_Batch_CountryRunDate;
    PRINT 'LPMSIM_Batch: dropped UQ_LPMSIM_Batch_CountryRunDate';
END
ELSE
    PRINT 'UQ_LPMSIM_Batch_CountryRunDate already absent';
GO

-- Replacement non-unique index — keeps CheckAsync's "latest batch for period"
-- query (ORDER BY LPMBatchNo DESC) and GetBatchesForPeriodAsync fast.
IF NOT EXISTS (
    SELECT 1
      FROM sys.indexes
     WHERE name = 'IX_LPMSIM_Batch_CountryRunDate'
       AND object_id = OBJECT_ID('dbo.LPMSIM_Batch')
)
BEGIN
    CREATE INDEX IX_LPMSIM_Batch_CountryRunDate
        ON dbo.LPMSIM_Batch (Country, RunDate, LPMBatchNo DESC);
    PRINT 'LPMSIM_Batch: created IX_LPMSIM_Batch_CountryRunDate (non-unique)';
END
ELSE
    PRINT 'IX_LPMSIM_Batch_CountryRunDate already exists';
GO

PRINT 'Migration 036 complete.';
