-- LPM SIM: create LPMDivMax table
-- One row per (StoreID, DivCode); stamps user + timestamps on upsert.
IF OBJECT_ID('dbo.LPMDivMax', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMDivMax (
        StoreID    varchar(25)  NOT NULL,
        DivCode    int          NOT NULL,
        MaxQty     int          NOT NULL,
        CreateTS   datetime2(0) NOT NULL CONSTRAINT DF_LPMDivMax_CreateTS  DEFAULT SYSDATETIME(),
        UpdatedTS  datetime2(0) NOT NULL CONSTRAINT DF_LPMDivMax_UpdatedTS DEFAULT SYSDATETIME(),
        UserID     varchar(100) NOT NULL,
        CONSTRAINT PK_LPMDivMax       PRIMARY KEY CLUSTERED (StoreID, DivCode),
        CONSTRAINT FK_LPMDivMax_Division FOREIGN KEY (DivCode) REFERENCES dbo.Division(DivCode)
    );
    CREATE INDEX IX_LPMDivMax_DivCode ON dbo.LPMDivMax(DivCode);
    PRINT 'Created dbo.LPMDivMax';
END
ELSE
    PRINT 'dbo.LPMDivMax already exists';
