namespace AxiomLedger.Domain;

/// <summary>
/// A double-entry journal. Every entry must balance in the company's base currency before it can be posted.
/// Entries are immutable once posted: corrections are made with a reversing entry.
/// </summary>
public class JournalEntry : CompanyEntity
{
    public string Number { get; set; } = string.Empty;
    public DateOnly EntryDate { get; set; }
    public string? Reference { get; set; }
    public string Memo { get; set; } = string.Empty;
    public JournalSource Source { get; set; } = JournalSource.Manual;
    public JournalStatus Status { get; set; } = JournalStatus.Draft;
    public Guid? ReversalOfId { get; set; }
    public Guid? ReversedById { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? PostedByUserId { get; set; }
    public DateTime? PostedAt { get; set; }
    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalNote { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public string? CurrencyCode { get; set; }
    public decimal? ExchangeRate { get; set; }
    public List<JournalLine> Lines { get; set; } = new();

    public bool IsEditable => Status is JournalStatus.Draft or JournalStatus.PendingApproval;

    /// <summary>Validates structure and balance. Account existence / postability is enforced by the application layer.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Memo))
            errors.Add("A journal memo is required.");
        if (Lines.Count < 2)
            errors.Add("A journal entry needs at least two lines.");
        if (Lines.Any(l => l.AccountId == Guid.Empty))
            errors.Add("Every journal line needs an account.");
        if (Lines.Any(l => l.Debit < 0 || l.Credit < 0))
            errors.Add("Debit and credit amounts cannot be negative; use the opposite column instead.");
        if (Lines.Any(l => l.Debit > 0 && l.Credit > 0))
            errors.Add("A line cannot have both a debit and a credit amount.");
        if (Lines.Any(l => l.Debit == 0 && l.Credit == 0))
            errors.Add("Every line must have a debit or a credit amount.");
        var duplicates = Lines.GroupBy(l => l.AccountId).Where(g => g.Count() > 1 && g.Any(x => x.DepartmentId == null))
            .Select(g => g.Key).ToList();
        if (Lines.Count > 0)
        {
            var debit = Money.Round(Lines.Sum(l => l.Debit));
            var credit = Money.Round(Lines.Sum(l => l.Credit));
            if (!Money.IsBalanced(debit, credit))
                errors.Add($"The journal does not balance: debits {debit:N2} vs credits {credit:N2}.");
        }
        return errors;
    }

    public void RecalculateTotals()
    {
        TotalDebit = Money.Round(Lines.Sum(l => l.Debit));
        TotalCredit = Money.Round(Lines.Sum(l => l.Credit));
    }

    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0) throw new DomainValidationException(errors);
    }

    public JournalLine AddLine(Guid accountId, decimal debit, decimal credit, string? description = null)
    {
        var line = new JournalLine
        {
            CompanyId = CompanyId,
            AccountId = accountId,
            Debit = Money.Round(debit),
            Credit = Money.Round(credit),
            Description = description,
            LineNumber = Lines.Count + 1
        };
        Lines.Add(line);
        return line;
    }

    /// <summary>Creates a posted reversal that mirrors this entry on the requested date.</summary>
    public JournalEntry CreateReversal(DateOnly date, string number, string userId, string? memo = null)
    {
        var reversal = new JournalEntry
        {
            CompanyId = CompanyId,
            Number = number,
            EntryDate = date,
            Reference = Number,
            Memo = memo ?? $"Reversal of {Number}",
            Source = Source,
            Status = JournalStatus.Posted,
            ReversalOfId = Id,
            CreatedByUserId = userId,
            PostedByUserId = userId,
            PostedAt = DateTime.UtcNow,
            CurrencyCode = CurrencyCode,
            ExchangeRate = ExchangeRate
        };

        foreach (var line in Lines)
        {
            reversal.Lines.Add(new JournalLine
            {
                CompanyId = CompanyId,
                AccountId = line.AccountId,
                Description = line.Description,
                LineNumber = line.LineNumber,
                Debit = line.Credit,
                Credit = line.Debit,
                CurrencyCode = line.CurrencyCode,
                ForeignAmount = line.ForeignAmount,
                ExchangeRate = line.ExchangeRate,
                DepartmentId = line.DepartmentId,
                PartyId = line.PartyId,
                TaxCodeId = line.TaxCodeId
            });
        }

        reversal.RecalculateTotals();
        return reversal;
    }
}

public class JournalLine : CompanyEntity
{
    public Guid JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public int LineNumber { get; set; }
    public string? Description { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? CurrencyCode { get; set; }
    /// <summary>Original transaction-currency amount, retained for audit and FX analysis.</summary>
    public decimal? ForeignAmount { get; set; }
    public decimal ExchangeRate { get; set; } = 1m;
    public Guid? DepartmentId { get; set; }
    public Guid? PartyId { get; set; }
    public Guid? TaxCodeId { get; set; }
    /// <summary>True once the line has been ticked off in a bank reconciliation.</summary>
    public bool IsCleared { get; set; }
    public Guid? BankStatementLineId { get; set; }

    public decimal SignedAmount => Debit - Credit;
}
