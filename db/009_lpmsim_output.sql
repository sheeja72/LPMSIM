-- LPM SIM: allocation output tables.
--   LPMSIM_Batch  : one row per (Country, RunDate). Status = Draft / Approved.
--   LPMSIM_Output : one row per allocation (Box × Item × Store).
--   *_Backup      : archived copies after a Delete; retained for audit / rerun.
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.LPMSIM_Batch','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_Batch (
        LPMBatchNo      bigint IDENTITY(1,1) NOT NULL,
        Country         varchar(20)   NOT NULL,
        RunYear         int           NOT NULL,
        RunMonth        int           NOT NULL,
        RunDate         date          NOT NULL,
        Status          varchar(20)   NOT NULL CONSTRAINT DF_LPMSIM_Batch_Status   DEFAULT ('Draft'),
        BoxesProcessed  int           NOT NULL CONSTRAINT DF_LPMSIM_Batch_Boxes    DEFAULT (0),
        LinesGenerated  int           NOT NULL CONSTRAINT DF_LPMSIM_Batch_Lines    DEFAULT (0),
        TotalQty        bigint        NOT NULL CONSTRAINT DF_LPMSIM_Batch_Total    DEFAULT (0),
        CreateTS        datetime2(0)  NOT NULL CONSTRAINT DF_LPMSIM_Batch_CreateTS DEFAULT SYSDATETIME(),
        CreatedBy       varchar(100)  NOT NULL,
        ApprovedTS      datetime2(0)  NULL,
        ApprovedBy      varchar(100)  NULL,
        CONSTRAINT PK_LPMSIM_Batch PRIMARY KEY CLUSTERED (LPMBatchNo),
        CONSTRAINT UQ_LPMSIM_Batch_CountryRunDate UNIQUE (Country, RunDate),
        CONSTRAINT CK_LPMSIM_Batch_Status CHECK (Status IN ('Draft','Approved')),
        CONSTRAINT CK_LPMSIM_Batch_RunMonth CHECK (RunMonth BETWEEN 1 AND 12)
    );
    PRINT 'Created dbo.LPMSIM_Batch';
END
GO

IF OBJECT_ID('dbo.LPMSIM_Output','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_Output (
        Id          bigint IDENTITY(1,1) NOT NULL,
        LPMBatchNo  bigint        NOT NULL,
        BoxNo       varchar(25)   NOT NULL,
        LPMDt       date          NULL,
        Itemcode    varchar(30)   NOT NULL,
        Qty         int           NOT NULL,
        StoreID     varchar(25)   NOT NULL,
        CreateTS    datetime2(0)  NOT NULL CONSTRAINT DF_LPMSIM_Output_CreateTS DEFAULT SYSDATETIME(),
        CreatedBy   varchar(100)  NOT NULL,
        CONSTRAINT PK_LPMSIM_Output PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_LPMSIM_Output_Batch FOREIGN KEY (LPMBatchNo)
            REFERENCES dbo.LPMSIM_Batch(LPMBatchNo) ON DELETE CASCADE
    );
    CREATE INDEX IX_LPMSIM_Output_Batch     ON dbo.LPMSIM_Output(LPMBatchNo);
    CREATE INDEX IX_LPMSIM_Output_Store     ON dbo.LPMSIM_Output(StoreID, Itemcode);
    CREATE INDEX IX_LPMSIM_Output_Box       ON dbo.LPMSIM_Output(BoxNo);
    PRINT 'Created dbo.LPMSIM_Output';
END
GO

-- Backups: same shape, plus BackupTS / BackupBy. Identity dropped — we keep the original IDs.
IF OBJECT_ID('dbo.LPMSIM_Batch_Backup','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_Batch_Backup (
        LPMBatchNo      bigint        NOT NULL,
        Country         varchar(20)   NOT NULL,
        RunYear         int           NOT NULL,
        RunMonth        int           NOT NULL,
        RunDate         date          NOT NULL,
        Status          varchar(20)   NOT NULL,
        BoxesProcessed  int           NOT NULL,
        LinesGenerated  int           NOT NULL,
        TotalQty        bigint        NOT NULL,
        CreateTS        datetime2(0)  NOT NULL,
        CreatedBy       varchar(100)  NOT NULL,
        ApprovedTS      datetime2(0)  NULL,
        ApprovedBy      varchar(100)  NULL,
        BackupTS        datetime2(0)  NOT NULL CONSTRAINT DF_LPMSIM_Batch_Backup_BackupTS DEFAULT SYSDATETIME(),
        BackupBy        varchar(100)  NOT NULL,
        CONSTRAINT PK_LPMSIM_Batch_Backup PRIMARY KEY CLUSTERED (LPMBatchNo, BackupTS)
    );
    PRINT 'Created dbo.LPMSIM_Batch_Backup';
END
GO

IF OBJECT_ID('dbo.LPMSIM_Output_Backup','U') IS NULL
BEGIN
    CREATE TABLE dbo.LPMSIM_Output_Backup (
        Id          bigint        NOT NULL,
        LPMBatchNo  bigint        NOT NULL,
        BoxNo       varchar(25)   NOT NULL,
        LPMDt       date          NULL,
        Itemcode    varchar(30)   NOT NULL,
        Qty         int           NOT NULL,
        StoreID     varchar(25)   NOT NULL,
        CreateTS    datetime2(0)  NOT NULL,
        CreatedBy   varchar(100)  NOT NULL,
        BackupTS    datetime2(0)  NOT NULL CONSTRAINT DF_LPMSIM_Output_Backup_BackupTS DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPMSIM_Output_Backup PRIMARY KEY CLUSTERED (Id, BackupTS)
    );
    CREATE INDEX IX_LPMSIM_Output_Backup_Batch ON dbo.LPMSIM_Output_Backup(LPMBatchNo);
    PRINT 'Created dbo.LPMSIM_Output_Backup';
END
GO

PRINT 'LPM SIM allocation tables ready.';
