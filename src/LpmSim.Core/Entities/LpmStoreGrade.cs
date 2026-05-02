namespace LpmSim.Core.Entities;

public class LpmStoreGrade
{
    /// <summary>Territory the grade applies to. Country is part of the PK.</summary>
    public string Country { get; set; } = "";
    public string GradeCode { get; set; } = "";
    public string GradeName { get; set; } = "";
    public int SortOrder { get; set; }
    public decimal SharePct { get; set; }
    public decimal MarkupPct { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
}
