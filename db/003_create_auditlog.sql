-- LPM SIM: generic audit log populated by the EF SaveChangesInterceptor.
IF OBJECT_ID('dbo.LPMAuditLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMAuditLog (
        Id          bigint IDENTITY(1,1) NOT NULL,
        EntityName  varchar(100)   NOT NULL,
        EntityKey   varchar(200)   NOT NULL,
        Action      char(1)        NOT NULL,   -- 'I', 'U', 'D'
        ChangedBy   varchar(100)   NOT NULL,
        ChangedTS   datetime2(0)   NOT NULL CONSTRAINT DF_LPMAuditLog_ChangedTS DEFAULT SYSDATETIME(),
        ChangesJson nvarchar(max)  NULL,
        CONSTRAINT PK_LPMAuditLog PRIMARY KEY CLUSTERED (Id)
    );
    CREATE INDEX IX_LPMAuditLog_Entity    ON dbo.LPMAuditLog(EntityName, EntityKey);
    CREATE INDEX IX_LPMAuditLog_ChangedTS ON dbo.LPMAuditLog(ChangedTS);
    CREATE INDEX IX_LPMAuditLog_ChangedBy ON dbo.LPMAuditLog(ChangedBy);
    PRINT 'Created dbo.LPMAuditLog';
END
ELSE
    PRINT 'dbo.LPMAuditLog already exists';
