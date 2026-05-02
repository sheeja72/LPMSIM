-- LPM SIM: Phase A tables for EOM Generate workflow.
SET XACT_ABORT ON;

-- 1. Sales + Turns source data (loaded from Excel)
IF OBJECT_ID('dbo.LPM_SalesTurns','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_SalesTurns (
        StoreID   varchar(25)   NOT NULL,
        DivCode   int           NOT NULL,
        Year1     int           NOT NULL,
        Month1    int           NOT NULL,
        SoldQty   decimal(18,4) NULL,
        TurnsQty  decimal(18,4) NULL,
        CreateTS  datetime2(0)  NOT NULL CONSTRAINT DF_LPM_SalesTurns_CreateTS DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPM_SalesTurns PRIMARY KEY CLUSTERED (StoreID, DivCode, Year1, Month1),
        CONSTRAINT CK_LPM_SalesTurns_Month CHECK (Month1 BETWEEN 1 AND 12),
        CONSTRAINT FK_LPM_SalesTurns_Division FOREIGN KEY (DivCode) REFERENCES dbo.Division(DivCode)
    );
    CREATE INDEX IX_LPM_SalesTurns_Period ON dbo.LPM_SalesTurns(Year1, Month1);
    PRINT 'Created dbo.LPM_SalesTurns';
END
GO

-- 2. Monthly weightage per run (13 periods, planning-team entered)
IF OBJECT_ID('dbo.LPM_MonthlyWeight','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_MonthlyWeight (
        Country     varchar(20)   NOT NULL,
        RunYear     int           NOT NULL,
        RunMonth    int           NOT NULL,
        PeriodSeq   int           NOT NULL,
        PeriodYear  int           NOT NULL,
        PeriodMonth int           NOT NULL,
        WeightPct   decimal(6,4)  NOT NULL,
        CreateTS    datetime2(0)  NOT NULL CONSTRAINT DF_LPM_MonthlyWeight_CreateTS DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPM_MonthlyWeight PRIMARY KEY CLUSTERED (Country, RunYear, RunMonth, PeriodSeq),
        CONSTRAINT CK_LPM_MonthlyWeight_Months CHECK (RunMonth BETWEEN 1 AND 12 AND PeriodMonth BETWEEN 1 AND 12)
    );
    PRINT 'Created dbo.LPM_MonthlyWeight';
END
GO

-- 3. Planned inputs per (Country, Division, Month)
IF OBJECT_ID('dbo.LPM_Planned','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_Planned (
        Country         varchar(20)   NOT NULL,
        DivCode         int           NOT NULL,
        Year1           int           NOT NULL,
        Month1          int           NOT NULL,
        PlannedTurn     decimal(18,4) NOT NULL,
        PlannedSalesQty decimal(18,4) NOT NULL,
        PlannedEOM      decimal(18,4) NOT NULL,
        UserID          varchar(100)  NULL,
        CreateTS        datetime2(0)  NOT NULL CONSTRAINT DF_LPM_Planned_CreateTS DEFAULT SYSDATETIME(),
        UpdatedTS       datetime2(0)  NULL,
        CONSTRAINT PK_LPM_Planned PRIMARY KEY CLUSTERED (Country, DivCode, Year1, Month1),
        CONSTRAINT CK_LPM_Planned_Month CHECK (Month1 BETWEEN 1 AND 12),
        CONSTRAINT FK_LPM_Planned_Division FOREIGN KEY (DivCode) REFERENCES dbo.Division(DivCode)
    );
    PRINT 'Created dbo.LPM_Planned';
END
GO

-- 4. Store grade configuration (single active set)
IF OBJECT_ID('dbo.LPM_StoreGrade','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_StoreGrade (
        GradeCode  varchar(20)   NOT NULL,
        GradeName  nvarchar(100) NOT NULL,
        SortOrder  int           NOT NULL,
        SharePct   decimal(6,4)  NOT NULL,
        MarkupPct  decimal(6,4)  NOT NULL,
        IsActive   bit           NOT NULL CONSTRAINT DF_LPM_StoreGrade_IsActive DEFAULT (1),
        CreateTS   datetime2(0)  NOT NULL CONSTRAINT DF_LPM_StoreGrade_CreateTS DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPM_StoreGrade PRIMARY KEY CLUSTERED (GradeCode)
    );
    PRINT 'Created dbo.LPM_StoreGrade';
END
GO

