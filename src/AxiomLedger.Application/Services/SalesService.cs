using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>Customer invoicing: draft capture, issuing (posting to the ledger), voiding and credit notes.</summary>
public sealed class SalesService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly IJournalPoster _poster;
    private readonly NumberSequenceService _numbers;
    private readonly LedgerAccountResolver _accounts;
    private readonly ReferenceDataService _reference;

    public SalesService(IAccountingDbContext db, ICurrentUser user, IClock clock, AuditService audit,
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

    public async Task<PagedResult<InvoiceListItem>> ListAsync(Guid companyId, InvoiceFilter filter,
        CancellationToken ct = default)
    {
        var query = from i in _db.SalesInvoices.AsNoTracking()
                    join p in _db.Parties on i.PartyId equals p.Id
                    where i.CompanyId == companyId
                    select new { Invoice = i, PartyName = p.Name };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(x => x.Invoice.Number.Contains(term) || x.PartyName.Contains(term));
        }
        if (filter.Status.HasValue) query = query.Where(x => x.Invoice.Status == filter.Status);
        if (filter.PartyId.HasValue) query = query.Where(x => x.Invoice.PartyId == filter.PartyId);
        if (filter.From.HasValue) query = query.Where(x => x.Invoice.IssueDate >= filter.From);
        if (filter.To.HasValue) query = query.Where(x => x.Invoice.IssueDate <= filter.To);
        if (filter.Overdue == true)
        {
            var today = _clock.Today;
            query = query.Where(x =>
                (x.Invoice.Status == DocumentStatus.Issued || x.Invoice.Status == DocumentStatus.PartiallyPaid)
                && x.Invoice.DueDate < today);
        }

        var total = await query.CountAsync(ct);
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 200);

        var rows = await query
            .OrderByDescending(x => x.Invoice.IssueDate)
            .ThenByDescending(x => x.Invoice.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        var items = rows.Select(x => new InvoiceListItem(x.Invoice.Id, x.Invoice.Number, x.Invoice.Kind,
            x.Invoice.PartyId, x.PartyName, x.Invoice.IssueDate, x.Invoice.DueDate, x.Invoice.CurrencyCode,
            x.Invoice.Total, x.Invoice.BalanceDue, x.Invoice.Status)).ToList();

        return new PagedResult<InvoiceListItem>(items, total, page, size);
    }

    public async Task<SalesInvoice?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default) =>
        await _db.SalesInvoices
            .Include(i => i.Party)
            .Include(i => i.Lines).ThenInclude(l => l.Account)
            .Include(i => i.Lines).ThenInclude(l => l.TaxCode)
            .FirstOrDefaultAsync(i => i.Id == id && i.CompanyId == companyId, ct);

    public async Task<SalesInvoice> CreateDraftAsync(Guid companyId, InvoiceEditModel model,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageSales);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var invoice = new SalesInvoice
        {
            CompanyId = companyId,
            Kind = model.Kind,
            Status = DocumentStatus.Draft,
            CreatedAt = _clock.UtcNow,
            CreatedByUserId = _user.UserId
        };

        await ApplyModelAsync(invoice, model, ct);
        _db.SalesInvoices.Add(invoice);
        _audit.Record(companyId, "InvoiceDrafted", nameof(SalesInvoice), invoice.Id.ToString(),
            $"{model.Kind} for party {invoice.PartyId}");
        await _db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<SalesInvoice> UpdateDraftAsync(Guid companyId, Guid id, InvoiceEditModel model,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageSales);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var invoice = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Invoice not found.");
        if (!invoice.IsEditable)
            throw new DomainValidationException("Only draft invoices can be edited. Void and re-issue instead.");

        _db.SalesInvoiceLines.RemoveRange(invoice.Lines);
        invoice.Lines.Clear();
        invoice.Number = string.Empty;
        invoice.Kind = model.Kind;
        await ApplyModelAsync(invoice, model, ct);

        _audit.Record(companyId, "InvoiceUpdated", nameof(SalesInvoice), invoice.Id.ToString());
        await _db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<SalesInvoice> IssueAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageSales);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var invoice = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Invoice not found.");
        if (invoice.Status != DocumentStatus.Draft)
            throw new DomainValidationException("Only draft invoices can be issued.");
        if (invoice.Kind == InvoiceKind.Invoice && !invoice.Party!.IsCustomer)
            throw new DomainValidationException("The selected party is not a customer.");

        invoice.Recalculate();
        var errors = invoice.Validate();
        if (errors.Count > 0) throw new DomainValidationException(errors);

        var settings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (string.IsNullOrWhiteSpace(invoice.Number))
        {
            var prefix = invoice.Kind == InvoiceKind.CreditNote ? "CN" : settings?.InvoicePrefix ?? "INV";
            var key = invoice.Kind == InvoiceKind.CreditNote ? "creditnote" : "invoice";
            invoice.Number = await _numbers.NextAsync(companyId, key, prefix, ct);
        }

        var receivable = await _accounts.ReceivableAsync(companyId, ct);
        var entry = BuildJournal(invoice, receivable, settings?.JournalPrefix ?? "JE");
        await _poster.PostAsync(entry, ct);

        invoice.Status = DocumentStatus.Issued;
        invoice.IssuedAt = _clock.UtcNow;
        invoice.IssuedByUserId = _user.UserId;
        invoice.JournalEntryId = entry.Id;

        _audit.Record(companyId, "InvoiceIssued", nameof(SalesInvoice), invoice.Id.ToString(),
            $"{invoice.Number} {invoice.Total:N2} {invoice.CurrencyCode}");
        await _db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task VoidAsync(Guid companyId, Guid id, string reason, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageSales);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var invoice = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Invoice not found.");
        if (invoice.Status == DocumentStatus.Void) return;
        if (invoice.AmountPaid > 0.005m)
            throw new DomainValidationException(
                "Payments are allocated to this invoice. Reverse those payments before voiding it.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainValidationException("Give a reason for voiding the invoice.");

        if (invoice.JournalEntryId is { } journalId)
        {
            var original = await _db.JournalEntries.Include(j => j.Lines)
                .FirstOrDefaultAsync(j => j.Id == journalId, ct);
            if (original is { Status: JournalStatus.Posted })
            {
                var settings = await _db.CompanySettings.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
                var number = await _numbers.NextAsync(companyId, "journal", settings?.JournalPrefix ?? "JE", ct);
                var reversal = original.CreateReversal(_clock.Today, number, _user.UserId ?? "system",
                    $"Void of {invoice.Number}: {reason}");
                reversal.Source = invoice.Kind == InvoiceKind.CreditNote
                    ? JournalSource.SalesCreditNote
                    : JournalSource.SalesInvoice;
                await _poster.PostAsync(reversal, ct);
                _db.JournalEntries.Add(reversal);
                original.Status = JournalStatus.Reversed;
                original.ReversedById = reversal.Id;
            }
        }

        invoice.Status = DocumentStatus.Void;
        invoice.VoidedAt = _clock.UtcNow;
        invoice.VoidReason = reason.Trim();

        _audit.Record(companyId, "InvoiceVoided", nameof(SalesInvoice), invoice.Id.ToString(), reason);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OpenDocument>> GetOpenInvoicesAsync(Guid companyId, Guid partyId,
        CancellationToken ct = default)
    {
        var today = _clock.Today;
        var invoices = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.PartyId == partyId && i.Kind == InvoiceKind.Invoice &&
                        (i.Status == DocumentStatus.Issued || i.Status == DocumentStatus.PartiallyPaid))
            .OrderBy(i => i.DueDate)
            .ToListAsync(ct);

        return invoices
            .Where(i => i.BalanceDue > 0.005m)
            .Select(i => new OpenDocument(i.Id, i.Number, i.IssueDate, i.DueDate, i.Total, i.BalanceDue,
                i.CurrencyCode, (today.DayNumber - i.DueDate.DayNumber) is var d && d > 0 ? d : 0))
            .ToList();
    }

    private async Task ApplyModelAsync(SalesInvoice invoice, InvoiceEditModel model, CancellationToken ct)
    {
        var party = await _db.Parties.FirstOrDefaultAsync(p => p.Id == model.PartyId && p.CompanyId == invoice.CompanyId, ct)
            ?? throw new DomainValidationException("Select a valid customer.");
        if (!party.IsActive)
            throw new DomainValidationException("The selected customer is inactive.");

        var baseCurrency = await _db.Companies.Where(c => c.Id == invoice.CompanyId)
            .Select(c => c.BaseCurrencyCode).FirstOrDefaultAsync(ct) ?? "USD";
        var currency = string.IsNullOrWhiteSpace(model.CurrencyCode)
            ? baseCurrency
            : model.CurrencyCode.Trim().ToUpperInvariant();

        invoice.PartyId = party.Id;
        invoice.IssueDate = model.IssueDate;
        invoice.DueDate = model.DueDate;
        invoice.CurrencyCode = currency;
        invoice.ExchangeRate = currency == baseCurrency
            ? 1m
            : model.ExchangeRate > 0
                ? model.ExchangeRate
                : await _reference.ResolveExchangeRateAsync(invoice.CompanyId, currency, model.IssueDate, ct);
        invoice.PurchaseOrderNumber = model.PurchaseOrderNumber;
        invoice.Notes = model.Notes;

        var taxCodes = await _db.TaxCodes
            .Where(t => t.CompanyId == invoice.CompanyId)
            .ToDictionaryAsync(t => t.Id, ct);

        var errors = new List<string>();
        var lineNumber = 1;
        foreach (var input in model.Lines)
        {
            taxCodes.TryGetValue(input.TaxCodeId ?? Guid.Empty, out var tax);
            var line = new SalesInvoiceLine
            {
                LineNumber = lineNumber++,
                Description = input.Description?.Trim() ?? string.Empty,
                AccountId = input.AccountId,
                Quantity = input.Quantity,
                UnitPrice = input.UnitPrice,
                DiscountPercent = input.DiscountPercent,
                TaxCodeId = tax?.Id,
                TaxRate = tax?.Rate ?? 0m,
                TaxInclusive = model.TaxInclusive || (tax?.IsInclusive ?? false),
                TaxAccountId = tax?.TaxAccountId,
                DepartmentId = input.DepartmentId
            };
            invoice.Lines.Add(line);

            var account = await _db.Accounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == input.AccountId && a.CompanyId == invoice.CompanyId, ct);
            if (account is null) errors.Add($"Line {line.LineNumber}: the revenue account does not exist.");
            else if (!account.IsPostable || !account.IsActive)
                errors.Add($"Line {line.LineNumber}: account {account.Code} cannot be posted to.");
            else if (account.Type != AccountType.Revenue)
                errors.Add($"Line {line.LineNumber}: account {account.Code} is not a revenue account.");
        }

        invoice.Recalculate();
        errors.AddRange(invoice.Validate());
        if (errors.Count > 0) throw new DomainValidationException(errors);
    }

    private JournalEntry BuildJournal(SalesInvoice invoice, Account receivable, string journalPrefix)
    {
        var isCredit = invoice.Kind == InvoiceKind.CreditNote;
        var entry = new JournalEntry
        {
            CompanyId = invoice.CompanyId,
            EntryDate = invoice.IssueDate,
            Reference = invoice.Number,
            Memo = $"{(isCredit ? "Credit note" : "Invoice")} {invoice.Number} \u2013 {invoice.Party?.Name}",
            Source = isCredit ? JournalSource.SalesCreditNote : JournalSource.SalesInvoice,
            Status = JournalStatus.Draft,
            CreatedByUserId = _user.UserId,
            CreatedAt = _clock.UtcNow,
            CurrencyCode = invoice.CurrencyCode,
            ExchangeRate = invoice.ExchangeRate
        };

        var rate = invoice.ExchangeRate;
        var total = 0m;
        foreach (var line in invoice.Lines)
        {
            var net = Money.Round(line.NetAmount * rate);
            var tax = Money.Round(line.TaxAmount * rate);
            total += net + tax;

            entry.Lines.Add(new JournalLine
            {
                CompanyId = invoice.CompanyId,
                AccountId = line.AccountId,
                Description = line.Description,
                Debit = isCredit ? net : 0m,
                Credit = isCredit ? 0m : net,
                DepartmentId = line.DepartmentId,
                PartyId = invoice.PartyId,
                CurrencyCode = invoice.CurrencyCode,
                ForeignAmount = line.NetAmount,
                ExchangeRate = rate,
                LineNumber = entry.Lines.Count + 1
            });

            if (tax != 0m && line.TaxAccountId.HasValue)
            {
                entry.Lines.Add(new JournalLine
                {
                    CompanyId = invoice.CompanyId,
                    AccountId = line.TaxAccountId.Value,
                    Description = $"Tax {line.TaxRate:0.##}% \u2013 {line.Description}",
                    Debit = isCredit ? tax : 0m,
                    Credit = isCredit ? 0m : tax,
                    PartyId = invoice.PartyId,
                    CurrencyCode = invoice.CurrencyCode,
                    ForeignAmount = line.TaxAmount,
                    ExchangeRate = rate,
                    LineNumber = entry.Lines.Count + 1
                });
            }
        }

        entry.Lines.Add(new JournalLine
        {
            CompanyId = invoice.CompanyId,
            AccountId = receivable.Id,
            Description = $"{invoice.Party?.Name} \u2013 {invoice.Number}",
            Debit = isCredit ? 0m : total,
            Credit = isCredit ? total : 0m,
            PartyId = invoice.PartyId,
            CurrencyCode = invoice.CurrencyCode,
            ForeignAmount = invoice.Total,
            ExchangeRate = rate,
            LineNumber = entry.Lines.Count + 1
        });

        entry.RecalculateTotals();
        return entry;
    }
}
