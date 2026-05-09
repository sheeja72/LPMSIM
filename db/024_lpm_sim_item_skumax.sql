-- ============================================================================
-- Migration 024 — LPM_SimItemSkuMax
--
-- Per-(Country, Year, Month, Store, Item, Season) snapshot built fresh at
-- the start of every SIM Generate. Holds the warehouse stock for that
-- specific item under the SIM eligibility filter, the store's volume
-- group (read from LPM_EOM_Output), and the resulting SKU Max from the
-- LPM_SKUMaxRule range lookup.
--
-- SIM Generate's allocator now reads SKU Max from this table per
-- (Store, Item, Season-of-box) instead of the divisional roll-up that
-- was previously persisted on LPM_EOM_Output.
--
-- Rebuilt every SIM run for the current (Country, Year, Month). One
-- season per item per store is typical (most items are either summer
-- OR winter); items with both season tags get two rows.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_SimItemSkuMax', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_SimItemSkuMax (
        Country     varchar(20)  NOT NULL,
        Year1       int          NOT NULL,
        Month1      int          NOT NULL,
        StoreID     varchar(25)  NOT NULL,
        ItemCode    varchar(30)  NOT NULL,
        Season      char(1)      NOT NULL,                                -- 'S' or 'W'
        DivCode     int          NOT NULL,
        WHBoxQty    bigint       NOT NULL CONSTRAINT DF_LSISM_WH       DEFAULT (0),
        VolumeGroup varchar(20)  NULL,
        SKUMax      int          NOT NULL CONSTRAINT DF_LSISM_SKU      DEFAULT (0),
        CreateTS    datetime2(0) NOT NULL CONSTRAINT DF_LSISM_CTS      DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPM_SimItemSkuMax PRIMARY KEY CLUSTERED
            (Country, Year1, Month1, StoreID, ItemCode, Season),
        CONSTRAINT CK_LPM_SimItemSkuMax_Season CHECK (Season IN ('S','W'))
    );
    CREATE INDEX IX_LPM_SimItemSkuMax_Lookup
        ON dbo.LPM_SimItemSkuMax (Country, Year1, Month1, StoreID, Season)
        INCLUDE (ItemCode, SKUMax, WHBoxQty, DivCode, VolumeGroup);
    CREATE INDEX IX_LPM_SimItemSkuMax_Item
        ON dbo.LPM_SimItemSkuMax (Country, Year1, Month1, ItemCode)
        INCLUDE (StoreID, Season, SKUMax);
    CREATE INDEX IX_LPM_SimItemSkuMax_Div
        ON dbo.LPM_SimItemSkuMax (Country, Year1, Month1, DivCode)
        INCLUDE (StoreID, ItemCode, Season, SKUMax, WHBoxQty);
    PRINT 'Created dbo.LPM_SimItemSkuMax';
END
ELSE
    PRINT 'LPM_SimItemSkuMax already exists';
GO

PRINT 'Migration 024 complete.';
