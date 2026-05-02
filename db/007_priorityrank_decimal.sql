-- LPM SIM: PriorityRank can be 1.5 (avg of two ranks), must be decimal.
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'LPM_EOM_Output' AND COLUMN_NAME = 'PriorityRank'
      AND DATA_TYPE = 'int'
)
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ALTER COLUMN PriorityRank decimal(9,2) NULL;
    PRINT 'Altered LPM_EOM_Output.PriorityRank -> decimal(9,2)';
END
ELSE
    PRINT 'LPM_EOM_Output.PriorityRank already non-int';
