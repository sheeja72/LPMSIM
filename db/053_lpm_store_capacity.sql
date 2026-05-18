-- ============================================================================
-- Migration 053 — LPM_StoreCapacity table
--
-- 1.14.51 — Per-store EOM Capacity ceiling. One row per (Country, StoreID).
-- Shown on Planning Config → Stores Capacity EOM and bulk-uploadable via
-- Data Uploads → Stores Capacity.
--
-- Independent of LPM_LocStock and LPM_StoreGrade — captures the upper-bound
-- store capacity the planner can plug into downstream sizing decisions.
--
-- Idempotent: guarded by OBJECT_ID IS NULL.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_StoreCapacity', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_StoreCapacity (
        Country     varchar(20)  NOT NULL,
        StoreID     varchar(25)  NOT NULL,
        EomCapacity int          NOT NULL CONSTRAINT DF_LPM_StoreCapacity_EomCapacity DEFAULT (0),
        IsActive    bit          NOT NULL CONSTRAINT DF_LPM_StoreCapacity_IsActive    DEFAULT (1),
        CreateTS    datetime2(0) NOT NULL CONSTRAINT DF_LPM_StoreCapacity_CreateTS    DEFAULT SYSDATETIME(),
        CreatedBy   varchar(100) NULL,
        UpdatedTS   datetime2(0) NULL,
        UpdatedBy   varchar(100) NULL,
        CONSTRAINT PK_LPM_StoreCapacity PRIMARY KEY CLUSTERED (Country, StoreID)
    );
    PRINT 'Created dbo.LPM_StoreCapacity';
END
ELSE
    PRINT 'LPM_StoreCapacity already exists — skipping.';
GO

PRINT 'Migration 053 complete.';
