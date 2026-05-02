-- LPM SIM: end-of-month analytical output per store × division × month.
-- Metrics are nullable so partial ETL loads don't reject rows.
IF OBJECT_ID('dbo.LPM_EOM_Output', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_EOM_Output (
        StoreID       varchar(25)   NOT NULL,
        DivCode       int           NOT NULL,
        Month1        int           NOT NULL,
        Year1         int           NOT NULL,
        WtAvgSoldQty  decimal(18,4) NULL,
        WtAvgTurn     decimal(18,4) NULL,
        SoldQtyRank   int           NULL,
        TurnsRank     int           NULL,
        PriorityRank  int           NULL,
        TargetTurn    decimal(18,4) NULL,
        TargetSales   decimal(18,2) NULL,
        TargetEOM     decimal(18,2) NULL,
        VolumeGroup   varchar(10)   NULL,
        WHStock       int           NULL,
        SKUMax        int           NULL,
        CreatedDt     datetime2(0)  NOT NULL CONSTRAINT DF_LPM_EOM_Output_CreatedDt DEFAULT SYSDATETIME(),
        CONSTRAINT PK_LPM_EOM_Output PRIMARY KEY CLUSTERED (StoreID, DivCode, Year1, Month1),
        CONSTRAINT CK_LPM_EOM_Output_Month CHECK (Month1 BETWEEN 1 AND 12),
        CONSTRAINT FK_LPM_EOM_Output_Division
            FOREIGN KEY (DivCode) REFERENCES dbo.Division(DivCode)
    );

    CREATE INDEX IX_LPM_EOM_Output_Period ON dbo.LPM_EOM_Output(Year1, Month1);
    CREATE INDEX IX_LPM_EOM_Output_Div    ON dbo.LPM_EOM_Output(DivCode);

    PRINT 'Created dbo.LPM_EOM_Output';
END
ELSE
    PRINT 'dbo.LPM_EOM_Output already exists';
