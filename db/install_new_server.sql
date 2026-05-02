-- LPM SIM : fresh install on BFL-LOGBACKUP\LOGBACKUP / LPMSIM.
-- Creates every table in its FINAL schema (no migration history).
-- Data must be loaded separately via BCP (except the small seed/master tables).
SET XACT_ABORT ON;
SET NOCOUNT ON;

-- ============================================================
-- 1. Division (21 rows) — created with PK; inserts appended from source.
-- ============================================================
IF OBJECT_ID('dbo.Division','U') IS NULL
BEGIN
    CREATE TABLE dbo.Division (
        DivCode  int NOT NULL,
        Division varchar(50) NULL,
        CONSTRAINT PK_Division PRIMARY KEY CLUSTERED (DivCode)
    );
    PRINT 'Created dbo.Division';
END
GO

-- ============================================================
-- 2. DataSettings (145 columns — cloned from 10.72 schema)
--    The column list below matches the source table exactly.
-- ============================================================
IF OBJECT_ID('dbo.DataSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.DataSettings (
--DATASETTINGS_COLUMNS--
    );
    PRINT 'Created dbo.DataSettings';
END
GO

-- ============================================================
-- 3. LPM_* domain tables
-- ============================================================
IF OBJECT_ID('dbo.LPMDivMax','U') IS NULL
CREATE TABLE dbo.LPMDivMax (
    StoreID    varchar(25)  NOT NULL,
    DivCode    int          NOT NULL,
    MaxQty     int          NOT NULL,
    CreateTS   datetime2(0) NOT NULL CONSTRAINT DF_LPMDivMax_CreateTS  DEFAULT SYSDATETIME(),
    UpdatedTS  datetime2(0) NOT NULL CONSTRAINT DF_LPMDivMax_UpdatedTS DEFAULT SYSDATETIME(),
    UserID     varchar(100) NOT NULL,
    CONSTRAINT PK_LPMDivMax          PRIMARY KEY CLUSTERED (StoreID, DivCode),
    CONSTRAINT FK_LPMDivMax_Division FOREIGN KEY (DivCode) REFERENCES dbo.Division(DivCode)
);
GO

IF OBJECT_ID('dbo.LPMUser','U') IS NULL
CREATE TABLE dbo.LPMUser (
    Username     varchar(100)  NOT NULL,
    DisplayName  nvarchar(200) NULL,
    Email        varchar(200)  NULL,
    IsActive     bit           NOT NULL CONSTRAINT DF_LPMUser_IsActive  DEFAULT (1),
    CreateTS     datetime2(0)  NOT NULL CONSTRAINT DF_LPMUser_CreateTS  DEFAULT SYSDATETIME(),
    CreatedBy    varchar(100)  NOT NULL,
    CONSTRAINT PK_LPMUser PRIMARY KEY CLUSTERED (Username)
);
GO

IF OBJECT_ID('dbo.LPMRole','U') IS NULL
CREATE TABLE dbo.LPMRole (
    RoleCode  varchar(20)   NOT NULL,
    RoleName  nvarchar(100) NOT NULL,
    CreateTS  datetime2(0)  NOT NULL CONSTRAINT DF_LPMRole_CreateTS DEFAULT SYSDATETIME(),
    CONSTRAINT PK_LPMRole PRIMARY KEY CLUSTERED (RoleCode)
);
GO

IF OBJECT_ID('dbo.LPMUserRole','U') IS NULL
CREATE TABLE dbo.LPMUserRole (
    Username  varchar(100) NOT NULL,
    RoleCode  varchar(20)  NOT NULL,
    CreateTS  datetime2(0) NOT NULL CONSTRAINT DF_LPMUserRole_CreateTS DEFAULT SYSDATETIME(),
    CONSTRAINT PK_LPMUserRole      PRIMARY KEY CLUSTERED (Username, RoleCode),
    CONSTRAINT FK_LPMUserRole_User FOREIGN KEY (Username) REFERENCES dbo.LPMUser(Username) ON DELETE CASCADE,
    CONSTRAINT FK_LPMUserRole_Role FOREIGN KEY (RoleCode) REFERENCES dbo.LPMRole(RoleCode) ON DELETE CASCADE
);
GO

