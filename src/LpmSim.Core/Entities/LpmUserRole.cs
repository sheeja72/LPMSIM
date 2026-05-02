namespace LpmSim.Core.Entities;

public class LpmUserRole
{
    public string Username { get; set; } = "";
    public string RoleCode { get; set; } = "";
    public DateTime CreateTS { get; set; }

    public LpmUser User { get; set; } = null!;
    public LpmRole Role { get; set; } = null!;
}
