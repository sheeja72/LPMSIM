-- ============================================================================
-- Migration 037 — LPM_WarehousePriority
--
-- Per-(Country, Warehouse) priority used by ReadBoxesAsync to order the box
-- stream the SIM allocator processes. Lower number = higher priority
-- (1 = first). Warehouses NOT listed in this table fall back to priority
-- 9999 (after every prioritised one), so the table only needs rows for the
-- warehouses you actually want to rank — the rest stay in the default
-- "BoxQty DESC, BoxNo" order.
--
-- Replaces the per-Generate UI chip-list. Planners told us the priority
-- order doesn't change run-to-run; storing it once in this table avoids the
-- need to re-set the chip order on every Generate.
--
-- Maintain via SSMS for now. A future "Warehouse Priorities" admin page can
-- read/write this table; the SIM Generate flow only reads from it.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPM_WarehousePriority', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_WarehousePriority (
        Country   varchar(20)  NOT NULL,
        Warehouse varchar(50)  NOT NULL,
        Priority  int          NOT NULL,                                       -- 1 = highest, 2, 3, ...
        IsActive  bit          NOT NULL CONSTRAINT DF_LWP_IsActive  DEFAULT (1),
        CreateTS  datetime2(0) NOT NULL CONSTRAINT DF_LWP_CreateTS  DEFAULT SYSDATETIME(),
        CreatedBy varchar(100) NULL,
        UpdatedTS datetime2(0) NULL,
        UpdatedBy varchar(100) NULL,
        CONSTRAINT PK_LPM_WarehousePriority PRIMARY KEY (Country, Warehouse)
    );
    PRINT 'Created dbo.LPM_WarehousePriority';
END
ELSE
    PRINT 'LPM_WarehousePriority already exists';
GO

-- Optional starter data for UAE — adjust to your actual warehouses + ranking.
-- Run this manually once you confirm the priority order. The IF NOT EXISTS
-- guard makes the migration idempotent.
/*
IF NOT EXISTS (SELECT 1 FROM dbo.LPM_WarehousePriority WHERE Country = 'UAE')
BEGIN
    INSERT INTO dbo.LPM_WarehousePriority (Country, Warehouse, Priority, CreatedBy)
    VALUES
        ('UAE', 'BLACKBOX', 1, 'migration_037'),
        ('UAE', 'JAFZA',    2, 'migration_037'),
        ('UAE', 'TECHNO',   3, 'migration_037'),
        ('UAE', 'TECHNO-E', 4, 'migration_037'),
        ('UAE', 'YOTO',     5, 'migration_037'),
        ('UAE', 'YOTO-BU',  6, 'migration_037');
END
*/

PRINT 'Migration 037 complete.';
