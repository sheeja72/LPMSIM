-- ============================================================================
-- Migration 017 — LPMSIM_BoxBalance
--
-- Per-box snapshot written at the end of every SIM Generate. One row per box
-- in the run, summing per-phase allocation and deriving Usability % so the
-- planning team can pull box-level numbers without aggregating LPMSIM_Output
-- on the fly.
--
-- Columns mirror the live SIM Boxes report tab so a SELECT * already gives
-- the same view shown in the UI.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPMSIM_BoxBalance','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_BoxBalance (
        LPMBatchNo      bigint        NOT NULL,
        BoxNo           varchar(25)   NOT NULL,
        BoxKind         varchar(8)    NOT NULL,                        -- 'LPM' | 'Non-LPM'
        LPMDt           date          NULL,
        BoxQty          bigint        NOT NULL CONSTRAINT DF_LBB_BoxQty   DEFAULT (0),
        P1_NormalAlloc  int           NOT NULL CONSTRAINT DF_LBB_P1N      DEFAULT (0),
        P1_RR           int           NOT NULL CONSTRAINT DF_LBB_P1R      DEFAULT (0),
        P2_NormalAlloc  int           NOT NULL CONSTRAINT DF_LBB_P2N      DEFAULT (0),
        P2_RR           int           NOT NULL CONSTRAINT DF_LBB_P2R      DEFAULT (0),
        TotalAlloc      bigint        NOT NULL CONSTRAINT DF_LBB_Total    DEFAULT (0),
        LeftOverQty     bigint        NOT NULL CONSTRAINT DF_LBB_Left     DEFAULT (0),
        UsabilityPct    decimal(6,1)  NOT NULL CONSTRAINT DF_LBB_Use      DEFAULT (0),
        CreateTS        datetime2(0)  NOT NULL CONSTRAINT DF_LBB_CTS      DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPMSIM_BoxBalance PRIMARY KEY CLUSTERED (LPMBatchNo, BoxNo),
        CONSTRAINT FK_LPMSIM_BoxBalance_Batch FOREIGN KEY (LPMBatchNo)
            REFERENCES dbo.LPMSIM_Batch(LPMBatchNo) ON DELETE CASCADE,
        CONSTRAINT CK_LPMSIM_BoxBalance_Kind CHECK (BoxKind IN ('LPM','Non-LPM'))
    );
    CREATE INDEX IX_LPMSIM_LBB_Batch ON dbo.LPMSIM_BoxBalance(LPMBatchNo);
    CREATE INDEX IX_LPMSIM_LBB_Use   ON dbo.LPMSIM_BoxBalance(LPMBatchNo, UsabilityPct);
    PRINT 'Created dbo.LPMSIM_BoxBalance';
END
ELSE
    PRINT 'LPMSIM_BoxBalance already exists';
GO

PRINT 'Migration 017 complete.';
