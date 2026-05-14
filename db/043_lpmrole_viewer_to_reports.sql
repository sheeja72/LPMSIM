-- ============================================================================
-- Migration 043 — Rename Viewer role display label
--
-- Renames the user-facing label:
--   Viewer  ->  Reports
--
-- RoleCode stays 'Viewer' (the technical key used by [Authorize(Roles = ...)]
-- and IsInRole checks). Only the human-readable RoleName column flips, so
-- every existing authorization rule + every LPMUserRole assignment keeps
-- working untouched.
--
-- Affects the Admin → Users page (grid chip + Edit User dialog checkbox),
-- which both read RoleName from this table.
--
-- Idempotent: the WHERE clause uses RoleCode (stable identifier) and the
-- check on the current RoleName skips the UPDATE if it's already applied.
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPMRole', 'U') IS NOT NULL
BEGIN
    UPDATE dbo.LPMRole
       SET RoleName = 'Reports'
     WHERE RoleCode = 'Viewer'
       AND ISNULL(RoleName, '') <> 'Reports';
    PRINT CONCAT('Rows updated to RoleName=Reports: ', @@ROWCOUNT);
END
ELSE
    PRINT 'LPMRole table not found — skipping (run install/seed scripts first).';

GO

PRINT 'Migration 043 complete.';
