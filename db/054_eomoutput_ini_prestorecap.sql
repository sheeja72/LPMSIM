-- ============================================================================
-- Migration 054 — LPM_EOM_Output: add IniEom + PreStoreCapEom columns
--
-- 1.14.53 — Two new EOM columns that decompose the Tgt EOM calculation:
--
--   Ini.EOM         = TargetSales × TargetTurn        (per Store × Division)
--   PreStoreCapEom  = (Ini.EOM[store, div] / Σ Ini.EOM in Div) × PlannedEOM[div]
--   TargetEOM       = if LPM_StoreCapacity.EomCapacity exists for the store
--                       AND Σ PreStoreCapEom across divisions > EomCapacity
--                     then PreStoreCapEom[div] × (EomCapacity / Σ PreStoreCapEom)
--                     else PreStoreCapEom[div]   ← passthrough
--
-- TargetEOM's formula CHANGES with this release (was WtAvgSold-share × PlannedEOM).
-- The column itself is reused — values shift on the next EOM Generate run.
--
-- Both new columns are NULLable. Existing rows in LPM_EOM_Output stay valid
-- (the new cols read as NULL); only freshly-generated batches populate them.
--
-- Idempotent: guarded by COL_LENGTH IS NULL.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'IniEom') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD IniEom decimal(18,2) NULL;
    PRINT 'Added dbo.LPM_EOM_Output.IniEom';
END
ELSE
    PRINT 'LPM_EOM_Output.IniEom already exists — skipping.';
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'PreStoreCapEom') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD PreStoreCapEom decimal(18,2) NULL;
    PRINT 'Added dbo.LPM_EOM_Output.PreStoreCapEom';
END
ELSE
    PRINT 'LPM_EOM_Output.PreStoreCapEom already exists — skipping.';
GO

PRINT 'Migration 054 complete.';
