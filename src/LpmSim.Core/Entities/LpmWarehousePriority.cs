namespace LpmSim.Core.Entities;

/// <summary>
/// Per-(Country, Warehouse) priority used by the SIM allocator's box read-order
/// in <c>ReadBoxesAsync</c>. Lower <see cref="Priority"/> = higher priority
/// (1 = first). Warehouses NOT listed in this table fall back to priority
/// 9999 (after every prioritised one), so the table only needs rows for the
/// warehouses you actually want to rank — the rest stay in the default
/// "BoxQty DESC, BoxNo" order.
///
/// Replaces the per-Generate UI chip-list. Maintained via the
/// <c>/admin/warehouse-priorities</c> page (or directly in SSMS).
/// </summary>
public class LpmWarehousePriority
{
    public string Country { get; set; } = "";
    public string Warehouse { get; set; } = "";
    /// <summary>1 = highest priority. Smaller number = processed first.</summary>
    public int Priority { get; set; }
    /// <summary>
    /// When false, the LEFT JOIN in ReadBoxesAsync no longer matches this row
    /// — the warehouse falls back to priority 9999 (sorts last). Lets you
    /// temporarily exclude a warehouse from the priority order without
    /// deleting the row.
    /// </summary>
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedTS { get; set; }
    public string? UpdatedBy { get; set; }
}
