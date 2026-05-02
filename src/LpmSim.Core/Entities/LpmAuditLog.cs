namespace LpmSim.Core.Entities;

public class LpmAuditLog
{
    public long Id { get; set; }
    public string EntityName { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public char Action { get; set; } // 'I','U','D'
    public string ChangedBy { get; set; } = "";
    public DateTime ChangedTS { get; set; }
    public string? ChangesJson { get; set; }
}
