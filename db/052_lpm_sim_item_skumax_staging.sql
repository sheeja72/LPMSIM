-- ============================================================================
-- Migration 052 — LPM_SimItemSkuMax_Staging (persistent staging table)
--
-- 1.14.43 — Replaces the session-scoped #NewSnap temp table used by
-- BuildSkuMaxAsync with a persistent staging table. The intermittent
-- "Invalid object name '#NewSnap'" error documented in
-- LpmSimGenerator.cs:2920-2934 happens when SqlClient's connection
-- pooling triggers a session reset between the staging SqlCommand and
-- the exclusions SqlCommand on the same connection. Session-scoped
-- temp tables (#name) get dropped on session reset; persistent tables
-- (dbo.name) survive.
--
-- Schema mirrors #NewSnap's columns plus a Country column so concurrent
-- builds for different countries can coexist without colliding.
--
-- PK: (Country, StoreID, ItemCode, Season) — same clustered key shape
-- as the old #NewSnap clustered index, just scoped per Country.
--
-- Lifecycle per build:
--   1) BuildSkuMaxAsync begins: DELETE FROM staging WHERE Country = @c
--      (clears any leftover rows from prior runs / crashed builds)
--   2) INSERT staging rows for this build's (Country, Store × Item × Season)
--   3) Override rules UPDATE staging.SKUMax (zero-outs + price cap)
--   4) Delta-apply phase reads from staging, writes to LPM_SimItemSkuMax
--   5) Rows remain in staging until next build for that Country clears them
--      (not auto-cleaned — keeps tempdb pressure off, lets debug queries
--      inspect the last build's staging state if needed)
--
-- Idempotent: guarded by OBJECT_ID IS NULL.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_SimItemSkuMax_Staging', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_SimItemSkuMax_Staging (
        Country     varchar(20)  NOT NULL,
        StoreID     varchar(25)  NOT NULL,
        ItemCode    varchar(30)  NOT NULL,
        Season      char(1)      NOT NULL,
        DivCode     int          NOT NULL,
        WHBoxQty    bigint       NOT NULL,
        VolumeGroup varchar(20)  NULL,
        SKUMax      int          NOT NULL CONSTRAINT DF_LPM_SISMS_SKUMax DEFAULT (0),
        SOH         int          NOT NULL CONSTRAINT DF_LPM_SISMS_SOH    DEFAULT (0),
        CONSTRAINT PK_LPM_SimItemSkuMax_Staging
            PRIMARY KEY CLUSTERED (Country, StoreID, ItemCode, Season),
        CONSTRAINT CK_LPM_SimItemSkuMax_Staging_Season CHECK (Season IN ('S','W'))
    );
    PRINT 'Created dbo.LPM_SimItemSkuMax_Staging (mirror of #NewSnap + Country).';
END
ELSE
    PRINT 'LPM_SimItemSkuMax_Staging already exists — skipping.';
GO

PRINT 'Migration 052 complete.';
