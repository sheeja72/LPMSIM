namespace LpmSim.Core.Entities;

/// <summary>
/// 1.14.26 — Per-eligible-box diagnostic row, populated at the end of every
/// successful SIM Generate for boxes where <c>RemainingQty &gt; 0</c>.
/// Backs the new "Allocation Gap" tab on the SIM Generate result preview.
/// See migration 046 for the column-by-column schema and the TopReason
/// taxonomy.
/// </summary>
public class LpmSimUnallocatedDiagnostic
{
    public long      LPMBatchNo   { get; set; }
    public string    BoxNo        { get; set; } = "";
    public string?   PalletNo     { get; set; }
    public DateTime? LPMDt        { get; set; }
    public string    BoxKind      { get; set; } = "";   // 'LPM' or 'Non-LPM'
    public long      BoxQty       { get; set; }
    public long      SimQty       { get; set; }
    public long      RemainingQty { get; set; }          // PERSISTED computed column
    public string    TopReason    { get; set; } = "";   // FILTERED_SEASON / SKIP_NO_DIV / SKIP_NO_EOM / CAP / UNKNOWN
    public string?   Reasons      { get; set; }
    public DateTime  CreateTS     { get; set; }
}
