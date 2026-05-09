-- ============================================================================
-- Migration 034 — LPM_SimItemSkuMaxExcluded
--
-- Audit table for the SKU Max exclusion rules applied during BuildSkuMax.
-- For every (Store, Item) row that gets SKUMax forced to 0 by an exclusion
-- rule, we INSERT one row here capturing the original SKUMax + the source
-- table that triggered the zero.
--
-- Rules applied (in BuildItemSkuMaxAsync, inside the same transaction as
-- the main DELETE+INSERT, so the whole build is atomic):
--
--   1. usa.dbo.ExcludeExport_Planning
--      → matches by (Shopname, ItemCode); zero everywhere this row appears.
--
--   2. usa.dbo.ExcludeSubclass with Inactive='N'
--      → matches by (Shopname, MH4ID); MH4ID resolved via
--        Datareporting.dbo.upc_subclass.MH4ID for each item.
--
--   3. bfldata.dbo.RemoveItemsFromTransfer with TrnDate >= '2025-09-01'
--      → matches by (ShopName, ItemCode).
--
--   4. usa.dbo.ExcludeItemsMFCS
--      → matches by (Shopname, HSCode); HSCode resolved via
--        usa.dbo.upcbarcodes.HSCode for each itemcode.
--
-- One audit row per (Item, Rule) match — multiple rules matching the same
-- item produce multiple rows so the planner can see every reason.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_SimItemSkuMaxExcluded', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_SimItemSkuMaxExcluded (
        Id           bigint IDENTITY(1, 1) NOT NULL,
        Country      varchar(20)  NOT NULL,
        Year1        int          NOT NULL,
        Month1       int          NOT NULL,
        StoreID      varchar(25)  NOT NULL,
        ItemCode     varchar(30)  NOT NULL,
        Season       char(1)      NULL,
        DivCode      int          NULL,
        PriorSKUMax  int          NOT NULL,                                  -- original SKUMax before zeroing
        SourceTable  varchar(120) NOT NULL,                                  -- e.g., 'usa.dbo.ExcludeExport_Planning'
        Reason       varchar(160) NOT NULL,                                  -- human-readable explanation
        MatchedKey   varchar(120) NULL,                                      -- e.g., 'MH4ID=1234' or 'HSCode=850110'
        CreateTS     datetime2(0) NOT NULL CONSTRAINT DF_ExclSkuMax_CTS DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPM_SimItemSkuMaxExcluded PRIMARY KEY CLUSTERED (Id)
    );
    CREATE INDEX IX_LPM_SimItemSkuMaxExcluded_Period
        ON dbo.LPM_SimItemSkuMaxExcluded (Country, Year1, Month1)
        INCLUDE (SourceTable);
    CREATE INDEX IX_LPM_SimItemSkuMaxExcluded_Source
        ON dbo.LPM_SimItemSkuMaxExcluded (Country, Year1, Month1, SourceTable);
    CREATE INDEX IX_LPM_SimItemSkuMaxExcluded_Item
        ON dbo.LPM_SimItemSkuMaxExcluded (Country, Year1, Month1, StoreID, ItemCode);
    PRINT 'Created dbo.LPM_SimItemSkuMaxExcluded';
END
ELSE
    PRINT 'LPM_SimItemSkuMaxExcluded already exists';
GO

PRINT 'Migration 034 complete.';
