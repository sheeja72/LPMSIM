namespace LpmSim.Web.Components.Pages.Admin;

public class UserEditModel
{
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public HashSet<string> SelectedRoles { get; set; } = new();
}
