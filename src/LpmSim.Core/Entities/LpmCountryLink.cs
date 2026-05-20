namespace LpmSim.Core.Entities;

/// <summary>
/// 1.14.77 — Parent-child country linkage. When a country (<see cref="ChildCountry"/>)
/// is shipped from another country's warehouse (<see cref="ParentCountry"/>):
///
/// <list type="bullet">
///   <item>EOM Generate for the Child uses the Parent's whboxitems source for
///         WH Stock (so OMAN EOM reads UAE warehouse, since OMAN has no WH of
///         its own).</item>
///   <item>SIM Generate for the Parent includes the Child's stores in the
///         same allocation run (UAE SIM allocates to both UAE and OMAN
///         stores).</item>
///   <item>Priority within each allocator phase: Parent's stores first, then
///         Child's stores (alphabetically when multiple children).</item>
/// </list>
///
/// Composite PK is <c>(ParentCountry, ChildCountry)</c>; a unique constraint
/// on <c>ChildCountry</c> alone enforces "one parent per child" so the
/// resolver never has to pick between conflicting parents.
/// </summary>
public class LpmCountryLink
{
    public string  ParentCountry { get; set; } = "";
    public string  ChildCountry  { get; set; } = "";
    public bool    IsActive      { get; set; } = true;
    public DateTime CreateTS     { get; set; }
    public string?  CreatedBy    { get; set; }
}
