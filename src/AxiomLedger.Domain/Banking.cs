namespace AxiomLedger.Domain;

/// <summary>
/// A cash receipt or payment against a party. Unallocated amounts become customer credits / vendor prepayments.
/// </summary>
public class Payment : AuditableEntity
{
    public string Number { get; set; } = string.Empty;
    public Guid? PartyId { get; set; }
    public Party? Party { get; set; }
    public PaymentDirection Direction { get; set; }
    public DateOnly PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public decimal ExchangeRate { get; set; } = 1m;
    public PaymentMethod Method { get; set; } = PaymentMethod.BankTransfer;
    public Guid? BankAccountId { get; set; }
    public BankAccount? BankAccount { get; set; }
    /// <summary>Ledger account used when no bank account is selected (e.g. cash on hand).</summary>
    public Guid? ContraAccountId { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public Guid? JournalEntryId { get; set; }
    public DateTime? PostedAt { get; set; }
    public string? PostedByUserId { get; set; }
    public List<PaymentAllocation> Allocations { get; set; } = new();

    public decimal AllocatedAmount => Money.Round(Allocations.Sum(a => a.Amount));
    public decimal UnallocatedAmount => Money.Round(Amount - AllocatedAmount);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Amount <= 0) errors.Add("The payment amount must be greater than zero.");
        if (PaymentDate == default) errors.Add("A payment date is required.");
        if (BankAccountId is null && ContraAccountId is null)
            errors.Add("Select the bank or cash account the money moved through.");
        if (AllocatedAmount > Amount + 0.005m)
            errors.Add("Allocations exceed the payment amount.");
        if (Allocations.Any(a => a.Amount <= 0))
            errors.Add("Allocation amounts must be greater than zero.");
        if (Allocations.Any(a => a.SalesInvoiceId is null && a.BillId is null))
            errors.Add("Each allocation must reference an invoice or a bill.");
        return errors;
    }
}

public class PaymentAllocation : Entity
{
    public Guid PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public Guid? SalesInvoiceId { get; set; }
    public SalesInvoice? SalesInvoice { get; set; }
    public Guid? BillId { get; set; }
    public Bill? Bill { get; set; }
    public decimal Amount { get; set; }
}

public class BankAccount : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? BankName { get; set; }
    public string? AccountNumber { get; set; }
    public string? Iban { get; set; }
    public string? Swift { get; set; }
    /// <summary>The ledger (asset) account that represents this bank account.</summary>
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public bool IsActive { get; set; } = true;
    public DateOnly? LastReconciledDate { get; set; }
    public List<BankStatementLine> StatementLines { get; set; } = new();
}

public class BankStatementLine : CompanyEntity
{
    public Guid BankAccountId { get; set; }
    public BankAccount? BankAccount { get; set; }
    public Guid ImportBatchId { get; set; }
    public DateOnly TransactionDate { get; set; }
    public DateOnly? ValueDate { get; set; }
    public string? Description { get; set; }
    public string? Reference { get; set; }
    /// <summary>Money leaving the bank account.</summary>
    public decimal Debit { get; set; }
    /// <summary>Money entering the bank account.</summary>
    public decimal Credit { get; set; }
    public decimal? Balance { get; set; }
    public BankLineStatus Status { get; set; } = BankLineStatus.Unmatched;
    public Guid? MatchedJournalLineId { get; set; }
    public Guid? MatchedPaymentId { get; set; }
    public string? ExternalId { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    public decimal SignedAmount => Credit - Debit;
}

public class BankReconciliation : CompanyEntity
{
    public Guid BankAccountId { get; set; }
    public BankAccount? BankAccount { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal StatementOpeningBalance { get; set; }
    public decimal StatementClosingBalance { get; set; }
    public decimal LedgerClosingBalance { get; set; }
    public decimal OutstandingChecks { get; set; }
    public decimal DepositsInTransit { get; set; }
    public ReconciliationStatus Status { get; set; } = ReconciliationStatus.InProgress;
    public DateTime? CompletedAt { get; set; }
    public string? CompletedByUserId { get; set; }
    public string? Notes { get; set; }

    /// <summary>Difference between the reconciled statement balance and the ledger balance.</summary>
    public decimal Difference =>
        Money.Round(StatementClosingBalance - LedgerClosingBalance - OutstandingChecks + DepositsInTransit);

    public bool IsBalanced => Money.IsZero(Difference);
}