-- 5. Volume group configuration (single active set)
IF OBJECT_ID('dbo.LPM_VolumeGroup','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_VolumeGroup (
        GroupCode  varchar(20)   NOT NULL,
        GroupName  nvarchar(100) NOT NULL,
        SortOrder  int           NOT NULL,
        SharePct   decimal(6,4)  NOT NULL,
        IsActive   bit           NOT NULL CONSTRAINT DF_LPM_VolumeGroup_IsActive DEFAULT (1),
        CreateTS   datetime2(0)  NOT NULL CONSTRAINT DF_LPM_VolumeGroup_CreateTS DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPM_VolumeGroup PRIMARY KEY CLUSTERED (GroupCode)
    );
    PRINT 'Created dbo.LPM_VolumeGroup';
END
GO

-- 6. WH stock per (Country, Division, Month)
IF OBJECT_ID('dbo.LPM_WHStock','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_WHStock (
        Country     varchar(20)   NOT NULL,
        DivCode     int           NOT NULL,
        Year1       int           NOT NULL,
        Month1      int           NOT NULL,
        WHStockQty  int           NOT NULL,
        UserID      varchar(100)  NULL,
        CreateTS    datetime2(0)  NOT NULL CONSTRAINT DF_LPM_WHStock_CreateTS DEFAULT SYSDATETIME(),
        UpdatedTS   datetime2(0)  NULL,
        CONSTRAINT PK_LPM_WHStock PRIMARY KEY CLUSTERED (Country, DivCode, Year1, Month1),
        CONSTRAINT CK_LPM_WHStock_Month CHECK (Month1 BETWEEN 1 AND 12),
        CONSTRAINT FK_LPM_WHStock_Division FOREIGN KEY (DivCode) REFERENCES dbo.Division(DivCode)
    );
    PRINT 'Created dbo.LPM_WHStock';
END
GO

-- 7. SKU Max lookup rules (Volume Group × WH Stock range -> SKU Max)
IF OBJECT_ID('dbo.LPM_SKUMaxRule','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_SKUMaxRule (
        RuleId       int           NOT NULL IDENTITY(1,1),
        GroupCode    varchar(20)   NOT NULL,
        WHStockFrom  int           NOT NULL,
        WHStockTo    int           NOT NULL,
        SKUMax       int           NOT NULL,
        IsActive     bit           NOT NULL CONSTRAINT DF_LPM_SKUMaxRule_IsActive DEFAULT (1),
        CreateTS     datetime2(0)  NOT NULL CONSTRAINT DF_LPM_SKUMaxRule_CreateTS DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPM_SKUMaxRule PRIMARY KEY CLUSTERED (RuleId),
        CONSTRAINT FK_LPM_SKUMaxRule_Group FOREIGN KEY (GroupCode) REFERENCES dbo.LPM_VolumeGroup(GroupCode),
        CONSTRAINT CK_LPM_SKUMaxRule_Range CHECK (WHStockTo >= WHStockFrom)
    );
    CREATE INDEX IX_LPM_SKUMaxRule_Lookup ON dbo.LPM_SKUMaxRule(GroupCode, WHStockFrom, WHStockTo);
    PRINT 'Created dbo.LPM_SKUMaxRule';
END
GO

-- Seed default grades (25/20/20/15/20 % share; 40/20/20/10/10 % markup)
MERGE dbo.LPM_StoreGrade AS t
USING (VALUES
    ('Diamond',  N'Diamond',  1, 0.25, 0.40),
    ('Platinum', N'Platinum', 2, 0.20, 0.20),
    ('Gold',     N'Gold',     3, 0.20, 0.20),
    ('Silver',   N'Silver',   4, 0.15, 0.10),
    ('Bronze',   N'Bronze',   5, 0.20, 0.10)
) AS s(GradeCode, GradeName, SortOrder, SharePct, MarkupPct)
ON t.GradeCode = s.GradeCode
WHEN NOT MATCHED BY TARGET THEN
    INSERT (GradeCode, GradeName, SortOrder, SharePct, MarkupPct)
    VALUES (s.GradeCode, s.GradeName, s.SortOrder, s.SharePct, s.MarkupPct);

-- Seed default volume groups (25/20/20/15/20 % share)
MERGE dbo.LPM_VolumeGroup AS t
USING (VALUES
    ('A', N'A', 1, 0.25),
    ('B', N'B', 2, 0.20),
    ('C', N'C', 3, 0.20),
    ('D', N'D', 4, 0.15),
    ('E', N'E', 5, 0.20)
) AS s(GroupCode, GroupName, SortOrder, SharePct)
ON t.GroupCode = s.GroupCode
WHEN NOT MATCHED BY TARGET THEN
    INSERT (GroupCode, GroupName, SortOrder, SharePct)
    VALUES (s.GroupCode, s.GroupName, s.SortOrder, s.SharePct);

PRINT 'Phase A schema complete.';
