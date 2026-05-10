-- ============================================================================
-- Migration 040 — LPM_StoreDeptAccess
--
-- Per-(Country, Store, Division, Department) activation + future EOM scaling
-- pct. When IsActive = 0 the SKU Max build's Rule 7 zeroes SKUMax for every
-- item under that (Store × Div × Dept) — a finer-grained version of
-- LPM_StoreDivAccess (which works at Division level).
--
-- Department matches Datareporting.dbo.subclassmaster.Department (the name,
-- not an MH-ID code).
--
-- DeptPct is RESERVED for a future EOM / SIM Generate rule the planner is
-- still designing — for now the SKU Max build ignores it. The column lives
-- on this table from day one so admins can populate it via the same page
-- that manages activation. Recommended default: 100 (no scaling).
--
-- Default behaviour (no row in this table) = ACTIVE for every (Store, Div,
-- Dept). Only explicit (Store, Div, Dept) deactivations need rows here.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_StoreDeptAccess', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_StoreDeptAccess (
        Id          int           IDENTITY(1,1) NOT NULL,
        Country     varchar(20)   NOT NULL,
        StoreID     varchar(25)   NOT NULL,
        DivCode     int           NOT NULL,
        Department  nvarchar(60)  NOT NULL,
        DeptPct     decimal(7,4)  NOT NULL CONSTRAINT DF_LSDA_DeptPct  DEFAULT (100),
        IsActive    bit           NOT NULL CONSTRAINT DF_LSDA_IsActive DEFAULT (1),
        Remarks     nvarchar(500) NULL,
        CreateTS    datetime2(0)  NOT NULL CONSTRAINT DF_LSDA_CreateTS DEFAULT SYSDATETIME(),
        CreatedBy   varchar(100)  NULL,
        UpdatedTS   datetime2(0)  NULL,
        UpdatedBy   varchar(100)  NULL,

        CONSTRAINT PK_LPM_StoreDeptAccess PRIMARY KEY (Id),
        CONSTRAINT UQ_LPM_StoreDeptAccess UNIQUE (Country, StoreID, DivCode, Department),
        CONSTRAINT CK_LSDA_DeptPct CHECK (DeptPct >= 0 AND DeptPct <= 100)
    );
    PRINT 'Created dbo.LPM_StoreDeptAccess';
END
ELSE
    PRINT 'LPM_StoreDeptAccess already exists';
GO

-- Lookup index for the SKU Max build Rule 7 — it joins on
-- (Country, StoreID, DivCode, Department). The unique constraint above
-- already covers this prefix in left-to-right order, so no extra index
-- needed.

PRINT 'Migration 040 complete.';
