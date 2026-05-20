-- ============================================================================
-- Migration 056 — LPMRole: add "EOM Generate/Approve Access" and
--                          "SIM Generate/Approve Access" roles
--
-- 1.14.66 — Two new finer-grained roles so an admin can grant Generate /
-- Approve access on EOM Generate (and/or SIM Generate) WITHOUT giving full
-- Admin. Before this, the Generate and Generate & Approve buttons on EOM
-- Generate and the Generate / Approve / Delete buttons on SIM Generate
-- were gated by [Admin] only (1.14.57).
--
-- After this migration, the AuthorizeView around those buttons accepts
-- either "Admin" OR the new role, so a user with only "EomGenerateApprove"
-- can trigger EOM Generate runs but can't touch admin-only surfaces.
--
-- Idempotent: MERGE with WHEN NOT MATCHED INSERT only.
-- ============================================================================
SET XACT_ABORT ON;
GO

MERGE dbo.LPMRole AS t
USING (VALUES
    ('EomGenerateApprove', N'EOM Generate/Approve Access'),
    ('SimGenerateApprove', N'SIM Generate/Approve Access')
) AS s(RoleCode, RoleName) ON t.RoleCode = s.RoleCode
WHEN NOT MATCHED BY TARGET THEN
    INSERT (RoleCode, RoleName) VALUES (s.RoleCode, s.RoleName)
WHEN MATCHED AND t.RoleName <> s.RoleName THEN
    -- Keep the display name in sync if a later release renames it.
    UPDATE SET RoleName = s.RoleName;

PRINT 'Migration 056 complete — EomGenerateApprove + SimGenerateApprove roles available.';
