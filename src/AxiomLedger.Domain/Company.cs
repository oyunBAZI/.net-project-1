namespace AxiomLedger.Domain;

public class Company : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? TaxId { get; set; }
    public string BaseCurrencyCode { get; set; } = "USD";
    public string CountryCode { get; set; } = "US";
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int FiscalYearStartMonth { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsArchived { get; set; }
    public CompanySettings Settings { get; set; } = new();
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
}

public class CompanySettings : Entity
{
    public Guid CompanyId { get; set; }
    /// <summary>When enabled, journals must be approved by a second user before posting.</summary>
    public bool RequireJournalApproval { get; set; }
    /// <summary>Journal totals at or above this amount require approval when approval is enabled.</summary>
    public decimal ApprovalThreshold { get; set; }
    public bool RequireSegregationOfDuties { get; set; } = true;
    public int DefaultInvoiceDueDays { get; set; } = 30;
    /// <summary>Hard lock: no entry may be dated on or before this date. Null when unlocked.</summary>
    public DateOnly? LockDate { get; set; }
    public Guid? DefaultSalesTaxCodeId { get; set; }
    public Guid? DefaultPurchaseTaxCodeId { get; set; }
    public Guid? RetainedEarningsAccountId { get; set; }
    public Guid? AccountsReceivableAccountId { get; set; }
    public Guid? AccountsPayableAccountId { get; set; }
    public string InvoicePrefix { get; set; } = "INV";
    public string BillPrefix { get; set; } = "BILL";
    public string JournalPrefix { get; set; } = "JE";
    public string PaymentPrefix { get; set; } = "PMT";
    public decimal LateFeePercent { get; set; }
}

public class Currency
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public int DecimalPlaces { get; set; } = 2;
    public bool IsActive { get; set; } = true;
}

public class ExchangeRate : CompanyEntity
{
    public string CurrencyCode { get; set; } = string.Empty;
    public DateOnly RateDate { get; set; }
    /// <summary>Units of base currency per one unit of <see cref="CurrencyCode"/>.</summary>
    public decimal Rate { get; set; } = 1m;
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TaxCode : CompanyEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public TaxKind Kind { get; set; } = TaxKind.Sales;
    public Guid TaxAccountId { get; set; }
    /// <summary>Input-tax account used when the code is applied to a purchase. Falls back to <see cref="TaxAccountId"/>.</summary>
    public Guid? InputTaxAccountId { get; set; }
    public bool IsInclusive { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ReportGroup { get; set; }
}

public class Account : CompanyEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; }
    public Guid? ParentId { get; set; }
    public string? Description { get; set; }
    public bool IsPostable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
    public bool IsContra { get; set; }
    public bool IsCashAccount { get; set; }
    public AccountGroup Group { get; set; } = AccountGroup.None;
    public CashFlowCategory CashFlowCategory { get; set; } = CashFlowCategory.Operating;
    public string? CurrencyCode { get; set; }
    public Guid? DefaultTaxCodeId { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public NormalBalance NormalBalance =>
        (Type == AccountType.Asset || Type == AccountType.Expense) ^ IsContra
            ? NormalBalance.Debit
            : NormalBalance.Credit;
}

public class FiscalYear : CompanyEntity
{
    public int Year { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public FiscalYearStatus Status { get; set; } = FiscalYearStatus.Open;
    public DateTime? ClosedAt { get; set; }
    public string? ClosedByUserId { get; set; }
}

public class AccountingPeriod : CompanyEntity
{
    public Guid FiscalYearId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public PeriodStatus Status { get; set; } = PeriodStatus.Open;
    public DateTime? ClosedAt { get; set; }
    public string? ClosedByUserId { get; set; }

    public bool Includes(DateOnly date) => date >= StartDate && date <= EndDate;
}

/// <summary>Analytical dimension for departmental / cost-centre reporting.</summary>
public class Department : CompanyEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class DocumentSequence : CompanyEntity
{
    public string Key { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public int NextNumber { get; set; } = 1;
    public int Padding { get; set; } = 5;

    public string Format(int number) => $"{Prefix}-{number.ToString().PadLeft(Padding, '0')}";
}
