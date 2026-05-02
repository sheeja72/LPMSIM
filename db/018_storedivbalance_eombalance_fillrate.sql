-- ============================================================================
-- Migration 018 — Add EomBalance + FillRate to LPMSIM_StoreDivBalance.
--
-- Both columns are PERSISTED computed columns so they stay in sync with
-- TargetEOM / DivSOH / TotalAlloc without needing the engine to write them.
-- They mirror the on-the-fly query the planning team has been writing:
--
--     EomBalance = TargetEOM − DivSOH
--     FillRate   = TotalAlloc × 100 / NULLIF(EomBalance, 0)
--
-- Once the columns exist, every existing row gets populated automatically.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPMSIM_StoreDivBalance', 'EomBalance') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_StoreDivBalance
        ADD EomBalance AS (CONVERT(decimal(18,4), TargetEOM) - DivSOH) PERSISTED;
    PRINT 'LPMSIM_StoreDivBalance: added EomBalance';
END
ELSE
    PRINT 'LPMSIM_StoreDivBalance.EomBalance already exists';
GO

IF COL_LENGTH('dbo.LPMSIM_StoreDivBalance', 'FillRate') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_StoreDivBalance
        ADD FillRate AS (
            CASE
                WHEN (CONVERT(decimal(18,4), TargetEOM) - DivSOH) > 0
                THEN ROUND(CAST(TotalAlloc AS decimal(18,4)) * 100.0
                           / (CONVERT(decimal(18,4), TargetEOM) - DivSOH), 2)
                ELSE 0
            END
        ) PERSISTED;
    PRINT 'LPMSIM_StoreDivBalance: added FillRate';
END
ELSE
    PRINT 'LPMSIM_StoreDivBalance.FillRate already exists';
GO

PRINT 'Migration 018 complete.';
