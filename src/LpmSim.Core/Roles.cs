namespace LpmSim.Core;

public static class Roles
{
    public const string Admin           = "Admin";
    public const string Editor          = "Editor";
    public const string Viewer          = "Viewer";
    public const string PlanningManager = "PlanningManager";

    public const string AdminOrEditor          = "Admin,Editor";
    public const string AdminOrPlanner         = "Admin,PlanningManager";
    public const string AdminOrEditorOrPlanner = "Admin,Editor,PlanningManager";
    public const string AnyRole                = "Admin,Editor,Viewer,PlanningManager";
}

public static class AuthPolicies
{
    // Authenticated Windows user AND an active row in LPMUser.
    public const string RequireLpmUser = "RequireLpmUser";
}
