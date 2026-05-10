namespace LpmSim.Core.Entities;

/// <summary>
/// Per-(Country, Store, Division, Department) activation flag + future EOM
/// scaling percent. Finer-grained version of <see cref="LpmStoreDivAccess"/>
/// which works at Division level.
///
/// <para>
/// When <see cref="IsActive"/> is <c>false</c>, the SKU Max build's Rule 7
/// zeroes <c>LPM_SimItemSkuMax.SKUMax</c> for every item under that
/// <c>(Store × Div × Dept)</c>. The override writes an audit row to
/// <c>LPM_SimItemSkuMaxExcluded</c> with
/// <c>SourceTable = 'dbo.LPM_StoreDeptAccess'</c> so admins can see which
/// items got zeroed and why.
/// </para>
///
/// <para>
/// <see cref="DeptPct"/> is RESERVED for a future EOM / SIM Generate rule.
/// The SKU Max build currently ignores it — the column exists on day one
/// so the admin page can collect values without a follow-up migration.
/// Default 100 (no scaling).
/// </para>
///
/// <para>
/// Default behaviour (no row in this table) is ACTIVE for every (Store,
/// Division, Department). Only explicit deactivations need rows.
/// </para>
/// </summary>
public class LpmStoreDeptAccess
{
    public int Id { get; set; }
    public string Country { get; set; } = "";
    public string StoreID { get; set; } = "";
    public int DivCode { get; set; }
    /// <summary>Department NAME — must match <c>Datareporting.dbo.subclassmaster.Department</c>.</summary>
    public string Department { get; set; } = "";
    /// <summary>0–100; 100 = no scaling. Reserved for future EOM use; the SKU Max build ignores it.</summary>
    public decimal DeptPct { get; set; } = 100m;
    public bool IsActive { get; set; } = true;
    public string? Remarks { get; set; }
    public DateTime CreateTS { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedTS { get; set; }
    public string? UpdatedBy { get; set; }
}
