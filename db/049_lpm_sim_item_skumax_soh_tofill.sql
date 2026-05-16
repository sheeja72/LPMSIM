-- ============================================================================
-- Migration 049 — add SOH + ToFillQty columns to LPM_SimItemSkuMax
--                 (and its backup mirror)
--
-- 1.14.35 enriches the per-(Country, Year, Month, Store, Item, Season) SKU
-- Max snapshot with two more columns so the planner can read the fill gap
-- directly out of the table without joining LocStock every time:
--
--   • SOH        — per-(Store, Item) stock from racks.dbo.LPM_LocStock,
--                  summed across LocStock rows for the same (Store, Item)
--                  pair (one item can show up in multiple LocStock rows for
--                  different sub-locations within the same store). Sourced
--                  with the same DataSettings.SIMCountry guard the allocator
--                  uses, so empty / mis-tagged LocStock.Country values don't
--                  drop stores.
--
--   • ToFillQty  — computed PERSISTED column. The standard "fill gap to
--                  max" calculation, FLOORED at 0 (overstocked stores
--                  surface as 0, not a negative). Negative SOH (oversold /
--                  data anomaly) is clamped at 0 before subtraction so a
--                  store with SKUMax=10 and SOH=-5 reads as ToFill=10, not
--                  15. Mirrors the negative-SOH clamp the allocator already
--                  applies in cap math (see LpmSimGenerator 1.14.31).
--
--                  Formula:  MAX(SKUMax − MAX(SOH, 0), 0)
--
--                  Persisted so it lands on disk (indexable, no recompute
--                  on read). SQL Server recalculates on every UPDATE of
--                  SOH or SKUMax, so the column can never drift from its
--                  inputs even if a downstream tool writes SKUMax directly.
--
-- Both columns are added to the backup table too (LPM_SimItemSkuMax_Backup
-- from migration 045) so archived periods carry the same shape.
--
-- Idempotent: each ALTER guarded by COL_LENGTH IS NULL.
-- ============================================================================
SET XACT_ABORT ON;
GO

-- Why EXEC() wraps the ToFillQty ALTERs: SQL Server parses every column
-- reference in a batch BEFORE executing any statement in it. If we wrote
-- the ALTER TABLE ADD ToFillQty AS (... SOH ...) inline, the parser would
-- fail with "Invalid column name 'SOH'" because the preceding ALTER TABLE
-- ADD SOH hasn't executed yet at parse time. Wrapping the second ALTER
-- in EXEC() defers parsing of the inner string until execution time, by
-- which point SOH exists. (Same pattern as DDL that adds + references a
-- column in the same idempotent block.)

-- ────────────────────────────────────────────────────────────────────────────
-- 1) Main table — dbo.LPM_SimItemSkuMax
-- ────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.LPM_SimItemSkuMax', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.LPM_SimItemSkuMax', 'SOH') IS NULL
    BEGIN
        ALTER TABLE dbo.LPM_SimItemSkuMax
            ADD SOH int NOT NULL CONSTRAINT DF_LSISM_SOH DEFAULT (0);
        PRINT 'Added LPM_SimItemSkuMax.SOH';
    END
    ELSE
        PRINT 'LPM_SimItemSkuMax.SOH already exists — skipping.';

    IF COL_LENGTH('dbo.LPM_SimItemSkuMax', 'ToFillQty') IS NULL
    BEGIN
        -- Computed PERSISTED. Inner CASE clamps negative SOH at 0; outer
        -- CASE floors the result at 0 so overstocked stores read as 0.
        -- Wrapped in EXEC() so the SOH reference is parsed at execute
        -- time (after the ALTER ADD SOH above has run), not at batch
        -- parse time.
        EXEC('
            ALTER TABLE dbo.LPM_SimItemSkuMax
                ADD ToFillQty AS (
                    CASE
                        WHEN SKUMax - CASE WHEN SOH > 0 THEN SOH ELSE 0 END > 0
                        THEN SKUMax - CASE WHEN SOH > 0 THEN SOH ELSE 0 END
                        ELSE 0
                    END
                ) PERSISTED;
        ');
        PRINT 'Added LPM_SimItemSkuMax.ToFillQty (computed PERSISTED)';
    END
    ELSE
        PRINT 'LPM_SimItemSkuMax.ToFillQty already exists — skipping.';
END
ELSE
    PRINT 'LPM_SimItemSkuMax table not found — run migration 024 first.';
GO

-- ────────────────────────────────────────────────────────────────────────────
-- 2) Backup table — dbo.LPM_SimItemSkuMax_Backup
-- ────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.LPM_SimItemSkuMax_Backup', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.LPM_SimItemSkuMax_Backup', 'SOH') IS NULL
    BEGIN
        ALTER TABLE dbo.LPM_SimItemSkuMax_Backup
            ADD SOH int NOT NULL CONSTRAINT DF_LSISMBKP_SOH DEFAULT (0);
        PRINT 'Added LPM_SimItemSkuMax_Backup.SOH';
    END
    ELSE
        PRINT 'LPM_SimItemSkuMax_Backup.SOH already exists — skipping.';

    IF COL_LENGTH('dbo.LPM_SimItemSkuMax_Backup', 'ToFillQty') IS NULL
    BEGIN
        EXEC('
            ALTER TABLE dbo.LPM_SimItemSkuMax_Backup
                ADD ToFillQty AS (
                    CASE
                        WHEN SKUMax - CASE WHEN SOH > 0 THEN SOH ELSE 0 END > 0
                        THEN SKUMax - CASE WHEN SOH > 0 THEN SOH ELSE 0 END
                        ELSE 0
                    END
                ) PERSISTED;
        ');
        PRINT 'Added LPM_SimItemSkuMax_Backup.ToFillQty (computed PERSISTED)';
    END
    ELSE
        PRINT 'LPM_SimItemSkuMax_Backup.ToFillQty already exists — skipping.';
END
ELSE
    PRINT 'LPM_SimItemSkuMax_Backup table not found — run migration 045 first.';
GO

PRINT 'Migration 049 complete.';
