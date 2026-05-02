-- LPM SIM: add PlanningManager role and scope SKU Max rules per (Country, Division).
SET XACT_ABORT ON;

-- 1. PlanningManager role
IF NOT EXISTS (SELECT 1 FROM dbo.LPMRole WHERE RoleCode = 'PlanningManager')
BEGIN
    INSERT INTO dbo.LPMRole (RoleCode, RoleName)
    VALUES ('PlanningManager', N'Planning Manager');
    PRINT 'Added PlanningManager role.';
END

-- 2. Add Country + DivCode to LPM_SKUMaxRule
IF COL_LENGTH('dbo.LPM_SKUMaxRule', 'Country') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_SKUMaxRule ADD Country varchar(20) NULL;
    PRINT 'Added LPM_SKUMaxRule.Country';
END
GO

IF COL_LENGTH('dbo.LPM_SKUMaxRule', 'DivCode') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_SKUMaxRule ADD DivCode int NULL;
    PRINT 'Added LPM_SKUMaxRule.DivCode';
END
GO

-- 3. If empty, tighten to NOT NULL and add FK + new lookup index.
IF NOT EXISTS (SELECT 1 FROM dbo.LPM_SKUMaxRule WHERE Country IS NULL OR DivCode IS NULL)
BEGIN
    DECLARE @rowcount int = (SELECT COUNT(*) FROM dbo.LPM_SKUMaxRule);

    IF @rowcount = 0
    BEGIN
        ALTER TABLE dbo.LPM_SKUMaxRule ALTER COLUMN Country varchar(20) NOT NULL;
        ALTER TABLE dbo.LPM_SKUMaxRule ALTER COLUMN DivCode int NOT NULL;

        IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_LPM_SKUMaxRule_Division')
            ALTER TABLE dbo.LPM_SKUMaxRule
                ADD CONSTRAINT FK_LPM_SKUMaxRule_Division
                    FOREIGN KEY (DivCode) REFERENCES dbo.Division(DivCode);

        IF EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_LPM_SKUMaxRule_Lookup'
                     AND object_id = OBJECT_ID('dbo.LPM_SKUMaxRule'))
            DROP INDEX IX_LPM_SKUMaxRule_Lookup ON dbo.LPM_SKUMaxRule;

        CREATE INDEX IX_LPM_SKUMaxRule_Lookup
            ON dbo.LPM_SKUMaxRule(Country, DivCode, GroupCode, WHStockFrom, WHStockTo);

        PRINT 'Tightened LPM_SKUMaxRule columns to NOT NULL + FK + new lookup index.';
    END
    ELSE
        PRINT 'LPM_SKUMaxRule has existing rows; Country/DivCode left nullable until backfilled.';
END
GO

PRINT 'Role + SKU Max scoping migration complete.';
