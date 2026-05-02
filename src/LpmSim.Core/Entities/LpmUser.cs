namespace LpmSim.Core.Entities;

public class LpmUser
{
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";

    public List<LpmUserRole> UserRoles { get; set; } = new();
}
