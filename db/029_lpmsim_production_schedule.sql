-- ============================================================================
-- Migration 029 — Production Schedule
--
-- Adds the day-level production scheduling layer on top of an existing SIM
-- batch. Each LPMSIM_Output row gets an optional Day (1..N) tagging which
-- production day will produce that box, and a new LPMSIM_ProductionSchedule
-- header table holds the per-batch schedule metadata.
--
--   • LPMSIM_Output.Day               -- 1..N or NULL (deferred / unscheduled)
--   • LPMSIM_Output_Backup.Day        -- carried forward when batch is archived
--   • LPMSIM_ProductionSchedule       -- one row per SIM batch
--                                        (DailyTarget, DaysInWeek, MinUsab%,
--                                         Status, Approve metadata)
--
-- Each step is its own GO batch and is fully idempotent — safe to re-run if
-- a previous attempt was cancelled mid-way.
--
-- IMPORTANT: stop the dev server before running, so it isn't holding
-- schema-stability locks on LPMSIM_Output. Otherwise the ALTER TABLE / CREATE
-- INDEX will block waiting for an exclusive schema lock.
-- ============================================================================
SET XACT_ABORT ON;
SET NOCOUNT  ON;
GO

-- 1) Add Day to LPMSIM_Output (column add is metadata-only, sub-second).
IF COL_LENGTH('dbo.LPMSIM_Output', 'Day') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output ADD [Day] int NULL;
    PRINT '  ✓ LPMSIM_Output: added Day column';
END
ELSE
    PRINT '  • LPMSIM_Output.Day already exists — skipped';
GO

-- 2) Index on (LPMBatchNo, Day). This is the most expensive step on a large
--    table — guarded by IF NOT EXISTS so re-runs don't error out.
--    On big tables consider running with WITH (ONLINE = ON) on Enterprise.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE name = 'IX_LPMSIM_Output_BatchDay'
                  AND object_id = OBJECT_ID('dbo.LPMSIM_Output'))
BEGIN
    PRINT '  … creating IX_LPMSIM_Output_BatchDay (may take a while on a large table)';
    CREATE INDEX IX_LPMSIM_Output_BatchDay
        ON dbo.LPMSIM_Output (LPMBatchNo, [Day]);
    PRINT '  ✓ IX_LPMSIM_Output_BatchDay created';
END
ELSE
    PRINT '  • IX_LPMSIM_Output_BatchDay already exists — skipped';
GO

-- 3) Add Day to backup (sub-second — backup table is usually empty or small).
IF COL_LENGTH('dbo.LPMSIM_Output_Backup', 'Day') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_Output_Backup ADD [Day] int NULL;
    PRINT '  ✓ LPMSIM_Output_Backup: added Day column';
END
ELSE
    PRINT '  • LPMSIM_Output_Backup.Day already exists — skipped';
GO

-- 4) Header table — one row per SIM batch with the schedule's parameters + status.
IF OBJECT_ID('dbo.LPMSIM_ProductionSchedule', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_ProductionSchedule (
        LPMBatchNo          bigint        NOT NULL,
        DailyTargetQty      int           NOT NULL CONSTRAINT DF_LPMSIM_PS_Target  DEFAULT (55000),
        DaysInWeek          int           NOT NULL CONSTRAINT DF_LPMSIM_PS_Days    DEFAULT (6),
        MinUsabilityPct     decimal(5,2)  NOT NULL CONSTRAINT DF_LPMSIM_PS_MinPct  DEFAULT (0),
        Status              varchar(20)   NOT NULL CONSTRAINT DF_LPMSIM_PS_Status  DEFAULT ('Draft'),
        EligibleBoxes       int           NOT NULL CONSTRAINT DF_LPMSIM_PS_EligB   DEFAULT (0),
        EligibleQty         bigint        NOT NULL CONSTRAINT DF_LPMSIM_PS_EligQ   DEFAULT (0),
        ScheduledBoxes      int           NOT NULL CONSTRAINT DF_LPMSIM_PS_SchB    DEFAULT (0),
        ScheduledQty        bigint        NOT NULL CONSTRAINT DF_LPMSIM_PS_SchQ    DEFAULT (0),
        DeferredBoxes       int           NOT NULL CONSTRAINT DF_LPMSIM_PS_DefB    DEFAULT (0),
        DeferredQty         bigint        NOT NULL CONSTRAINT DF_LPMSIM_PS_DefQ    DEFAULT (0),
        CreateTS            datetime2(0)  NOT NULL CONSTRAINT DF_LPMSIM_PS_CTS     DEFAULT SYSDATETIME(),
        CreatedBy           varchar(100)  NOT NULL,
        ApprovedTS          datetime2(0)  NULL,
        ApprovedBy          varchar(100)  NULL,
        CONSTRAINT PK_LPMSIM_ProductionSchedule PRIMARY KEY CLUSTERED (LPMBatchNo),
        CONSTRAINT FK_LPMSIM_ProductionSchedule_Batch
            FOREIGN KEY (LPMBatchNo) REFERENCES dbo.LPMSIM_Batch(LPMBatchNo) ON DELETE CASCADE,
        CONSTRAINT CK_LPMSIM_PS_Status CHECK (Status IN ('Draft','Approved')),
        CONSTRAINT CK_LPMSIM_PS_DaysInWeek CHECK (DaysInWeek BETWEEN 1 AND 14)
    );
    PRINT '  ✓ Created dbo.LPMSIM_ProductionSchedule';
END
ELSE
    PRINT '  • LPMSIM_ProductionSchedule already exists — skipped';
GO

PRINT '─── Migration 029 complete. ───────────────────────────────';