IF OBJECT_ID('dbo.LPMAuditLog','U') IS NULL
CREATE TABLE dbo.LPMAuditLog (
    Id          bigint IDENTITY(1,1) NOT NULL,
    EntityName  varchar(100)   NOT NULL,
    EntityKey   varchar(200)   NOT NULL,
    Action      char(1)        NOT NULL,
    ChangedBy   varchar(100)   NOT NULL,
    ChangedTS   datetime2(0)   NOT NULL CONSTRAINT DF_LPMAuditLog_ChangedTS DEFAULT SYSDATETIME(),
    ChangesJson nvarchar(max)  NULL,
    CONSTRAINT PK_LPMAuditLog PRIMARY KEY CLUSTERED (Id)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LPMAuditLog_Entity' AND object_id=OBJECT_ID('dbo.LPMAuditLog'))
    CREATE INDEX IX_LPMAuditLog_Entity    ON dbo.LPMAuditLog(EntityName, EntityKey);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LPMAuditLog_ChangedTS' AND object_id=OBJECT_ID('dbo.LPMAuditLog'))
    CREATE INDEX IX_LPMAuditLog_ChangedTS ON dbo.LPMAuditLog(ChangedTS);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_LPMAuditLog_ChangedBy' AND object_id=OBJECT_ID('dbo.LPMAuditLog'))
    CREATE INDEX IX_LPMAuditLog_ChangedBy ON dbo.LPMAuditLog(ChangedBy);
GO

IF OBJECT_ID('dbo.LPM_EOM_Output','U') IS NULL
CREATE TABLE dbo.LPM_EOM_Output (
    StoreID         varchar(25)   NOT NULL,
    DivCode         int           NOT NULL,
    Month1          int           NOT NULL,
    Year1           int           NOT NULL,
    WtAvgSoldQty    decimal(18,4) NULL,
    WtAvgTurn       decimal(18,4) NULL,
    SoldQtyRank     int           NULL,
    TurnsRank       int           NULL,
    PriorityRank    decimal(9,1)  NULL,
    TargetTurn      decimal(18,4) NULL,
    TargetSales     decimal(18,2) NULL,
    TargetEOM       decimal(18,2) NULL,
    VolumeGroup     varchar(10)   NULL,
    WHStock         int           NULL,
    SKUMax          int           NULL,
    CreateTS        datetime2(0)  NOT NULL CONSTRAINT DF_LPM_EOM_Output_CreateTS DEFAULT SYSDATETIME(),
    Country         varchar(20)   NULL,
    CONSTRAINT PK_LPM_EOM_Output PRIMARY KEY CLUSTERED (StoreID, DivCode, Year1, Month1),
    CONSTRAINT CK_LPM_EOM_Output_Month CHECK (Month1 BETWEEN 1 AND 12),
    CONSTRAINT FK_LPM_EOM_Output_Division FOREIGN KEY (DivCode) REFERENCES dbo.Division(DivCode)
);
GO

IF OBJECT_ID('dbo.LPM_SalesTurns','U') IS NULL
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
GO

IF OBJECT_ID('dbo.LPM_MonthlyWeight','U') IS NULL
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
GO

IF OBJECT_ID('dbo.LPM_Planned','U') IS NULL
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
GO

IF OBJECT_ID('dbo.LPM_StoreGrade','U') IS NULL
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
GO

IF OBJECT_ID('dbo.LPM_VolumeGroup','U') IS NULL
CREATE TABLE dbo.LPM_VolumeGroup (
    GroupCode  varchar(20)   NOT NULL,
    GroupName  nvarchar(100) NOT NULL,
    SortOrder  int           NOT NULL,
    SharePct   decimal(6,4)  NOT NULL,
    IsActive   bit           NOT NULL CONSTRAINT DF_LPM_VolumeGroup_IsActive DEFAULT (1),
    CreateTS   datetime2(0)  NOT NULL CONSTRAINT DF_LPM_VolumeGroup_CreateTS DEFAULT SYSDATETIME(),
    CONSTRAINT PK_LPM_VolumeGroup PRIMARY KEY CLUSTERED (GroupCode)
);
GO

IF OBJECT_ID('dbo.LPM_WHStock','U') IS NULL
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
GO

IF OBJECT_ID('dbo.LPM_SKUMaxRule','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_SKUMaxRule (
        RuleId       int IDENTITY(1,1) NOT NULL,
        Country      varchar(20)  NOT NULL,
        DivCode      int          NOT NULL,
        GroupCode    varchar(20)  NOT NULL,
        WHStockFrom  int          NOT NULL,
        WHStockTo    int          NOT NULL,
        SKUMax       int          NOT NULL,
        IsActive     bit          NOT NULL CONSTRAINT DF_LPM_SKUMaxRule_IsActive DEFAULT (1),
        CreateTS     datetime2(0) NOT NULL CONSTRAINT DF_LPM_SKUMaxRule_CreateTS DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPM_SKUMaxRule PRIMARY KEY CLUSTERED (RuleId),
        CONSTRAINT FK_LPM_SKUMaxRule_Group    FOREIGN KEY (GroupCode) REFERENCES dbo.LPM_VolumeGroup(GroupCode),
        CONSTRAINT FK_LPM_SKUMaxRule_Division FOREIGN KEY (DivCode)   REFERENCES dbo.Division(DivCode),
        CONSTRAINT CK_LPM_SKUMaxRule_Range CHECK (WHStockTo >= WHStockFrom)
    );
    CREATE INDEX IX_LPM_SKUMaxRule_Lookup ON dbo.LPM_SKUMaxRule(Country, DivCode, GroupCode, WHStockFrom, WHStockTo);
END
GO

-- ============================================================
-- Seed master rows
-- ============================================================
MERGE dbo.LPMRole AS t
USING (VALUES
    ('Admin',           N'Administrator'),
    ('Editor',          N'Editor'),
    ('Viewer',          N'Viewer'),
    ('PlanningManager', N'Planning Manager')
) AS s(RoleCode, RoleName) ON t.RoleCode = s.RoleCode
WHEN NOT MATCHED BY TARGET THEN INSERT (RoleCode, RoleName) VALUES (s.RoleCode, s.RoleName);

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

PRINT 'Install complete.';
