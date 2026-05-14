-- ============================================================================
-- Migration 045 — LPM_SimItemSkuMax_Backup table
--
-- Archives older periods of LPM_SimItemSkuMax so the active production table
-- only holds the latest period per country. Build SKU Max moves any rows
-- whose (Year1, Month1) is STRICTLY OLDER than the period being built (same
-- Country) into this backup, then deletes them from the main table.
--
-- Schema mirrors LPM_SimItemSkuMax exactly + (BackupTS, BackupBy). Same
-- pattern as LPMSIM_Output_Backup (migration 009) and LPMSIM_Batch_Backup.
--
-- PK includes BackupTS so the same row can be backed up multiple times if a
-- user rebuilds an old period; otherwise the archive INSERT would fail with
-- a unique-key violation on the second attempt.
--
-- Idempotent: guarded by OBJECT_ID IS NULL.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_SimItemSkuMax_Backup', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_SimItemSkuMax_Backup (
        Country     varchar(20)  NOT NULL,
        Year1       int          NOT NULL,
        Month1      int          NOT NULL,
        StoreID     varchar(25)  NOT NULL,
        ItemCode    varchar(30)  NOT NULL,
        Season      char(1)      NOT NULL,
        DivCode     int          NOT NULL,
        WHBoxQty    bigint       NOT NULL CONSTRAINT DF_LSISMBKP_WH       DEFAULT (0),
        VolumeGroup varchar(20)  NULL,
        SKUMax      int          NOT NULL CONSTRAINT DF_LSISMBKP_SKU      DEFAULT (0),
        CreateTS    datetime2(0) NOT NULL,
        CreatedBy   varchar(100) NULL,
        BackupTS    datetime2(0) NOT NULL CONSTRAINT DF_LSISMBKP_BackupTS DEFAULT SYSDATETIME(),
        BackupBy    varchar(100) NULL,
        CONSTRAINT PK_LPM_SimItemSkuMax_Backup PRIMARY KEY CLUSTERED
            (Country, Year1, Month1, StoreID, ItemCode, Season, BackupTS),
        CONSTRAINT CK_LPM_SimItemSkuMax_Backup_Season CHECK (Season IN ('S','W'))
    );
    -- Period-scoped lookup index — most queries want "what did UAE 2026-04
    -- look like" and don't care about per-store/item slicing within that.
    CREATE INDEX IX_LPM_SimItemSkuMax_Backup_Period
        ON dbo.LPM_SimItemSkuMax_Backup (Country, Year1, Month1)
        INCLUDE (StoreID, ItemCode, Season, SKUMax, WHBoxQty, DivCode, VolumeGroup);
    PRINT 'Created dbo.LPM_SimItemSkuMax_Backup';
END
ELSE
    PRINT 'LPM_SimItemSkuMax_Backup already exists';
GO

PRINT 'Migration 045 complete.';
