namespace LpmSim.Core.Entities;

// Read-only projection of dbo.DataSettings — we only map the columns LPM SIM uses.
// Declared keyless so EF does not try to track or update it.
public class DataSetting
{
    // DataSettings columns are nullable in the real DB; materialize safely and let callers filter.
    public string? StoreID { get; set; }
    public string? PBFullname { get; set; }

    /// <summary>The store's primary / business country (used by EOM Generate to
    /// scope active stores).</summary>
    public string? Country { get; set; }

    /// <summary>The country the store participates in for SIM allocation.
    /// May differ from <see cref="Country"/> — e.g. a store physically in
    /// Country A but receiving stock under the Country B SIM plan. SIM
    /// Generate (and every SIM report) filters store eligibility by this
    /// column, not by <see cref="Country"/>.</summary>
    public string? SIMCountry { get; set; }

    public string? ActiveStore { get; set; }
}
