using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>Vendor bills: capture, receipt (posting), payment tracking and voiding.</summary>
public sealed class PurchaseService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly IJournalPoster _poster;
    private readonly NumberSequenceService _numbers;
    private readonly LedgerAccountResolver _accounts;
    private readonly ReferenceDataService _reference;

    public PurchaseService(IAccountingDbContext db, ICurrentUser user, IClock clock, AuditService audit,
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

    public async Task<PagedResult<BillListItem>> ListAsync(Guid companyId, InvoiceFilter filter,
        CancellationToken ct = default)
    {
        var query = from b in _db.Bills.AsNoTracking()
                    join p in _db.Parties on b.PartyId equals p.Id
                    where b.CompanyId == companyId
                    select new { Bill = b, PartyName = p.Name };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(x => x.Bill.Number.Contains(term) || x.PartyName.Contains(term) ||
                                     (x.Bill.VendorInvoiceNumber != null && x.Bill.VendorInvoiceNumber.Contains(term)));
        }
        if (filter.Status.HasValue) query = query.Where(x => x.Bill.Status == filter.Status);
        if (filter.PartyId.HasValue) query = query.Where(x => x.Bill.PartyId == filter.PartyId);
        if (filter.From.HasValue) query = query.Where(x => x.Bill.IssueDate >= filter.From);
        if (filter.To.HasValue) query = query.Where(x => x.Bill.IssueDate <= filter.To);
        if (filter.Overdue == true)
        {
            var today = _clock.Today;
            query = query.Where(x =>
                (x.Bill.Status == DocumentStatus.Issued || x.Bill.Status == DocumentStatus.PartiallyPaid)
                && x.Bill.DueDate < today);
        }

        var total = await query.CountAsync(ct);
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 200);

        var rows = await query
            .OrderByDescending(x => x.Bill.IssueDate)
            .ThenByDescending(x => x.Bill.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        var items = rows.Select(x => new BillListItem(x.Bill.Id, x.Bill.Number, x.Bill.VendorInvoiceNumber,
            x.Bill.PartyId, x.PartyName, x.Bill.IssueDate, x.Bill.DueDate, x.Bill.CurrencyCode, x.Bill.Total,
            x.Bill.BalanceDue, x.Bill.Status)).ToList();

        return new PagedResult<BillListItem>(items, total, page, size);
    }

    public async Task<Bill?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default) =>
        await _db.Bills
            .Include(b => b.Party)
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .Include(b => b.Lines).ThenInclude(l => l.TaxCode)
            .FirstOrDefaultAsync(b => b.Id == id && b.CompanyId == companyId, ct);

    public async Task<Bill> CreateDraftAsync(Guid companyId, BillEditModel model, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManagePurchases);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var bill = new Bill
        {
            CompanyId = companyId,
            Status = DocumentStatus.Draft,
            CreatedAt = _clock.UtcNow,
            CreatedByUserId = _user.UserId,
            VendorInvoiceNumber = model.VendorInvoiceNumber
        };

        await ApplyModelAsync(bill, model, ct);
        _db.Bills.Add(bill);
        _audit.Record(companyId, "BillDrafted", nameof(Bill), bill.Id.ToString(), model.VendorInvoiceNumber);
        await _db.SaveChangesAsync(ct);
        return bill;
    }

    public async Task<Bill> UpdateDraftAsync(Guid companyId, Guid id, BillEditModel model,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManagePurchases);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var bill = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Bill not found.");
        if (!bill.IsEditable)
            throw new DomainValidationException("Only draft bills can be edited.");

        _db.BillLines.RemoveRange(bill.Lines);
        bill.Lines.Clear();
        bill.Number = string.Empty;
        bill.VendorInvoiceNumber = model.VendorInvoiceNumber;
        await ApplyModelAsync(bill, model, ct);

        _audit.Record(companyId, "BillUpdated", nameof(Bill), bill.Id.ToString());
        await _db.SaveChangesAsync(ct);
        return bill;
    }

    public async Task<Bill> ReceiveAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManagePurchases);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var bill = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Bill not found.");
        if (bill.Status != DocumentStatus.Draft)
            throw new DomainValidationException("Only draft bills can be received.");
        if (!bill.Party!.IsVendor)
            throw new DomainValidationException("The selected party is not a vendor.");

        bill.Recalculate();
        var errors = bill.Validate();
        if (errors.Count > 0) throw new DomainValidationException(errors);

        var settings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (string.IsNullOrWhiteSpace(bill.Number))
            bill.Number = await _numbers.NextAsync(companyId, "bill", settings?.BillPrefix ?? "BILL", ct);

        var payable = await _accounts.PayableAsync(companyId, ct);
        var entry = BuildJournal(bill, payable.Id);
        await _poster.PostAsync(entry, ct);

        bill.Status = DocumentStatus.Issued;
        bill.IssuedAt = _clock.UtcNow;
        bill.IssuedByUserId = _user.UserId;
        bill.JournalEntryId = entry.Id;

        _audit.Record(companyId, "BillReceived", nameof(Bill), bill.Id.ToString(),
            $"{bill.Number} {bill.Total:N2} {bill.CurrencyCode}");
        await _db.SaveChangesAsync(ct);
        return bill;
    }

    public async Task VoidAsync(Guid companyId, Guid id, string reason, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManagePurchases);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var bill = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Bill not found.");
        if (bill.Status == DocumentStatus.Void) return;
        if (bill.AmountPaid > 0.005m)
            throw new DomainValidationException("Payments are allocated to this bill. Reverse them first.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainValidationException("Give a reason for voiding the bill.");

        if (bill.JournalEntryId is { } journalId)
        {
            var original = await _db.JournalEntries.Include(j => j.Lines)
                .FirstOrDefaultAsync(j => j.Id == journalId, ct);
            if (original is { Status: JournalStatus.Posted })
            {
                var settings = await _db.CompanySettings.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
                var number = await _numbers.NextAsync(companyId, "journal", settings?.JournalPrefix ?? "JE", ct);
                var reversal = original.CreateReversal(_clock.Today, number, _user.UserId ?? "system",
                    $"Void of {bill.Number}: {reason}");
                reversal.Source = JournalSource.PurchaseBill;
                await _poster.PostAsync(reversal, ct);
                _db.JournalEntries.Add(reversal);
                original.Status = JournalStatus.Reversed;
                original.ReversedById = reversal.Id;
            }
        }

        bill.Status = DocumentStatus.Void;
        bill.VoidedAt = _clock.UtcNow;
        bill.VoidReason = reason.Trim();

        _audit.Record(companyId, "BillVoided", nameof(Bill), bill.Id.ToString(), reason);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OpenDocument>> GetOpenBillsAsync(Guid companyId, Guid partyId,
        CancellationToken ct = default)
    {
        var today = _clock.Today;
        var bills = await _db.Bills.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.PartyId == partyId &&
                        (b.Status == DocumentStatus.Issued || b.Status == DocumentStatus.PartiallyPaid))
            .OrderBy(b => b.DueDate)
            .ToListAsync(ct);

        return bills
            .Where(b => b.BalanceDue > 0.005m)
            .Select(b => new OpenDocument(b.Id, b.Number, b.IssueDate, b.DueDate, b.Total, b.BalanceDue,
                b.CurrencyCode, (today.DayNumber - b.DueDate.DayNumber) is var d && d > 0 ? d : 0))
            .ToList();
    }

    private async Task ApplyModelAsync(Bill bill, BillEditModel model, CancellationToken ct)
    {
        var party = await _db.Parties.FirstOrDefaultAsync(p => p.Id == model.PartyId && p.CompanyId == bill.CompanyId, ct)
            ?? throw new DomainValidationException("Select a valid vendor.");
        if (!party.IsActive)
            throw new DomainValidationException("The selected vendor is inactive.");

        var baseCurrency = await _db.Companies.Where(c => c.Id == bill.CompanyId)
            .Select(c => c.BaseCurrencyCode).FirstOrDefaultAsync(ct) ?? "USD";
        var currency = string.IsNullOrWhiteSpace(model.CurrencyCode)
            ? baseCurrency
            : model.CurrencyCode.Trim().ToUpperInvariant();

        bill.PartyId = party.Id;
        bill.IssueDate = model.IssueDate;
        bill.DueDate = model.DueDate;
        bill.CurrencyCode = currency;
        bill.ExchangeRate = currency == baseCurrency
            ? 1m
            : model.ExchangeRate > 0
                ? model.ExchangeRate
                : await _reference.ResolveExchangeRateAsync(bill.CompanyId, currency, model.IssueDate, ct);
        bill.Notes = model.Notes;

        var taxCodes = await _db.TaxCodes.Where(t => t.CompanyId == bill.CompanyId)
            .ToDictionaryAsync(t => t.Id, ct);

        var errors = new List<string>();
        var lineNumber = 1;
        foreach (var input in model.Lines)
        {
            taxCodes.TryGetValue(input.TaxCodeId ?? Guid.Empty, out var tax);
            bill.Lines.Add(new BillLine
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
                TaxAccountId = tax?.InputTaxAccountId ?? tax?.TaxAccountId,
                DepartmentId = input.DepartmentId
            });

            var account = await _db.Accounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == input.AccountId && a.CompanyId == bill.CompanyId, ct);
            if (account is null) errors.Add($"Line {lineNumber - 1}: the expense account does not exist.");
            else if (!account.IsPostable || !account.IsActive)
                errors.Add($"Line {lineNumber - 1}: account {account.Code} cannot be posted to.");
            else if (account.Type is not (AccountType.Expense or AccountType.Asset))
                errors.Add($"Line {lineNumber - 1}: account {account.Code} is not an expense or asset account.");
        }

        bill.Recalculate();
        errors.AddRange(bill.Validate());
        if (errors.Count > 0) throw new DomainValidationException(errors);
    }

    private JournalEntry BuildJournal(Bill bill, Guid payableAccountId)
    {
        var entry = new JournalEntry
        {
            CompanyId = bill.CompanyId,
            EntryDate = bill.IssueDate,
            Reference = bill.Number,
            Memo = $"Bill {bill.Number} \u2013 {bill.Party?.Name}",
            Source = JournalSource.PurchaseBill,
            Status = JournalStatus.Draft,
            CreatedByUserId = _user.UserId,
            CreatedAt = _clock.UtcNow,
            CurrencyCode = bill.CurrencyCode,
            ExchangeRate = bill.ExchangeRate
        };

        var rate = bill.ExchangeRate;
        var total = 0m;
        foreach (var line in bill.Lines)
        {
            var net = Money.Round(line.NetAmount * rate);
            var tax = Money.Round(line.TaxAmount * rate);
            total += net + tax;

            entry.Lines.Add(new JournalLine
            {
                CompanyId = bill.CompanyId,
                AccountId = line.AccountId,
                Description = line.Description,
                Debit = net,
                Credit = 0m,
                DepartmentId = line.DepartmentId,
                PartyId = bill.PartyId,
                CurrencyCode = bill.CurrencyCode,
                ForeignAmount = line.NetAmount,
                ExchangeRate = rate,
                LineNumber = entry.Lines.Count + 1
            });

            if (tax != 0m && line.TaxAccountId.HasValue)
            {
                entry.Lines.Add(new JournalLine
                {
                    CompanyId = bill.CompanyId,
                    AccountId = line.TaxAccountId.Value,
                    Description = $"Input tax {line.TaxRate:0.##}% \u2013 {line.Description}",
                    Debit = tax,
                    Credit = 0m,
                    PartyId = bill.PartyId,
                    CurrencyCode = bill.CurrencyCode,
                    ForeignAmount = line.TaxAmount,
                    ExchangeRate = rate,
                    LineNumber = entry.Lines.Count + 1
                });
            }
        }

        entry.Lines.Add(new JournalLine
        {
            CompanyId = bill.CompanyId,
            AccountId = payableAccountId,
            Description = $"{bill.Party?.Name} \u2013 {bill.Number}",
            Debit = 0m,
            Credit = total,
            PartyId = bill.PartyId,
            CurrencyCode = bill.CurrencyCode,
            ForeignAmount = bill.Total,
            ExchangeRate = rate,
            LineNumber = entry.Lines.Count + 1
        });

        entry.RecalculateTotals();
        return entry;
    }

}
