-- ============================================================================
-- Migration 023 — Email-based login support for SSO (Microsoft Entra ID)
--
-- LPMUser.Email already exists. This migration:
--   1. Widens it if necessary (varchar(200)) so corporate emails always fit.
--   2. Adds a filtered UNIQUE index so the OIDC sign-in flow can look up a
--      user by Email cheaply and with no duplicates.
--   3. Optional seed: when Username already looks like an email
--      (contains '@'), copy it into Email if Email is NULL.
--
-- Safe to run multiple times. Does NOT drop the existing Username PK — both
-- the legacy Negotiate flow (Auth:Mode = Negotiate) and the new OIDC flow
-- (Auth:Mode = OIDC) coexist behind a feature flag in Program.cs.
-- ============================================================================
SET XACT_ABORT ON;
GO

-- 1. Make sure Email exists and is wide enough.
IF COL_LENGTH('dbo.LPMUser', 'Email') IS NULL
BEGIN
    ALTER TABLE dbo.LPMUser ADD Email varchar(200) NULL;
    PRINT 'LPMUser: added Email';
END
ELSE
BEGIN
    DECLARE @maxLen int =
        (SELECT max_length FROM sys.columns
          WHERE object_id = OBJECT_ID('dbo.LPMUser') AND name = 'Email');
    IF @maxLen < 200
    BEGIN
        ALTER TABLE dbo.LPMUser ALTER COLUMN Email varchar(200) NULL;
        PRINT 'LPMUser: widened Email to varchar(200)';
    END
END
GO

-- 2. Filtered unique index — only the rows that actually have an email.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE name = 'UX_LPMUser_Email' AND object_id = OBJECT_ID('dbo.LPMUser'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_LPMUser_Email
        ON dbo.LPMUser(Email)
        WHERE Email IS NOT NULL;
    PRINT 'LPMUser: added UX_LPMUser_Email';
END
ELSE
    PRINT 'UX_LPMUser_Email already exists';
GO

-- 3. Best-effort seed: copy Username into Email if Username is already an
--    email-looking string and Email is NULL. Safe — only fills in blanks.
UPDATE dbo.LPMUser
   SET Email = Username
 WHERE Email IS NULL
   AND Username LIKE '%@%';
PRINT CONCAT('LPMUser: seeded Email from Username for ', @@ROWCOUNT, ' row(s).');
GO

PRINT 'Migration 023 complete.';
PRINT 'Reminder: every active user MUST have a non-NULL Email value before flipping Auth:Mode = OIDC.';
