using AxiomLedger.Domain;

namespace AxiomLedger.Application.Models;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;
}

public sealed record AccountOption(Guid Id, string Code, string Name, AccountType Type, AccountGroup Group)
{
    public string Display => $"{Code} \u2013 {Name}";
}

public sealed record PartyOption(Guid Id, string Name, PartyType Type);

public sealed record CompanyOption(Guid Id, string Name, UserRole Role, string CurrencyCode);

public sealed record MembershipView(Guid MembershipId, Guid UserId, string Email, string DisplayName,
    UserRole Role, bool IsActive, DateTime CreatedAt, DateTime? LastLoginAt);

public sealed record UserListItem(Guid Id, string Email, string DisplayName, bool IsActive,
    bool IsPlatformAdmin, DateTime CreatedAt, DateTime? LastLoginAt, int CompanyCount);

public sealed class NewCompanyRequest
{
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string? TaxId { get; set; }
    public string? RegistrationNumber { get; set; }
    public string BaseCurrencyCode { get; set; } = "USD";
    public string CountryCode { get; set; } = "US";
    public int FiscalYearStartMonth { get; set; } = 1;
    public string? AddressLine1 { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string Industry { get; set; } = "general";
}

public sealed class CompanyEditRequest
{
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string? TaxId { get; set; }
    public string? RegistrationNumber { get; set; }
    public string CountryCode { get; set; } = "US";
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

public sealed class CompanySettingsRequest
{
    public bool RequireJournalApproval { get; set; }
    public decimal ApprovalThreshold { get; set; }
    public bool RequireSegregationOfDuties { get; set; } = true;
    public int DefaultInvoiceDueDays { get; set; } = 30;
    public DateOnly? LockDate { get; set; }
    public Guid? DefaultSalesTaxCodeId { get; set; }
    public Guid? DefaultPurchaseTaxCodeId { get; set; }
    public string InvoicePrefix { get; set; } = "INV";
    public string BillPrefix { get; set; } = "BILL";
    public string JournalPrefix { get; set; } = "JE";
    public string PaymentPrefix { get; set; } = "PMT";
    public decimal LateFeePercent { get; set; }
}

public sealed record PartyListItem(Guid Id, string Code, string Name, PartyType Type, string? Email,
    string CurrencyCode, bool IsActive, decimal Receivable, decimal Payable);

public sealed class PartyRequest
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
}

public sealed class JournalFilter
{
    public string? Search { get; set; }
    public JournalStatus? Status { get; set; }
    public JournalSource? Source { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public Guid? AccountId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed record JournalListItem(Guid Id, string Number, DateOnly Date, string Memo, string? Reference,
    JournalSource Source, JournalStatus Status, decimal TotalDebit, string? CreatedByEmail,
    string? PostedByEmail, DateTime? PostedAt);

public sealed record JournalLineInput(Guid AccountId, string? Description, decimal Debit, decimal Credit,
    Guid? DepartmentId = null, Guid? PartyId = null, Guid? TaxCodeId = null);

public sealed class JournalDraftRequest
{
    public DateOnly EntryDate { get; set; }
    public string Memo { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public List<JournalLineInput> Lines { get; set; } = new();
    public string? CurrencyCode { get; set; }
    public decimal ExchangeRate { get; set; } = 1m;
}

public sealed class InvoiceFilter
{
    public string? Search { get; set; }
    public DocumentStatus? Status { get; set; }
    public Guid? PartyId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public bool? Overdue { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed record InvoiceListItem(Guid Id, string Number, InvoiceKind Kind, Guid PartyId, string PartyName,
    DateOnly IssueDate, DateOnly DueDate, string CurrencyCode, decimal Total, decimal Balance,
    DocumentStatus Status);

public sealed class InvoiceLineInput
{
    public string Description { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public Guid? TaxCodeId { get; set; }
    /// <summary>Informational snapshot of the selected tax code's rate; the code is authoritative.</summary>
    public decimal TaxRate { get; set; }
    public Guid? DepartmentId { get; set; }
}

public sealed class InvoiceEditModel
{
    public Guid PartyId { get; set; }
    public InvoiceKind Kind { get; set; } = InvoiceKind.Invoice;
    public DateOnly IssueDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly DueDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
    public string CurrencyCode { get; set; } = "USD";
    /// <summary>Zero means "use the latest rate on or before the issue date".</summary>
    public decimal ExchangeRate { get; set; }
    public bool TaxInclusive { get; set; }
    public string? PurchaseOrderNumber { get; set; }
    public string? Notes { get; set; }
    public List<InvoiceLineInput> Lines { get; set; } = new();
}

public sealed record BillListItem(Guid Id, string Number, string? VendorInvoiceNumber, Guid PartyId,
    string VendorName, DateOnly BillDate, DateOnly DueDate, string CurrencyCode, decimal Total,
    decimal Balance, DocumentStatus Status);

public sealed class BillEditModel
{
    public Guid PartyId { get; set; }
    public string? VendorInvoiceNumber { get; set; }
    public DateOnly IssueDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly DueDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
    public string CurrencyCode { get; set; } = "USD";
    /// <summary>Zero means "use the latest rate on or before the bill date".</summary>
    public decimal ExchangeRate { get; set; }
    public bool TaxInclusive { get; set; }
    public string? Notes { get; set; }
    public List<InvoiceLineInput> Lines { get; set; } = new();
}

public sealed class PaymentFilter
{
    public string? Search { get; set; }
    public PaymentDirection? Direction { get; set; }
    public Guid? PartyId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed record PaymentListItem(Guid Id, string Number, string? PartyName, PaymentDirection Direction,
    DateOnly PaymentDate, decimal Amount, decimal Unallocated, string CurrencyCode, PaymentMethod Method,
    DocumentStatus Status, string? BankAccountName);

public sealed class PaymentAllocationInput
{
    public Guid? SalesInvoiceId { get; set; }
    public Guid? BillId { get; set; }
    /// <summary>Null means "allocate as much as possible".</summary>
    public decimal? Amount { get; set; }
}

public sealed class PaymentRequest
{
    public Guid? PartyId { get; set; }
    public PaymentDirection Direction { get; set; } = PaymentDirection.Receipt;
    public DateOnly PaymentDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    /// <summary>Zero means "use the latest rate on or before the payment date".</summary>
    public decimal ExchangeRate { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.BankTransfer;
    public Guid? BankAccountId { get; set; }
    public Guid? ContraAccountId { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public List<PaymentAllocationInput> Allocations { get; set; } = new();
}

public sealed record OpenDocument(Guid Id, string Number, DateOnly Date, DateOnly DueDate, decimal Total,
    decimal Balance, string CurrencyCode, int DaysOverdue);

public sealed record BankAccountListItem(Guid Id, string Name, string? BankName, string? AccountNumber,
    Guid AccountId, string AccountCode, string AccountName, string CurrencyCode, decimal LedgerBalance,
    int UnreconciledLines, bool IsActive, DateOnly? LastReconciledDate);

public sealed class BankAccountRequest
{
    public string Name { get; set; } = string.Empty;
    public string? BankName { get; set; }
    public string? AccountNumber { get; set; }
    public string? Iban { get; set; }
    public string? Swift { get; set; }
    public Guid? AccountId { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public bool CreateLedgerAccount { get; set; }
}

public sealed record BankTransactionView(Guid Id, DateOnly Date, string? Description, string? Reference,
    decimal Debit, decimal Credit, decimal? Balance, BankLineStatus Status, Guid? MatchedJournalLineId,
    string? MatchedEntryNumber, DateTime ImportedAt);

public sealed record StatementImportResult(int Imported, int Duplicates, int Skipped, IReadOnlyList<string> Errors)
{
    public int Total => Imported + Duplicates + Skipped;
}

public sealed record MatchingCandidate(Guid JournalLineId, string EntryNumber, DateOnly Date, string? Memo,
    decimal Amount, string Account, string? Reference);

public sealed record ReconciliationLine(Guid StatementLineId, DateOnly Date, string? Description,
    decimal Amount, BankLineStatus Status, string? MatchedWith);

public sealed record ReconciliationSummary(Guid BankAccountId, string BankAccountName,
    DateOnly PeriodStart, DateOnly PeriodEnd, decimal OpeningBalance, decimal StatementBalance,
    decimal LedgerBalance, decimal UnreconciledOut, decimal UnreconciledIn, int UnmatchedLines,
    IReadOnlyList<ReconciliationLine> Lines)
{
    public decimal OutstandingChecks => UnreconciledOut;
    public decimal DepositsInTransit => UnreconciledIn;
    public decimal Difference => Money.Round(StatementBalance - LedgerBalance - UnreconciledOut + UnreconciledIn);
    public bool IsBalanced => Money.IsZero(Difference);
}

public sealed class ReportRequest
{
    public DateOnly From { get; set; } = new(DateOnly.FromDateTime(DateTime.UtcNow).Year, 1, 1);
    public DateOnly To { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public bool IncludeZeroBalances { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? CompareFrom { get; set; }
    public Guid? Unused { get; set; }
}

public sealed record TaxSummaryRow(Guid TaxCodeId, string Code, string Name, decimal Rate, TaxKind Kind,
    decimal SalesTaxCollected, decimal PurchaseTaxPaid, decimal NetLiability);

public sealed record DashboardSnapshot(
    string CompanyName, string CurrencyCode, DateOnly AsOf,
    decimal CashBalance, decimal ReceivableBalance, decimal PayableBalance,
    decimal RevenueMtd, decimal RevenueYtd, decimal ExpensesMtd, decimal ExpensesYtd,
    decimal NetProfitYtd, decimal VatLiability, int OverdueInvoiceCount, decimal OverdueAmount,
    int DraftJournalCount, int PendingApprovalCount, int UnreconciledBankLines,
    IReadOnlyList<MonthlyPoint> MonthlySeries, IReadOnlyList<RecentActivity> RecentActivity,
    IReadOnlyList<DashboardInvoice> RecentInvoices);

public sealed record MonthlyPoint(int Year, int Month, decimal Revenue, decimal Expenses)
{
    public string Label => new DateTime(Year, Month, 1).ToString("MMM yy");
}

public sealed record RecentActivity(Guid Id, string Number, DateOnly Date, string Memo, decimal Amount,
    JournalSource Source, JournalStatus Status);

public sealed record DashboardInvoice(Guid Id, string Number, string PartyName, DateOnly DueDate,
    decimal Balance, DocumentStatus Status, bool IsOverdue);

public sealed record PreCloseCheck(string Name, string Status, string Detail, bool IsBlocking);

public sealed record PeriodView(Guid Id, int Year, int Month, string Label, DateOnly StartDate,
    DateOnly EndDate, PeriodStatus Status, DateTime? ClosedAt);
