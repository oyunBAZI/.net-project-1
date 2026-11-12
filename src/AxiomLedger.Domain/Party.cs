namespace AxiomLedger.Domain;

/// <summary>A customer, vendor or both. Balances are derived from posted ledger activity.</summary>
public class Party : AuditableEntity
{
    public PartyType Type { get; set; } = PartyType.Customer;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? ContactName { get; set; }
    public string? TaxId { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public int PaymentTermsDays { get; set; } = 30;
    public decimal? CreditLimit { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    public bool IsCustomer => Type is PartyType.Customer or PartyType.Both;
    public bool IsVendor => Type is PartyType.Vendor or PartyType.Both;
}
