-- ============================================================================
-- Migration 042 — Rename role display labels (RoleName)
--
-- Renames the user-facing role labels:
--   Editor          -> EOM/SIM
--   PlanningManager -> Planning Config
--
-- RoleCode (the technical key used by [Authorize(Roles = ...)] checks) is
-- UNCHANGED. Only the human-readable RoleName column flips. This keeps every
-- authorization rule + every existing LPMUserRole assignment working without
-- any further migration.
--
-- Idempotent: the WHERE clause uses RoleCode (stable identifier).
-- ============================================================================
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.LPMRole', 'U') IS NOT NULL
BEGIN
    UPDATE dbo.LPMRole
       SET RoleName = 'EOM/SIM'
     WHERE RoleCode = 'Editor'
       AND ISNULL(RoleName, '') <> 'EOM/SIM';
    PRINT CONCAT('Rows updated to RoleName=EOM/SIM: ', @@ROWCOUNT);

    UPDATE dbo.LPMRole
       SET RoleName = 'Planning Config'
     WHERE RoleCode = 'PlanningManager'
       AND ISNULL(RoleName, '') <> 'Planning Config';
    PRINT CONCAT('Rows updated to RoleName=Planning Config: ', @@ROWCOUNT);
END
ELSE
    PRINT 'LPMRole table not found — skipping (run install/seed scripts first).';

GO

PRINT 'Migration 042 complete.';
