-- =============================================================================
-- 060_lpm_skumaxlock.sql  (1.14.102)
-- -----------------------------------------------------------------------------
-- LPM_SkuMaxLock — per-country lock for Build SKU Max.
--
-- Row presence = locked. Insert a row to block Build SKU Max for that country
-- (both the SkuMaxBuildJobManager.Start manual path AND the nightly
-- SkuMaxBuildScheduler auto path). DELETE the row to unlock.
--
-- The lock is per-country only (no Year/Month dimension) — the planner's stated
-- need is "lock UAE entirely until I unlock", not "lock UAE for May only".
--
-- Defaults:
--   LockedAt  -> SYSDATETIME()
--   LockedBy  -> '' (recommend setting it explicitly when locking via SSMS)
--   Reason    -> NULL  (free-text note for audit)
--
-- Audit trail: locks are not history-tracked (delete = unlock = row gone).
-- If audit history is needed later, switch to an IsLocked flag + keep the row.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LPM_SkuMaxLock' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LPM_SkuMaxLock (
        Country   varchar(50)  NOT NULL CONSTRAINT PK_LpmSkuMaxLock PRIMARY KEY,
        LockedAt  datetime     NOT NULL CONSTRAINT DF_LpmSkuMaxLock_LockedAt DEFAULT SYSDATETIME(),
        LockedBy  varchar(100) NOT NULL CONSTRAINT DF_LpmSkuMaxLock_LockedBy DEFAULT '',
        Reason    varchar(500) NULL
    );
END;
GO

-- Seed: lock UAE per planner request (1.14.102). Idempotent — re-running this
-- script won't double-insert and won't overwrite an existing reason.
IF NOT EXISTS (SELECT 1 FROM dbo.LPM_SkuMaxLock WHERE Country = 'UAE')
    INSERT INTO dbo.LPM_SkuMaxLock (Country, LockedBy, Reason)
    VALUES ('UAE', 'manual', 'Locked by user request on 2026-05-21 (1.14.102).');
GO
