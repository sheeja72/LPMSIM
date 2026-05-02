-- ============================================================================
-- Migration 022 — LPM_StoreDivAccess
--
-- Per-Country, per-Store, per-Division activation flag. A row with
-- IsActive = 0 means the SIM allocator must NOT push that division's items
-- to that store — enforced by EOM Generate setting SKUMax = 0 for the
-- (Store × Division) row, which makes the SKU Max balance zero, which
-- makes the normal-fill and round-robin loops skip that store for that
-- division on every cycle.
--
-- Default behaviour (no row in this table) = ACTIVE. The table only needs
-- entries for explicit overrides.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_StoreDivAccess', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_StoreDivAccess (
        Country   varchar(20)  NOT NULL,
        StoreID   varchar(25)  NOT NULL,
        DivCode   int          NOT NULL,
        IsActive  bit          NOT NULL CONSTRAINT DF_LSDA_IsActive  DEFAULT (1),
        CreateTS  datetime2(0) NOT NULL CONSTRAINT DF_LSDA_CreateTS  DEFAULT SYSDATETIME(),
        CreatedBy varchar(100) NULL,
        UpdatedTS datetime2(0) NULL,
        UpdatedBy varchar(100) NULL,
        CONSTRAINT PK_LPM_StoreDivAccess PRIMARY KEY (Country, StoreID, DivCode),
        CONSTRAINT FK_LPM_StoreDivAccess_Division FOREIGN KEY (DivCode)
            REFERENCES dbo.Division(DivCode)
    );
    CREATE INDEX IX_LSDA_Inactive ON dbo.LPM_StoreDivAccess(Country, IsActive)
        INCLUDE (StoreID, DivCode);
    PRINT 'Created dbo.LPM_StoreDivAccess';
END
ELSE
    PRINT 'LPM_StoreDivAccess already exists';
GO

PRINT 'Migration 022 complete.';
