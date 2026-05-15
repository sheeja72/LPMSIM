-- ============================================================================
-- Migration 046 — LPMSIM_UnallocatedDiagnostic
--
-- Per-box diagnostic written at the end of every successful SIM Generate.
-- Captures, for each eligible box that DID NOT fully allocate, why qty
-- remained on the warehouse side. Lets the planner answer the question
-- "we had 32,930 Non-LPM Summer boxes eligible but only X allocated — why
-- the gap?" without re-running the build.
--
-- ONE row per eligible (LPMBatchNo, BoxNo) where RemainingQty > 0.
-- Boxes fully allocated (RemainingQty = 0) are NOT written here — they're
-- already covered by the existing LPMSIM_Output / SIM Boxes view.
--
-- TopReason values:
--   • FILTERED_SEASON     — Box was eligible per the SQL filter but every
--                           item's w.Season failed the user's Season choice
--                           (the per-item drop introduced in 1.14.18). Box
--                           never entered the allocator.
--   • SKIP_NO_DIV         — Item missing from upc_subclass — can't classify.
--   • SKIP_NO_EOM         — Division has no EOM Output rows for this period.
--   • CAP                 — SKU Max or EOM Merch Need (Week) cap hit at every
--                           eligible store. Deduced from ALLOC trace rows in
--                           non-verbose mode (SKIP_SKUMAX / SKIP_TARGET are
--                           dropped by the trace filter). Use Verbose Trace
--                           on the run to see the precise SKU vs Target split
--                           in the existing Allocation Trace tab.
--   • UNKNOWN             — Defensive fallback. Should never appear in
--                           practice; investigate if you see one.
--
-- Forward-fill only — pre-1.14.26 batches have no rows here. Regenerate to
-- populate diagnostic for an older period if needed.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPMSIM_UnallocatedDiagnostic', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_UnallocatedDiagnostic (
        LPMBatchNo   bigint        NOT NULL,
        BoxNo        varchar(25)   NOT NULL,
        PalletNo     varchar(50)   NULL,
        LPMDt        date          NULL,
        BoxKind      varchar(10)   NOT NULL,                            -- 'LPM' or 'Non-LPM'
        BoxQty       bigint        NOT NULL,
        SimQty       bigint        NOT NULL CONSTRAINT DF_LSUD_SimQty   DEFAULT (0),
        -- Computed + persisted so ORDER BY RemainingQty DESC is index-friendly.
        RemainingQty AS (BoxQty - SimQty) PERSISTED,
        TopReason    varchar(50)   NOT NULL,
        Reasons      nvarchar(400) NULL,                                -- 'SKIP_NO_DIV (3) · SKIP_NO_EOM (2) · CAP (45)'
        CreateTS     datetime2(0)  NOT NULL CONSTRAINT DF_LSUD_CTS      DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPMSIM_UnallocatedDiagnostic PRIMARY KEY CLUSTERED (LPMBatchNo, BoxNo),
        CONSTRAINT FK_LSUD_Batch FOREIGN KEY (LPMBatchNo)
            REFERENCES dbo.LPMSIM_Batch(LPMBatchNo) ON DELETE CASCADE,
        CONSTRAINT CK_LSUD_BoxKind CHECK (BoxKind IN ('LPM','Non-LPM')),
        CONSTRAINT CK_LSUD_TopReason CHECK
            (TopReason IN ('FILTERED_SEASON','SKIP_NO_DIV','SKIP_NO_EOM','CAP','UNKNOWN'))
    );
    -- Most common UI queries: "show gaps for this batch sorted by remaining DESC,
    -- optionally filtered by reason or kind". The PK already supports the batch-
    -- scoped lookup; this NCI adds reason / kind filtering speed.
    CREATE INDEX IX_LPMSIM_UnallocatedDiagnostic_Reason
        ON dbo.LPMSIM_UnallocatedDiagnostic (LPMBatchNo, TopReason, BoxKind)
        INCLUDE (BoxNo, BoxQty, SimQty, RemainingQty);
    PRINT 'Created dbo.LPMSIM_UnallocatedDiagnostic';
END
ELSE
    PRINT 'LPMSIM_UnallocatedDiagnostic already exists';
GO

PRINT 'Migration 046 complete.';
