-- ============================================================================
-- Migration 032 — LPM_SimItemSkuMaxBuild
--
-- Per-(Country, Year, Month) header row capturing the timing + outcome of
-- the most recent SKU Max build. SIM Generate's UI reads this so it can
-- display "Built 06-May 10:30 · 11.3M rows · in 5m 30s" — the duration was
-- previously held only in memory by SkuMaxBuildJobManager and disappeared
-- once the server restarted or 60 minutes passed.
--
-- One row per period. Updated (or inserted) at the end of every successful
-- build via UPSERT (MERGE-style).
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_SimItemSkuMaxBuild', 'U') IS NULL
BEGIN
    -- NB: [RowCount] is bracketed because RowCount is a SQL Server reserved
    -- keyword (clashes with @@ROWCOUNT and SET ROWCOUNT). Without the
    -- brackets the CREATE TABLE fails with "Incorrect syntax near 'RowCount'".
    CREATE TABLE dbo.LPM_SimItemSkuMaxBuild (
        Country     varchar(20)   NOT NULL,
        Year1       int           NOT NULL,
        Month1      int           NOT NULL,
        BuildStart  datetime2(0)  NOT NULL,
        BuildEnd    datetime2(0)  NOT NULL,
        DurationMs  bigint        NOT NULL,
        [RowCount]  bigint        NOT NULL,
        BuiltBy     varchar(80)   NULL,
        StageDetail nvarchar(500) NULL,                              -- "Done · Delete X · ItemWh Y · Insert Z"
        CONSTRAINT PK_LPM_SimItemSkuMaxBuild PRIMARY KEY CLUSTERED
            (Country, Year1, Month1)
    );
    PRINT 'Created dbo.LPM_SimItemSkuMaxBuild';
END
ELSE
    PRINT 'LPM_SimItemSkuMaxBuild already exists';
GO

PRINT 'Migration 032 complete.';
