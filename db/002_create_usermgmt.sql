-- LPM SIM: user management tables + seed roles + bootstrap Admin.
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.LPMUser', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMUser (
        Username     varchar(100)  NOT NULL,
        DisplayName  nvarchar(200) NULL,
        Email        varchar(200)  NULL,
        IsActive     bit           NOT NULL CONSTRAINT DF_LPMUser_IsActive  DEFAULT (1),
        CreatedTS    datetime2(0)  NOT NULL CONSTRAINT DF_LPMUser_CreatedTS DEFAULT SYSDATETIME(),
        CreatedBy    varchar(100)  NOT NULL,
        CONSTRAINT PK_LPMUser PRIMARY KEY CLUSTERED (Username)
    );
    PRINT 'Created dbo.LPMUser';
END;

IF OBJECT_ID('dbo.LPMRole', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMRole (
        RoleCode varchar(20)   NOT NULL,
        RoleName nvarchar(100) NOT NULL,
        CONSTRAINT PK_LPMRole PRIMARY KEY CLUSTERED (RoleCode)
    );
    PRINT 'Created dbo.LPMRole';
END;

IF OBJECT_ID('dbo.LPMUserRole', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMUserRole (
        Username varchar(100) NOT NULL,
        RoleCode varchar(20)  NOT NULL,
        CONSTRAINT PK_LPMUserRole PRIMARY KEY CLUSTERED (Username, RoleCode),
        CONSTRAINT FK_LPMUserRole_User FOREIGN KEY (Username) REFERENCES dbo.LPMUser(Username) ON DELETE CASCADE,
        CONSTRAINT FK_LPMUserRole_Role FOREIGN KEY (RoleCode) REFERENCES dbo.LPMRole(RoleCode) ON DELETE CASCADE
    );
    PRINT 'Created dbo.LPMUserRole';
END;

MERGE dbo.LPMRole AS t
USING (VALUES
    ('Admin',  N'Administrator'),
    ('Editor', N'Editor'),
    ('Viewer', N'Viewer')
) AS s(RoleCode, RoleName) ON t.RoleCode = s.RoleCode
WHEN NOT MATCHED BY TARGET THEN INSERT (RoleCode, RoleName) VALUES (s.RoleCode, s.RoleName);

-- Bootstrap Admin (idempotent)
IF NOT EXISTS (SELECT 1 FROM dbo.LPMUser WHERE Username = 'BFLDomain\sheeja')
    INSERT INTO dbo.LPMUser (Username, DisplayName, IsActive, CreatedBy)
        VALUES ('BFLDomain\sheeja', N'Sheeja (bootstrap admin)', 1, 'system');

IF NOT EXISTS (SELECT 1 FROM dbo.LPMUserRole WHERE Username = 'BFLDomain\sheeja' AND RoleCode = 'Admin')
    INSERT INTO dbo.LPMUserRole (Username, RoleCode) VALUES ('BFLDomain\sheeja', 'Admin');

PRINT 'User management setup complete.';
