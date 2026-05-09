-- ============================================================================
-- Migration 030 — Add LPMBoxQty to LPM_EOM_Output
--
-- Per-Division total qty of eligible LPM boxes from racks.dbo.whboxitems:
--    pt.PalletCategory = 'ELIGIBLE'
--    w.LPMDt IS NOT NULL
--    (NO ShopEligible filter — different from EOM's WHStock columns)
--
-- Surfaced on the EOM Division Summary tab so planners can see how much
-- LPM-tagged stock the warehouse holds for each division this period.
--
-- The value is the same for every (Store × Division) row in the period —
-- repeated per row to keep the schema flat (same shape as WHStock /
-- WHStockSummer / WHStockWinter on this table).
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'LPMBoxQty') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD LPMBoxQty int NULL;
    PRINT 'LPM_EOM_Output: added LPMBoxQty';
END
ELSE
    PRINT 'LPM_EOM_Output.LPMBoxQty already exists';
GO

PRINT 'Migration 030 complete.';
