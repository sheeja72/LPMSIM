-- LPM SIM: add primary key to dbo.Division so LPMDivMax can FK to it.
-- Division has 21 rows, 21 distinct DivCodes, 0 nulls — safe to alter.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Division')
      AND name = 'DivCode' AND is_nullable = 1
)
    ALTER TABLE dbo.Division ALTER COLUMN DivCode int NOT NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE parent_object_id = OBJECT_ID('dbo.Division') AND type = 'PK'
)
    ALTER TABLE dbo.Division ADD CONSTRAINT PK_Division PRIMARY KEY CLUSTERED (DivCode);
GO

PRINT 'dbo.Division PK ensured.';
