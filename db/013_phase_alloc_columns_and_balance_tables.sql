-- ============================================================================
-- Migration 013 — Two-phase allocation + round-robin support
--
-- 1) LPMSIM_Output           : add Phase + IsRoundRobin
--    LPMSIM_Output_Backup    : same (so backups round-trip)
-- 2) LPMSIM_AllocTrace       : add Phase column; widen Decision check to
--                              include 'ALLOC_RR'
-- 3) LPMSIM_StoreItemBalance : NEW — running per-(Store,Item) totals snapshot
-- 4) LPMSIM_StoreDivBalance  : NEW — running per-(Store,Div)  totals snapshot
-- ============================================================================
SET XACT_ABORT ON;
GO

-- 1. LPMSIM_Output ---------------------------------------------------------
IF COL_LENGTH('dbo.LPMSIM_Output','Phase') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output
        ADD Phase varchar(8) NOT NULL CONSTRAINT DF_LPMSIM_Output_Phase DEFAULT ('P1');
    PRINT 'LPMSIM_Output: added Phase';
END
GO
IF COL_LENGTH('dbo.LPMSIM_Output','IsRoundRobin') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output
        ADD IsRoundRobin bit NOT NULL CONSTRAINT DF_LPMSIM_Output_IsRR DEFAULT (0);
    PRINT 'LPMSIM_Output: added IsRoundRobin';
END
GO

IF COL_LENGTH('dbo.LPMSIM_Output_Backup','Phase') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output_Backup
        ADD Phase varchar(8) NOT NULL CONSTRAINT DF_LPMSIM_Output_Backup_Phase DEFAULT ('P1');
    PRINT 'LPMSIM_Output_Backup: added Phase';
END
GO
IF COL_LENGTH('dbo.LPMSIM_Output_Backup','IsRoundRobin') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output_Backup
        ADD IsRoundRobin bit NOT NULL CONSTRAINT DF_LPMSIM_Output_Backup_IsRR DEFAULT (0);
    PRINT 'LPMSIM_Output_Backup: added IsRoundRobin';
END
GO

-- 2. LPMSIM_AllocTrace -----------------------------------------------------
IF COL_LENGTH('dbo.LPMSIM_AllocTrace','Phase') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_AllocTrace
        ADD Phase varchar(8) NOT NULL CONSTRAINT DF_LPMSIM_AllocTrace_Phase DEFAULT ('P1');
    PRINT 'LPMSIM_AllocTrace: added Phase';
END
GO

-- Widen Decision check: add ALLOC_RR.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_LPMSIM_AllocTrace_Decision')
BEGIN
    ALTER TABLE dbo.LPMSIM_AllocTrace DROP CONSTRAINT CK_LPMSIM_AllocTrace_Decision;
    PRINT 'Dropped old CK_LPMSIM_AllocTrace_Decision';
END
GO
ALTER TABLE dbo.LPMSIM_AllocTrace
    ADD CONSTRAINT CK_LPMSIM_AllocTrace_Decision CHECK
    (Decision IN ('ALLOC','ALLOC_RR','SKIP_SKUMAX','SKIP_TARGET','SKIP_NO_DIV','SKIP_NO_EOM'));
PRINT 'Re-added CK_LPMSIM_AllocTrace_Decision (incl ALLOC_RR)';
GO

-- 3. LPMSIM_StoreItemBalance ----------------------------------------------
IF OBJECT_ID('dbo.LPMSIM_StoreItemBalance','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_StoreItemBalance (
        LPMBatchNo          bigint        NOT NULL,
        StoreID             varchar(25)   NOT NULL,
        ItemCode            varchar(30)   NOT NULL,
        DivCode             int           NULL,
        SKUMax              int           NULL,
        SOH_Item            int           NOT NULL CONSTRAINT DF_SIB_SOH      DEFAULT (0),
        P1_NormalAlloc      int           NOT NULL CONSTRAINT DF_SIB_P1N      DEFAULT (0),
        P1_RR               int           NOT NULL CONSTRAINT DF_SIB_P1R      DEFAULT (0),
        P2_NormalAlloc      int           NOT NULL CONSTRAINT DF_SIB_P2N      DEFAULT (0),
        P2_RR               int           NOT NULL CONSTRAINT DF_SIB_P2R      DEFAULT (0),
        TotalAlloc          int           NOT NULL CONSTRAINT DF_SIB_Total    DEFAULT (0),
        SkuBalanceRemaining int           NULL,
        CreateTS            datetime2(0)  NOT NULL CONSTRAINT DF_SIB_CTS      DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPMSIM_StoreItemBalance PRIMARY KEY CLUSTERED (LPMBatchNo, StoreID, ItemCode),
        CONSTRAINT FK_LPMSIM_StoreItemBalance_Batch FOREIGN KEY (LPMBatchNo)
            REFERENCES dbo.LPMSIM_Batch(LPMBatchNo) ON DELETE CASCADE
    );
    CREATE INDEX IX_LPMSIM_SIB_Batch  ON dbo.LPMSIM_StoreItemBalance(LPMBatchNo);
    CREATE INDEX IX_LPMSIM_SIB_Store  ON dbo.LPMSIM_StoreItemBalance(LPMBatchNo, StoreID);
    PRINT 'Created dbo.LPMSIM_StoreItemBalance';
END
ELSE
    PRINT 'LPMSIM_StoreItemBalance already exists';
GO

-- 4. LPMSIM_StoreDivBalance -----------------------------------------------
IF OBJECT_ID('dbo.LPMSIM_StoreDivBalance','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_StoreDivBalance (
        LPMBatchNo          bigint        NOT NULL,
        StoreID             varchar(25)   NOT NULL,
        DivCode             int           NOT NULL,
        TargetEOM           decimal(18,4) NULL,
        DivSOH              int           NOT NULL CONSTRAINT DF_SDB_SOH      DEFAULT (0),
        P1_NormalAlloc      int           NOT NULL CONSTRAINT DF_SDB_P1N      DEFAULT (0),
        P1_RR               int           NOT NULL CONSTRAINT DF_SDB_P1R      DEFAULT (0),
        P2_NormalAlloc      int           NOT NULL CONSTRAINT DF_SDB_P2N      DEFAULT (0),
        P2_RR               int           NOT NULL CONSTRAINT DF_SDB_P2R      DEFAULT (0),
        TotalAlloc          int           NOT NULL CONSTRAINT DF_SDB_Total    DEFAULT (0),
        DivBalanceRemaining decimal(18,4) NULL,
        CreateTS            datetime2(0)  NOT NULL CONSTRAINT DF_SDB_CTS      DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPMSIM_StoreDivBalance PRIMARY KEY CLUSTERED (LPMBatchNo, StoreID, DivCode),
        CONSTRAINT FK_LPMSIM_StoreDivBalance_Batch FOREIGN KEY (LPMBatchNo)
            REFERENCES dbo.LPMSIM_Batch(LPMBatchNo) ON DELETE CASCADE
    );
    CREATE INDEX IX_LPMSIM_SDB_Batch ON dbo.LPMSIM_StoreDivBalance(LPMBatchNo);
    PRINT 'Created dbo.LPMSIM_StoreDivBalance';
END
ELSE
    PRINT 'LPMSIM_StoreDivBalance already exists';
GO

PRINT 'Migration 013 complete.';
