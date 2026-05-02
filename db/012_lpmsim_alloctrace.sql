-- LPM SIM Allocation Trace
-- ----------------------------------------------------------------------------
-- One row per (Box, Item, Store) decision considered during a SIM run.
-- Captures the SKUMax / SOH / TargetEOM values used and the final outcome
-- (ALLOC | SKIP_SKUMAX | SKIP_TARGET | SKIP_NO_DIV | SKIP_NO_EOM).
--
-- Use this table to diagnose under-utilised eligible boxes:
--
--   SELECT Decision, COUNT(*), SUM(LineQty) AS Qty
--     FROM dbo.LPMSIM_AllocTrace
--    WHERE LPMBatchNo = <bn>
--    GROUP BY Decision;
--
--   -- Boxes where every candidate store was rejected:
--   SELECT BoxNo, ItemCode, MIN(Decision)
--     FROM dbo.LPMSIM_AllocTrace
--    WHERE LPMBatchNo = <bn>
--    GROUP BY BoxNo, ItemCode
--   HAVING SUM(CASE WHEN Decision = 'ALLOC' THEN 1 ELSE 0 END) = 0;
-- ----------------------------------------------------------------------------
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.LPMSIM_AllocTrace','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_AllocTrace (
        Id               bigint IDENTITY(1,1) NOT NULL,
        LPMBatchNo       bigint        NOT NULL,
        BoxNo            varchar(25)   NOT NULL,
        ItemCode         varchar(30)   NOT NULL,
        DivCode          int           NULL,                  -- NULL when item has no div mapping
        StoreID          varchar(25)   NULL,                  -- NULL for SKIP_NO_DIV / SKIP_NO_EOM
        SKUMax           int           NULL,
        SOH_Item         int           NULL,                  -- per-(store,item) SOH
        SkuBalance       int           NULL,                  -- SKUMax - SOH_Item
        TargetEOM        decimal(18,4) NULL,
        DivSOH           int           NULL,                  -- per-(store,div) SOH (used for WithoutLpmDate cap)
        AlreadyAllocated decimal(18,4) NULL,                  -- already allocated to (store,div) before this attempt
        TargetRemain     decimal(18,4) NULL,
        LineQty          int           NOT NULL,              -- qty available on the box for this item
        Take             int           NOT NULL,              -- units taken in this decision (0 if skipped)
        Decision         varchar(20)   NOT NULL,
        CreateTS         datetime2(0)  NOT NULL CONSTRAINT DF_LPMSIM_AllocTrace_CreateTS DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPMSIM_AllocTrace PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_LPMSIM_AllocTrace_Batch FOREIGN KEY (LPMBatchNo)
            REFERENCES dbo.LPMSIM_Batch(LPMBatchNo) ON DELETE CASCADE,
        CONSTRAINT CK_LPMSIM_AllocTrace_Decision CHECK
            (Decision IN ('ALLOC','SKIP_SKUMAX','SKIP_TARGET','SKIP_NO_DIV','SKIP_NO_EOM'))
    );
    CREATE INDEX IX_LPMSIM_AllocTrace_Batch    ON dbo.LPMSIM_AllocTrace(LPMBatchNo);
    CREATE INDEX IX_LPMSIM_AllocTrace_Box      ON dbo.LPMSIM_AllocTrace(LPMBatchNo, BoxNo);
    CREATE INDEX IX_LPMSIM_AllocTrace_Item     ON dbo.LPMSIM_AllocTrace(LPMBatchNo, ItemCode);
    CREATE INDEX IX_LPMSIM_AllocTrace_Store    ON dbo.LPMSIM_AllocTrace(LPMBatchNo, StoreID);
    CREATE INDEX IX_LPMSIM_AllocTrace_Decision ON dbo.LPMSIM_AllocTrace(LPMBatchNo, Decision);
    PRINT 'Created dbo.LPMSIM_AllocTrace';
END
ELSE
    PRINT 'dbo.LPMSIM_AllocTrace already exists';
GO
