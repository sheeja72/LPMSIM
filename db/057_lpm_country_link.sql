-- ============================================================================
-- Migration 057 — LPM_CountryLink table
--
-- 1.14.77 — Parent-child country linkage. When a country (Child) is shipped
-- from another country's warehouse (Parent):
--   * EOM Generate for the Child uses the Parent's whboxitems source for WH
--     Stock (so OMAN EOM reads UAE warehouse, since OMAN has no WH of its own).
--   * SIM Generate for the Parent includes the Child's stores in the same
--     allocation run (UAE SIM allocates to both UAE and OMAN stores).
--   * Priority within each allocator phase: Parent's stores first, then
--     Child's stores (alphabetically when multiple children).
--
-- One row per (Parent, Child) pair. PK is composite. Seed: UAE → OMAN.
--
-- Idempotent: guarded by OBJECT_ID IS NULL. Re-running is safe.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_CountryLink', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_CountryLink (
        ParentCountry varchar(20)  NOT NULL,
        ChildCountry  varchar(20)  NOT NULL,
        IsActive      bit          NOT NULL CONSTRAINT DF_LPM_CountryLink_IsActive DEFAULT (1),
        CreateTS      datetime2(0) NOT NULL CONSTRAINT DF_LPM_CountryLink_CreateTS DEFAULT SYSDATETIME(),
        CreatedBy     varchar(100) NULL,
        CONSTRAINT PK_LPM_CountryLink PRIMARY KEY CLUSTERED (ParentCountry, ChildCountry),
        -- A child can only have ONE parent (one WH source) — guard with a
        -- unique index on ChildCountry alone. Multiple children per parent
        -- IS allowed (UAE could feed OMAN + BAH + … later).
        CONSTRAINT UQ_LPM_CountryLink_Child UNIQUE (ChildCountry)
    );
    PRINT 'Created dbo.LPM_CountryLink';
END
ELSE
    PRINT 'LPM_CountryLink already exists — skipping.';
GO

-- Seed the UAE → OMAN link (idempotent).
IF NOT EXISTS (SELECT 1 FROM dbo.LPM_CountryLink
                WHERE ParentCountry = 'UAE' AND ChildCountry = 'OMAN')
BEGIN
    INSERT INTO dbo.LPM_CountryLink (ParentCountry, ChildCountry, CreatedBy)
    VALUES ('UAE', 'OMAN', 'migration_057');
    PRINT 'Seeded UAE -> OMAN link.';
END
ELSE
    PRINT 'UAE -> OMAN link already present — skipping seed.';
GO

PRINT 'Migration 057 complete.';
