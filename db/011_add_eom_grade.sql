-- LPM SIM: persist the assigned Grade alongside each EOM Output row.
SET XACT_ABORT ON;
IF COL_LENGTH('dbo.LPM_EOM_Output', 'Grade') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD Grade varchar(20) NULL;
    PRINT 'Added LPM_EOM_Output.Grade';
END
ELSE
    PRINT 'LPM_EOM_Output.Grade already exists';
