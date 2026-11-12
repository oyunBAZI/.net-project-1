using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>
/// Customer receipts and vendor payments. Allocations update document balances; unallocated money stays on the
/// party account as a credit or prepayment. Foreign-currency settlements book the difference to FX gain/loss.
/// </summary>
public sealed class PaymentService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly IJournalPoster _poster;
    private readonly NumberSequenceService _numbers;
    private readonly LedgerAccountResolver _accounts;
    private readonly ReferenceDataService _reference;

    public PaymentService(IAccountingDbContext db, ICurrentUser user, IClock clock, AuditService audit,
        IJournalPoster poster, NumberSequenceService numbers, LedgerAccountResolver accounts,
        ReferenceDataService reference)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = audit;
        _poster = poster;
        _numbers = numbers;
        _accounts = accounts;
        _reference = reference;
    }

    public async Task<PagedResult<PaymentListItem>> ListAsync(Guid companyId, PaymentFilter filter,
        CancellationToken ct = default)
    {
        var query = from p in _db.Payments.AsNoTracking()
                    join party in _db.Parties on p.PartyId equals party.Id into parties
                    from party in parties.DefaultIfEmpty()
                    where p.CompanyId == companyId
                    select new { Payment = p, PartyName = party != null ? party.Name : null };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(x => x.Payment.Number.Contains(term) || x.PartyName!.Contains(term));
        }
        if (filter.Direction.HasValue) query = query.Where(x => x.Payment.Direction == filter.Direction);
        if (filter.PartyId.HasValue) query = query.Where(x => x.Payment.PartyId == filter.PartyId);
        if (filter.From.HasValue) query = query.Where(x => x.Payment.PaymentDate >= filter.From);
        if (filter.To.HasValue) query = query.Where(x => x.Payment.PaymentDate <= filter.To);

        var total = await query.CountAsync(ct);
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 200);

        var rows = await query
            .OrderByDescending(x => x.Payment.PaymentDate)
            .ThenByDescending(x => x.Payment.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .Select(x => new
            {
                x.Payment,
                x.PartyName,
                Allocated = x.Payment.Allocations.Sum(a => (double?)a.Amount) ?? 0d,
                BankName = x.Payment.BankAccount != null ? x.Payment.BankAccount.Name : null
            })
            .ToListAsync(ct);

        var items = rows.Select(x => new PaymentListItem(x.Payment.Id, x.Payment.Number, x.PartyName,
            x.Payment.Direction, x.Payment.PaymentDate, x.Payment.Amount,
            Money.Round(x.Payment.Amount - (decimal)x.Allocated), x.Payment.CurrencyCode, x.Payment.Method,
            x.Payment.Status, x.BankName)).ToList();

        return new PagedResult<PaymentListItem>(items, total, page, size);
    }

    public async Task<Payment?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default) =>
        await _db.Payments
            .Include(p => p.Party)
            .Include(p => p.BankAccount)
            .Include(p => p.Allocations).ThenInclude(a => a.SalesInvoice)
            .Include(p => p.Allocations).ThenInclude(a => a.Bill)
            .FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId, ct);

    public async Task<Payment> RecordAsync(Guid companyId, PaymentRequest request, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManagePayments);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        if (request.Amount <= 0m)
            throw new DomainValidationException("The payment amount must be greater than zero.");
        if (request.PaymentDate == default)
            throw new DomainValidationException("A payment date is required.");

        var settings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        var baseCurrency = await _db.Companies.Where(c => c.Id == companyId)
            .Select(c => c.BaseCurrencyCode).FirstOrDefaultAsync(ct) ?? "USD";
        var currency = string.IsNullOrWhiteSpace(request.CurrencyCode)
            ? baseCurrency
            : request.CurrencyCode.Trim().ToUpperInvariant();
        var rate = currency == baseCurrency
            ? 1m
            : request.ExchangeRate > 0
                ? request.ExchangeRate
                : await _reference.ResolveExchangeRateAsync(companyId, currency, request.PaymentDate, ct);

        Account cashAccount;
        BankAccount? bankAccount = null;
        if (request.BankAccountId is { } bankId)
        {
            bankAccount = await _db.BankAccounts
                .FirstOrDefaultAsync(b => b.Id == bankId && b.CompanyId == companyId, ct)
                ?? throw new DomainValidationException("Select a valid bank account.");
            cashAccount = await _db.Accounts
                .FirstOrDefaultAsync(a => a.Id == bankAccount.AccountId && a.CompanyId == companyId, ct)
                ?? throw new DomainValidationException("The bank account has no ledger account.");
        }
        else if (request.ContraAccountId is { } contraId)
        {
            cashAccount = await _db.Accounts
                .FirstOrDefaultAsync(a => a.Id == contraId && a.CompanyId == companyId && a.IsPostable && a.IsActive, ct)
                ?? throw new DomainValidationException("Select a valid cash or bank ledger account.");
        }
        else
        {
            throw new DomainValidationException("Select the bank or cash account the money moved through.");
        }

        Party? party = null;
        if (request.PartyId is { } partyId)
        {
            party = await _db.Parties.FirstOrDefaultAsync(p => p.Id == partyId && p.CompanyId == companyId, ct)
                ?? throw new DomainValidationException("Select a valid customer or vendor.");
            if (request.Direction == PaymentDirection.Receipt && !party.IsCustomer)
                throw new DomainValidationException("Receipts must come from a customer.");
            if (request.Direction == PaymentDirection.Disbursement && !party.IsVendor)
                throw new DomainValidationException("Payments must go to a vendor.");
        }

        var allocations = new List<PaymentAllocation>();
        var allocatedTotal = 0m;

        if (request.Allocations.Count > 0)
        {
            if (party is null)
                throw new DomainValidationException("Select a party before allocating a payment to documents.");
            if (request.Direction == PaymentDirection.Receipt)
            {
                var ids = request.Allocations.Where(a => a.SalesInvoiceId.HasValue)
                    .Select(a => a.SalesInvoiceId!.Value).Distinct().ToList();
                var invoices = await _db.SalesInvoices
                    .Where(i => i.CompanyId == companyId && i.PartyId == party.Id && ids.Contains(i.Id))
                    .ToDictionaryAsync(i => i.Id, ct);

                foreach (var input in request.Allocations)
                {
                    if (input.SalesInvoiceId is not { } invoiceId)
                        throw new DomainValidationException("Receipt allocations must reference a customer invoice.");
                    if (!invoices.TryGetValue(invoiceId, out var invoice))
                        throw new DomainValidationException("An allocated invoice does not belong to this customer.");
                    if (!invoice.IsOpen)
                        throw new DomainValidationException($"Invoice {invoice.Number} is not open.");
                    if (!invoice.CurrencyCode.Equals(currency, StringComparison.OrdinalIgnoreCase))
                        throw new DomainValidationException(
                            $"Invoice {invoice.Number} is in {invoice.CurrencyCode}; record the receipt in the same currency.");

                    var already = allocations.Where(a => a.SalesInvoiceId == invoiceId).Sum(a => a.Amount);
                    var available = Money.Round(invoice.BalanceDue - already);
                    var remaining = Money.Round(request.Amount - allocatedTotal);
                    var amount = input.Amount ?? Money.Round(Math.Min(available, Math.Max(0m, remaining)));
                    if (amount <= 0) continue;
                    if (amount > available + 0.005m)
                        throw new DomainValidationException(
                            $"Allocation to {invoice.Number} exceeds its balance of {available:N2}.");
                    if (allocatedTotal + amount > request.Amount + 0.005m)
                        throw new DomainValidationException("Allocations exceed the payment amount.");

                    allocations.Add(new PaymentAllocation { SalesInvoiceId = invoiceId, Amount = Money.Round(amount) });
                    allocatedTotal += Money.Round(amount);
                }
            }
            else
            {
                var ids = request.Allocations.Where(a => a.BillId.HasValue)
                    .Select(a => a.BillId!.Value).Distinct().ToList();
                var bills = await _db.Bills
                    .Where(b => b.CompanyId == companyId && b.PartyId == party.Id && ids.Contains(b.Id))
                    .ToDictionaryAsync(b => b.Id, ct);

                foreach (var input in request.Allocations)
                {
                    if (input.BillId is not { } billId)
                        throw new DomainValidationException("Payment allocations must reference a vendor bill.");
                    if (!bills.TryGetValue(billId, out var bill))
                        throw new DomainValidationException("An allocated bill does not belong to this vendor.");
                    if (!bill.IsOpen)
                        throw new DomainValidationException($"Bill {bill.Number} is not open.");
                    if (!bill.CurrencyCode.Equals(currency, StringComparison.OrdinalIgnoreCase))
                        throw new DomainValidationException(
                            $"Bill {bill.Number} is in {bill.CurrencyCode}; record the payment in the same currency.");

                    var already = allocations.Where(a => a.BillId == billId).Sum(a => a.Amount);
                    var available = Money.Round(bill.BalanceDue - already);
                    var remaining = Money.Round(request.Amount - allocatedTotal);
                    var amount = input.Amount ?? Money.Round(Math.Min(available, Math.Max(0m, remaining)));
                    if (amount <= 0) continue;
                    if (amount > available + 0.005m)
                        throw new DomainValidationException(
                            $"Allocation to {bill.Number} exceeds its balance of {available:N2}.");
                    if (allocatedTotal + amount > request.Amount + 0.005m)
                        throw new DomainValidationException("Allocations exceed the payment amount.");

                    allocations.Add(new PaymentAllocation { BillId = billId, Amount = Money.Round(amount) });
                    allocatedTotal += Money.Round(amount);
                }
            }
        }

        var payment = new Payment
        {
            CompanyId = companyId,
            PartyId = party?.Id,
            Direction = request.Direction,
            PaymentDate = request.PaymentDate,
            Amount = Money.Round(request.Amount),
            CurrencyCode = currency,
            ExchangeRate = rate,
            Method = request.Method,
            BankAccountId = bankAccount?.Id,
            ContraAccountId = request.ContraAccountId,
            Reference = request.Reference,
            Notes = request.Notes,
            Status = DocumentStatus.Draft,
            CreatedAt = _clock.UtcNow,
            CreatedByUserId = _user.UserId
        };
        payment.Allocations = allocations;
        payment.Number = await _numbers.NextAsync(companyId, "payment", settings?.PaymentPrefix ?? "PMT", ct);

        var bankBase = Money.Round(payment.Amount * rate);
        var documentBase = 0m;
        foreach (var allocation in allocations)
        {
            if (allocation.SalesInvoiceId is { } invoiceId)
            {
                var invoice = await _db.SalesInvoices.FirstAsync(i => i.Id == invoiceId, ct);
                documentBase += Money.Round(allocation.Amount * invoice.ExchangeRate);
                invoice.AmountPaid = Money.Round(invoice.AmountPaid + allocation.Amount);
                invoice.RefreshStatus();
            }
            else if (allocation.BillId is { } billId)
            {
                var bill = await _db.Bills.FirstAsync(b => b.Id == billId, ct);
                documentBase += Money.Round(allocation.Amount * bill.ExchangeRate);
                bill.AmountPaid = Money.Round(bill.AmountPaid + allocation.Amount);
                bill.RefreshStatus();
            }
        }
        documentBase += Money.Round(Money.Round(payment.Amount - allocatedTotal) * rate);

        var controlAccount = payment.Direction == PaymentDirection.Receipt
            ? await _accounts.ReceivableAsync(companyId, ct)
            : await _accounts.PayableAsync(companyId, ct);
        var fxGain = await _accounts.FxGainAsync(companyId, ct);
        var fxLoss = await _accounts.FxLossAsync(companyId, ct);
        payment.Party = party;

        var entry = BuildJournal(companyId, payment, cashAccount, documentBase, bankBase,
            controlAccount.Id, fxGain.Id, fxLoss.Id);
        await _poster.PostAsync(entry, ct);

        payment.JournalEntryId = entry.Id;
        payment.Status = DocumentStatus.Issued;
        payment.PostedAt = _clock.UtcNow;
        payment.PostedByUserId = _user.UserId;
        _db.Payments.Add(payment);

        _audit.Record(companyId, "PaymentPosted", nameof(Payment), payment.Id.ToString(),
            $"{payment.Number} {payment.Amount:N2} {currency}");
        await _db.SaveChangesAsync(ct);
        return payment;
    }

    private JournalEntry BuildJournal(Guid companyId, Payment payment, Account cashAccount,
        decimal documentBase, decimal bankBase, Guid controlAccountId, Guid fxGainAccountId, Guid fxLossAccountId)
    {
        var isReceipt = payment.Direction == PaymentDirection.Receipt;
        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryDate = payment.PaymentDate,
            Reference = payment.Number,
            Memo = $"{(isReceipt ? "Receipt" : "Payment")} {payment.Number}" +
                   (payment.Party != null ? $" \u2013 {payment.Party.Name}" : string.Empty),
            Source = isReceipt ? JournalSource.CustomerReceipt : JournalSource.VendorPayment,
            Status = JournalStatus.Draft,
            CreatedByUserId = _user.UserId,
            CreatedAt = _clock.UtcNow,
            CurrencyCode = payment.CurrencyCode,
            ExchangeRate = payment.ExchangeRate
        };

        entry.Lines.Add(new JournalLine
        {
            CompanyId = companyId,
            AccountId = cashAccount.Id,
            Description = isReceipt ? "Money received" : "Money paid",
            Debit = isReceipt ? bankBase : 0m,
            Credit = isReceipt ? 0m : bankBase,
            PartyId = payment.PartyId,
            CurrencyCode = payment.CurrencyCode,
            ForeignAmount = payment.Amount,
            ExchangeRate = payment.ExchangeRate,
            LineNumber = 1
        });

        if (documentBase != 0m)
        {
            entry.Lines.Add(new JournalLine
            {
                CompanyId = companyId,
                AccountId = controlAccountId,
                Description = payment.Party?.Name,
                Debit = isReceipt ? 0m : documentBase,
                Credit = isReceipt ? documentBase : 0m,
                PartyId = payment.PartyId,
                LineNumber = 2
            });
        }

        var fx = isReceipt ? bankBase - documentBase : documentBase - bankBase;
        if (fx > 0.004m)
        {
            entry.Lines.Add(new JournalLine
            {
                CompanyId = companyId,
                AccountId = fxGainAccountId,
                Description = "Foreign exchange gain",
                Credit = fx,
                LineNumber = entry.Lines.Count + 1
            });
        }
        else if (fx < -0.004m)
        {
            entry.Lines.Add(new JournalLine
            {
                CompanyId = companyId,
                AccountId = fxLossAccountId,
                Description = "Foreign exchange loss",
                Debit = -fx,
                LineNumber = entry.Lines.Count + 1
            });
        }

        entry.RecalculateTotals();
        return entry;
    }

    public async Task VoidAsync(Guid companyId, Guid id, string reason, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManagePayments);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var payment = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Payment not found.");
        if (payment.Status == DocumentStatus.Void) return;
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainValidationException("Give a reason for voiding the payment.");

        if (payment.JournalEntryId is { } journalId)
        {
            var original = await _db.JournalEntries.Include(j => j.Lines)
                .FirstOrDefaultAsync(j => j.Id == journalId, ct);
            if (original is { Status: JournalStatus.Posted })
            {
                var settings = await _db.CompanySettings.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
                var number = await _numbers.NextAsync(companyId, "journal", settings?.JournalPrefix ?? "JE", ct);
                var reversal = original.CreateReversal(_clock.Today, number, _user.UserId ?? "system",
                    $"Void of {payment.Number}: {reason}");
                reversal.Source = payment.Direction == PaymentDirection.Receipt
                    ? JournalSource.CustomerReceipt
                    : JournalSource.VendorPayment;
                await _poster.PostAsync(reversal, ct);
                _db.JournalEntries.Add(reversal);
                original.Status = JournalStatus.Reversed;
                original.ReversedById = reversal.Id;
            }
        }

        foreach (var allocation in payment.Allocations)
        {
            if (allocation.SalesInvoiceId is { } invoiceId)
            {
                var invoice = await _db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
                if (invoice is not null)
                {
                    invoice.AmountPaid = Money.Round(invoice.AmountPaid - allocation.Amount);
                    invoice.RefreshStatus();
                }
            }
            else if (allocation.BillId is { } billId)
            {
                var bill = await _db.Bills.FirstOrDefaultAsync(b => b.Id == billId, ct);
                if (bill is not null)
                {
                    bill.AmountPaid = Money.Round(bill.AmountPaid - allocation.Amount);
                    bill.RefreshStatus();
                }
            }
        }

        payment.Status = DocumentStatus.Void;
        payment.Notes = string.IsNullOrWhiteSpace(payment.Notes)
            ? $"Voided: {reason}"
            : $"{payment.Notes}\nVoided: {reason}";

        _audit.Record(companyId, "PaymentVoided", nameof(Payment), payment.Id.ToString(), reason);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OpenDocument>> GetOpenDocumentsAsync(Guid companyId, Guid partyId,
        PaymentDirection direction, CancellationToken ct = default)
    {
        var today = _clock.Today;
        if (direction == PaymentDirection.Receipt)
        {
            var invoices = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.CompanyId == companyId && i.PartyId == partyId && i.Kind == InvoiceKind.Invoice &&
                            (i.Status == DocumentStatus.Issued || i.Status == DocumentStatus.PartiallyPaid))
                .OrderBy(i => i.DueDate).ToListAsync(ct);
            return invoices.Where(i => i.BalanceDue > 0.005m).Select(i => new OpenDocument(i.Id, i.Number,
                i.IssueDate, i.DueDate, i.Total, i.BalanceDue, i.CurrencyCode,
                Math.Max(0, today.DayNumber - i.DueDate.DayNumber))).ToList();
        }

        var bills = await _db.Bills.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.PartyId == partyId &&
                        (b.Status == DocumentStatus.Issued || b.Status == DocumentStatus.PartiallyPaid))
            .OrderBy(b => b.DueDate).ToListAsync(ct);
        return bills.Where(b => b.BalanceDue > 0.005m).Select(b => new OpenDocument(b.Id, b.Number,
            b.IssueDate, b.DueDate, b.Total, b.BalanceDue, b.CurrencyCode,
            Math.Max(0, today.DayNumber - b.DueDate.DayNumber))).ToList();
    }

    /// <summary>Oldest-first allocation suggestion used by the payment screen.</summary>
    public async Task<IReadOnlyList<PaymentAllocationInput>> SuggestAllocationsAsync(Guid companyId, Guid partyId,
        PaymentDirection direction, decimal amount, CancellationToken ct = default)
    {
        var documents = await GetOpenDocumentsAsync(companyId, partyId, direction, ct);
        var result = new List<PaymentAllocationInput>();
        var remaining = Money.Round(amount);
        foreach (var document in documents)
        {
            if (remaining <= 0.005m) break;
            var applied = Math.Min(document.Balance, remaining);
            result.Add(new PaymentAllocationInput
            {
                SalesInvoiceId = direction == PaymentDirection.Receipt ? document.Id : null,
                BillId = direction == PaymentDirection.Disbursement ? document.Id : null,
                Amount = Money.Round(applied)
            });
            remaining = Money.Round(remaining - applied);
        }
        return result;
    }

    public async Task<decimal> GetUnappliedCreditsAsync(Guid companyId, Guid partyId,
        PaymentDirection direction = PaymentDirection.Receipt, CancellationToken ct = default)
    {
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.PartyId == partyId &&
                        p.Direction == direction && p.Status == DocumentStatus.Issued)
            .Select(p => new { p.Amount, Allocated = p.Allocations.Sum(a => (double?)a.Amount) ?? 0d })
            .ToListAsync(ct);

        return Money.Round(payments.Sum(p => p.Amount - (decimal)p.Allocated));
    }
}
