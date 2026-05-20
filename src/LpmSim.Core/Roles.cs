namespace LpmSim.Core;

public static class Roles
{
    public const string Admin           = "Admin";
    public const string Editor          = "Editor";
    public const string Viewer          = "Viewer";
    public const string PlanningManager = "PlanningManager";

    // 1.14.66 — Two new finer-grained roles that gate just the Generate /
    // Approve button groups on the EOM Generate and SIM Generate pages
    // (everything else stays page-level [Authorize(...)]). Lets an admin
    // grant Generate/Approve rights WITHOUT giving full Admin. Provisioned
    // via migration 056; visible as checkboxes in the User Access dialog.
    public const string EomGenerateApprove = "EomGenerateApprove";
    public const string SimGenerateApprove = "SimGenerateApprove";

    public const string AdminOrEditor          = "Admin,Editor";
    public const string AdminOrPlanner         = "Admin,PlanningManager";
    public const string AdminOrEditorOrPlanner = "Admin,Editor,PlanningManager";
    public const string AnyRole                = "Admin,Editor,Viewer,PlanningManager,EomGenerateApprove,SimGenerateApprove";

    // 1.14.66 — Aggregates used by the AuthorizeView around the per-page
    // Generate / Approve / Delete button groups.
    public const string AdminOrEomGen          = "Admin,EomGenerateApprove";
    public const string AdminOrSimGen          = "Admin,SimGenerateApprove";

    // 1.14.66 — Page-level access aggregates that include the new fine-grained
    // roles so a user with ONLY "EomGenerateApprove" (or "SimGenerateApprove")
    // can reach the page where their permission applies. These extend the
    // existing AdminOrEditor / AdminOrEditorOrPlanner gates for EomGenerate
    // and LpmSimGenerate specifically — other pages keep the narrower
    // aggregates so the new role doesn't accidentally grant access to
    // Monthly Weights / Planned Inputs / Uploads / etc.
    public const string EomGeneratePageAccess  = "Admin,Editor,EomGenerateApprove";
    public const string SimGeneratePageAccess  = "Admin,Editor,PlanningManager,SimGenerateApprove";
}

public static class AuthPolicies
{
    // Authenticated Windows user AND an active row in LPMUser.
    public const string RequireLpmUser = "RequireLpmUser";
}
