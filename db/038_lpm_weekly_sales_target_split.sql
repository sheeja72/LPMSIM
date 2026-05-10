-- ============================================================================
-- Migration 038 — LPM_WeeklySalesTargetSplit
--
-- Per-(Country, Year, Month, DivCode, WeekNo) split percentage of the
-- monthly Target Sales across the 4 logical weeks of the run month.
-- Drives the new Merch Need (Week) formula:
--
--     MerchNeedWeekN = (TargetEOM − SOH) / 4
--                    + (TargetSales × SplitPct[N] / 100)        for N = 1..4
--
-- Logical weeks (NOT ISO weeks):
--     Week 1 = days 1–7
--     Week 2 = days 8–14
--     Week 3 = days 15–21
--     Week 4 = days 22–end (absorbs days 29–31 in 30/31-day months)
--
-- When no row exists for a given (Country, Year, Month, DivCode), the
-- EomCalculator falls back to the hard-coded default split
-- 20% / 20% / 25% / 35% (sums to 100). The new "Weekly Sales Target Split"
-- admin page (under Planning Config) writes rows to this table to override
-- the default per (Country, Year, Month, Div).
--
-- Validation: the admin page enforces sum(SplitPct) = 100 across the 4
-- weeks of a (Country, Year, Month, Div) before allowing save. The CHECK
-- constraint here only ranges 0–100 per row (relying on app-side total
-- validation since CHECK can't span rows).
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_WeeklySalesTargetSplit', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_WeeklySalesTargetSplit (
        Id        int           IDENTITY(1,1) NOT NULL,
        Country   varchar(20)   NOT NULL,
        Year1     int           NOT NULL,
        Month1    int           NOT NULL,
        DivCode   int           NOT NULL,
        WeekNo    tinyint       NOT NULL,
        SplitPct  decimal(7,4)  NOT NULL,
        IsActive  bit           NOT NULL CONSTRAINT DF_LWSTS_IsActive  DEFAULT (1),
        CreateTS  datetime2(0)  NOT NULL CONSTRAINT DF_LWSTS_CreateTS  DEFAULT SYSDATETIME(),
        CreateBy  varchar(100)  NULL,
        UpdatedTS datetime2(0)  NULL,
        UpdatedBy varchar(100)  NULL,

        CONSTRAINT PK_LPM_WeeklySalesTargetSplit PRIMARY KEY (Id),
        CONSTRAINT UQ_LPM_WeeklySalesTargetSplit UNIQUE (Country, Year1, Month1, DivCode, WeekNo),
        CONSTRAINT CK_LWSTS_WeekNo   CHECK (WeekNo  BETWEEN 1 AND 4),
        CONSTRAINT CK_LWSTS_Month    CHECK (Month1  BETWEEN 1 AND 12),
        CONSTRAINT CK_LWSTS_SplitPct CHECK (SplitPct >= 0 AND SplitPct <= 100)
    );
    PRINT 'Created dbo.LPM_WeeklySalesTargetSplit';
END
ELSE
    PRINT 'LPM_WeeklySalesTargetSplit already exists';
GO

-- Lookup index for the EomCalculator load: it pulls every (DivCode, WeekNo)
-- row for a single (Country, Year, Month). The unique constraint above
-- already covers this prefix, so no extra index is needed — left here as a
-- comment so future maintainers don't re-add one.
-- CREATE INDEX IX_LWSTS_Period ON dbo.LPM_WeeklySalesTargetSplit (Country, Year1, Month1) INCLUDE (DivCode, WeekNo, SplitPct, IsActive);

PRINT 'Migration 038 complete.';
