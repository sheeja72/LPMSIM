-- ============================================================================
-- Migration 033 — ADM (Allocation Distribution Model)
--
-- Need-driven box-to-week allocator. For the run period, every eligible LPM
-- box gets placed in Week 1, 2, 3 or 4 (or deferred) based on:
--   • Merch Need (Week)        — demand from LPM_EOM_Output.MerchNeedWeek
--   • Priority Rank            — store priority from LPM_EOM_Output.PriorityRank
--   • Fill Rate per Division   — SOH / TargetEOM (most under-filled = first)
--   • Brand Variety            — hard cap per brand per week (default 25%)
--
-- See logic spec for the full algorithm. ADM is a separate engine from SIM:
--   • SIM = item-level allocation to stores
--   • ADM = box-level allocation to weeks of the month
--
-- Two tables, parent/child:
--   LPMSIM_AdmRun       — one header per (Country, RunDate)
--   LPMSIM_AdmBoxAlloc  — one row per (Run, Box) carrying Week + reason
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPMSIM_AdmRun', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_AdmRun (
        AdmRunNo            bigint IDENTITY(1, 1) NOT NULL,
        Country             varchar(20)   NOT NULL,
        RunDate             date          NOT NULL,
        RunYear             int           NOT NULL,
        RunMonth            int           NOT NULL,
        NumWeeks            int           NOT NULL CONSTRAINT DF_AdmRun_Weeks DEFAULT (4),
        Status              varchar(20)   NOT NULL CONSTRAINT DF_AdmRun_Status DEFAULT ('Draft'),

        -- The 3 planner levers.
        Week1TargetPct      decimal(5, 2) NOT NULL CONSTRAINT DF_AdmRun_W1T DEFAULT (25.00),  -- % of monthly LPM that ships Week 1
        BrandCapPct         decimal(5, 2) NOT NULL CONSTRAINT DF_AdmRun_BC  DEFAULT (25.00),  -- max % per brand per week
        ApplyVarietyBonus   bit           NOT NULL CONSTRAINT DF_AdmRun_VB  DEFAULT (1),       -- prefer fresh brands as tiebreak

        -- Snapshot of totals for the run (Eligible = passed Layer-1 filters;
        -- Scheduled = ended up with a Week assignment; Deferred = no week fit).
        TotalEligibleBoxes  int    NOT NULL CONSTRAINT DF_AdmRun_TotB DEFAULT (0),
        TotalEligibleQty    bigint NOT NULL CONSTRAINT DF_AdmRun_TotQ DEFAULT (0),
        ScheduledBoxes      int    NOT NULL CONSTRAINT DF_AdmRun_SchB DEFAULT (0),
        ScheduledQty        bigint NOT NULL CONSTRAINT DF_AdmRun_SchQ DEFAULT (0),
        DeferredBoxes       int    NOT NULL CONSTRAINT DF_AdmRun_DefB DEFAULT (0),
        DeferredQty         bigint NOT NULL CONSTRAINT DF_AdmRun_DefQ DEFAULT (0),

        CreateTS    datetime2(0) NOT NULL CONSTRAINT DF_AdmRun_CTS DEFAULT SYSDATETIME(),
        CreatedBy   varchar(80)  NOT NULL CONSTRAINT DF_AdmRun_CB  DEFAULT (''),
        ApprovedTS  datetime2(0) NULL,
        ApprovedBy  varchar(80)  NULL,

        CONSTRAINT PK_LPMSIM_AdmRun PRIMARY KEY CLUSTERED (AdmRunNo)
    );
    CREATE INDEX IX_LPMSIM_AdmRun_Period
        ON dbo.LPMSIM_AdmRun (Country, RunYear, RunMonth, RunDate);
    PRINT 'Created dbo.LPMSIM_AdmRun';
END
ELSE
    PRINT 'LPMSIM_AdmRun already exists';
GO

IF OBJECT_ID('dbo.LPMSIM_AdmBoxAlloc', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_AdmBoxAlloc (
        Id           bigint IDENTITY(1, 1) NOT NULL,
        AdmRunNo     bigint       NOT NULL,
        Week         int          NULL,           -- NULL = deferred (Reason explains why)

        -- Box identity (from racks.dbo.whboxitems).
        BoxNo        varchar(50)  NOT NULL,
        Warehouse    varchar(40)  NULL,
        LPMBrand     varchar(80)  NULL,           -- whboxitems.LPM (free-text brand)
        BoxQty       int          NOT NULL,
        DaysInDC     int          NOT NULL CONSTRAINT DF_AdmBox_Age DEFAULT (0),
        LPMDt        date         NULL,

        -- Division this box maps to (via item → upc_subclass → subclassmaster).
        DivCode      int          NULL,
        Division     varchar(80)  NULL,

        -- Snapshots used to rank (so the UI can show "why was this in W1?").
        DivFillRatePct  decimal(5, 2) NOT NULL CONSTRAINT DF_AdmBox_FR  DEFAULT (0),
        DivFillGapPct   decimal(5, 2) NOT NULL CONSTRAINT DF_AdmBox_FG  DEFAULT (0),
        BrandQtyAtPick  bigint        NOT NULL CONSTRAINT DF_AdmBox_BQ  DEFAULT (0),

        -- Outcome flags.
        Reason       varchar(40)  NOT NULL CONSTRAINT DF_AdmBox_R DEFAULT (''),  -- ALLOC, BRAND_CAP, WEEK_FULL, NO_DIV, etc.
        CreateTS     datetime2(0) NOT NULL CONSTRAINT DF_AdmBox_CTS DEFAULT SYSDATETIME(),

        CONSTRAINT PK_LPMSIM_AdmBoxAlloc PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_AdmBoxAlloc_Run FOREIGN KEY (AdmRunNo)
            REFERENCES dbo.LPMSIM_AdmRun(AdmRunNo) ON DELETE CASCADE
    );
    CREATE INDEX IX_LPMSIM_AdmBoxAlloc_Run_Week ON dbo.LPMSIM_AdmBoxAlloc (AdmRunNo, Week);
    CREATE INDEX IX_LPMSIM_AdmBoxAlloc_Run_Div  ON dbo.LPMSIM_AdmBoxAlloc (AdmRunNo, DivCode);
    CREATE INDEX IX_LPMSIM_AdmBoxAlloc_Run_Box  ON dbo.LPMSIM_AdmBoxAlloc (AdmRunNo, BoxNo);
    PRINT 'Created dbo.LPMSIM_AdmBoxAlloc';
END
ELSE
    PRINT 'LPMSIM_AdmBoxAlloc already exists';
GO

PRINT 'Migration 033 complete.';
