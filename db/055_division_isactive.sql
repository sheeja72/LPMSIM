-- ============================================================================
-- Migration 055 — dbo.Division: IsActive flag (retire Div 420 globally)
--
-- 1.14.55 — Adds an IsActive bit to dbo.Division so a division can be
-- globally retired without breaking historical data. EOM Generate / SIM
-- Generate / ADM / Reports / Uploads all filter by IsActive going forward,
-- so an inactive division:
--   • Disappears from every admin dropdown.
--   • Stops contributing to readiness checks (divCount, plannedOk, whOk).
--   • Stops being iterated by the (Store × Division) grid in EOM Generate.
--   • Is rejected by config uploads (Planned, SkuMax Rules, Volume Groups,
--     WH Stock) — uploading a row for an inactive division now fails
--     validation.
--
-- Historical rows in LPM_SalesTurns / LPM_EOM_Output / LPM_SimItemSkuMax for
-- inactive divisions are preserved as-is — the filter only affects the
-- forward planning surface.
--
-- This release also marks DivCode = 420 inactive. To revive it later, run:
--     UPDATE dbo.Division SET IsActive = 1 WHERE DivCode = 420;
--
-- Idempotent: guarded by COL_LENGTH IS NULL and a defensive WHERE on the UPDATE.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.Division', 'IsActive') IS NULL
BEGIN
    ALTER TABLE dbo.Division
        ADD IsActive bit NOT NULL
            CONSTRAINT DF_Division_IsActive DEFAULT (1);
    PRINT 'Added dbo.Division.IsActive (default 1 — existing rows stay active).';
END
ELSE
    PRINT 'dbo.Division.IsActive already exists — skipping ALTER.';
GO

UPDATE dbo.Division
   SET IsActive = 0
 WHERE DivCode  = 420
   AND IsActive <> 0;

IF @@ROWCOUNT > 0
    PRINT 'Set DivCode 420 IsActive = 0.';
ELSE
    PRINT 'DivCode 420 already inactive (or no row exists) — skipping UPDATE.';
GO

PRINT 'Migration 055 complete.';
