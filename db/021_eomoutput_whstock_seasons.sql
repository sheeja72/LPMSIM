-- ============================================================================
-- Migration 021 — Add WHStockSummer / WHStockWinter to LPM_EOM_Output
--
-- WH Stock per Division is now computed live from racks.dbo.whboxitems
-- (joined via Datareporting.dbo.upc_subclass × subclassmaster × Division)
-- using the same eligibility filters SIM Generate uses:
--    pt.PalletCategory = 'ELIGIBLE'
--    ShopEligible <> 'E'
--    LPMDt set & current/elapsed   OR   LPMDt IS NULL
-- and split by pallettype.Season ('W' = Winter, otherwise Summer).
--
-- The legacy single WHStock column is kept (= Summer + Winter) so the
-- existing SKU Max range lookup keeps working without changes.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'WHStockSummer') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output
        ADD WHStockSummer int NULL;
    PRINT 'LPM_EOM_Output: added WHStockSummer';
END
ELSE
    PRINT 'LPM_EOM_Output.WHStockSummer already exists';
GO

IF COL_LENGTH('dbo.LPM_EOM_Output', 'WHStockWinter') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output
        ADD WHStockWinter int NULL;
    PRINT 'LPM_EOM_Output: added WHStockWinter';
END
ELSE
    PRINT 'LPM_EOM_Output.WHStockWinter already exists';
GO

PRINT 'Migration 021 complete.';
