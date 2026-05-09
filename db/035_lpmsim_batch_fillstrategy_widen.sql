-- ============================================================================
-- Migration 035 — Widen LPMSIM_Batch.FillStrategy from varchar(20) to varchar(40)
--
-- Migration 019 originally sized this column for the strategy name only
-- ("EqualFillRate", 13 chars). The new "SKU Max only" run mode appends a
-- "(SKU Max only)" suffix to the snapshot, taking the value to ~28 chars
-- and tripping a "String or binary data would be truncated" error on
-- SIM Generate.
--
-- Widening to varchar(40) gives headroom for any future strategy + tag
-- combination without forcing another column-width migration. Existing
-- short values are unaffected.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF EXISTS (
    SELECT 1
      FROM sys.columns c
      INNER JOIN sys.tables  t ON t.object_id = c.object_id
     WHERE t.name = 'LPMSIM_Batch'
       AND c.name = 'FillStrategy'
       AND c.max_length < 40
)
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch
        ALTER COLUMN FillStrategy varchar(40) NULL;
    PRINT 'LPMSIM_Batch.FillStrategy widened to varchar(40)';
END
ELSE
    PRINT 'LPMSIM_Batch.FillStrategy already varchar(40) or wider';
GO

-- Same column on the backup table — keep it aligned so backup INSERTs in
-- DeleteAsync don't fail when a "(SKU Max only)" batch is being archived.
IF EXISTS (
    SELECT 1
      FROM sys.columns c
      INNER JOIN sys.tables  t ON t.object_id = c.object_id
     WHERE t.name = 'LPMSIM_Batch_Backup'
       AND c.name = 'FillStrategy'
       AND c.max_length < 40
)
BEGIN
    ALTER TABLE dbo.LPMSIM_Batch_Backup
        ALTER COLUMN FillStrategy varchar(40) NULL;
    PRINT 'LPMSIM_Batch_Backup.FillStrategy widened to varchar(40)';
END
ELSE
    PRINT 'LPMSIM_Batch_Backup.FillStrategy already varchar(40) or wider (or table missing)';
GO

PRINT 'Migration 035 complete.';
