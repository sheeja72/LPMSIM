-- ============================================================================
-- Migration 050 — fix LPM_SKUMaxRule.IX_LPM_SKUMaxRule_Lookup leading column
--
-- 1.14.37 — The SKU Max Rules admin page (and BuildSkuMax's rule-lookup
-- query) filter by Country first:
--
--     SELECT ... FROM LPM_SKUMaxRule WHERE Country = @c [AND DivCode = @d]
--
-- The optimal index leads with Country. Migration 008 was meant to create
-- it, BUT only if the table was empty at migration time:
--
--     IF NOT EXISTS (... WHERE Country IS NULL OR DivCode IS NULL) AND
--        rowcount = 0 THEN
--         DROP IX_LPM_SKUMaxRule_Lookup (old GroupCode-leading shape)
--         CREATE IX_LPM_SKUMaxRule_Lookup (Country, DivCode, ...)
--
-- Servers that had rules in LPM_SKUMaxRule when 008 ran kept the original
-- (GroupCode, WHStockFrom, WHStockTo) index from migration 006. Every
-- `WHERE Country = ?` becomes a full clustered-index scan. On the SKU Max
-- Rules admin page, picking a country with no rules (e.g. KUWAIT) still
-- forces a scan of every row to confirm there are none — the planner sees
-- a noticeable lag.
--
-- This migration unconditionally ensures the index exists in the right
-- shape. Idempotent:
--   • If the index already leads with Country → leave alone.
--   • If it exists with a different leading column → drop + recreate.
--   • If it doesn't exist → create.
--
-- The INCLUDE list (SKUMax, IsActive) covers the BuildSkuMax rule-lookup
-- SELECT so it can be served entirely from the index without touching the
-- clustered table.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_SKUMaxRule', 'U') IS NULL
BEGIN
    PRINT 'LPM_SKUMaxRule table not found — skipping (run migration 006 first).';
    SET NOEXEC ON;
END
GO

-- Detect current state: is there an index on the table whose LEADING
-- key column is Country? (Any name. Any type. Just needs Country first.)
DECLARE @hasCountryLeadingIx bit = (
    CASE WHEN EXISTS (
        SELECT 1
          FROM sys.indexes i
          INNER JOIN sys.index_columns ic
                  ON ic.object_id = i.object_id AND ic.index_id = i.index_id
          INNER JOIN sys.columns c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
         WHERE i.object_id = OBJECT_ID('dbo.LPM_SKUMaxRule')
           AND i.is_primary_key = 0
           AND ic.key_ordinal = 1
           AND c.name = 'Country'
    ) THEN 1 ELSE 0 END
);

IF @hasCountryLeadingIx = 1
BEGIN
    PRINT 'LPM_SKUMaxRule already has a Country-leading index — leaving alone.';
END
ELSE
BEGIN
    -- Drop old IX_LPM_SKUMaxRule_Lookup (any shape) if present.
    -- Migration 006 created it as (GroupCode, WHStockFrom, WHStockTo) —
    -- that shape doesn't serve any current code path. The new index below
    -- replaces it.
    IF EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name = 'IX_LPM_SKUMaxRule_Lookup'
                  AND object_id = OBJECT_ID('dbo.LPM_SKUMaxRule'))
    BEGIN
        DROP INDEX IX_LPM_SKUMaxRule_Lookup ON dbo.LPM_SKUMaxRule;
        PRINT 'Dropped old IX_LPM_SKUMaxRule_Lookup (old GroupCode-leading shape).';
    END

    -- Country/DivCode columns might still be nullable if migration 008
    -- couldn't tighten them (because the table had pre-existing rows with
    -- NULL Country at the time). Indexes work fine on nullable columns —
    -- no need to alter the columns here.
    CREATE INDEX IX_LPM_SKUMaxRule_Lookup
        ON dbo.LPM_SKUMaxRule (Country, DivCode, GroupCode, WHStockFrom, WHStockTo)
        INCLUDE (SKUMax, IsActive);

    PRINT 'Created IX_LPM_SKUMaxRule_Lookup (Country, DivCode, GroupCode, WHStockFrom, WHStockTo) INCLUDE (SKUMax, IsActive).';
END
GO

SET NOEXEC OFF;
GO

PRINT 'Migration 050 complete.';
