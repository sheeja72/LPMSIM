-- LPM SIM: standardize on CreateTS across existing tables.
SET XACT_ABORT ON;

-- LPMUser: rename CreatedTS -> CreateTS
IF COL_LENGTH('dbo.LPMUser', 'CreatedTS') IS NOT NULL
   AND COL_LENGTH('dbo.LPMUser', 'CreateTS') IS NULL
BEGIN
    EXEC sp_rename 'dbo.LPMUser.CreatedTS', 'CreateTS', 'COLUMN';
    IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_LPMUser_CreatedTS')
        EXEC sp_rename 'dbo.DF_LPMUser_CreatedTS', 'DF_LPMUser_CreateTS', 'OBJECT';
    PRINT 'Renamed LPMUser.CreatedTS -> CreateTS';
END

-- LPM_EOM_Output: rename CreatedDt -> CreateTS
IF COL_LENGTH('dbo.LPM_EOM_Output', 'CreatedDt') IS NOT NULL
   AND COL_LENGTH('dbo.LPM_EOM_Output', 'CreateTS') IS NULL
BEGIN
    EXEC sp_rename 'dbo.LPM_EOM_Output.CreatedDt', 'CreateTS', 'COLUMN';
    IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_LPM_EOM_Output_CreatedDt')
        EXEC sp_rename 'dbo.DF_LPM_EOM_Output_CreatedDt', 'DF_LPM_EOM_Output_CreateTS', 'OBJECT';
    PRINT 'Renamed LPM_EOM_Output.CreatedDt -> CreateTS';
END

-- LPMRole: add CreateTS
IF COL_LENGTH('dbo.LPMRole', 'CreateTS') IS NULL
BEGIN
    ALTER TABLE dbo.LPMRole
        ADD CreateTS datetime2(0) NOT NULL CONSTRAINT DF_LPMRole_CreateTS DEFAULT SYSDATETIME();
    PRINT 'Added LPMRole.CreateTS';
END

-- LPMUserRole: add CreateTS
IF COL_LENGTH('dbo.LPMUserRole', 'CreateTS') IS NULL
BEGIN
    ALTER TABLE dbo.LPMUserRole
        ADD CreateTS datetime2(0) NOT NULL CONSTRAINT DF_LPMUserRole_CreateTS DEFAULT SYSDATETIME();
    PRINT 'Added LPMUserRole.CreateTS';
END

-- LPM_EOM_Output: add Country (EOM runs are per country)
IF COL_LENGTH('dbo.LPM_EOM_Output', 'Country') IS NULL
BEGIN
    ALTER TABLE dbo.LPM_EOM_Output ADD Country varchar(20) NULL;
    PRINT 'Added LPM_EOM_Output.Country';
END

PRINT 'CreateTS standardization complete.';
