using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

public sealed class JournalService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly IJournalPoster _poster;
    private readonly NumberSequenceService _numbers;

    public JournalService(IAccountingDbContext db, ICurrentUser user, IClock clock, AuditService audit,
        IJournalPoster poster, NumberSequenceService numbers)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = audit;
        _poster = poster;
        _numbers = numbers;
    }

    public async Task<PagedResult<JournalListItem>> ListAsync(Guid companyId, JournalFilter filter,
        CancellationToken ct = default)
    {
        var query = _db.JournalEntries.AsNoTracking().Where(j => j.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(j => j.Number.Contains(term) || j.Memo.Contains(term) ||
                                     (j.Reference != null && j.Reference.Contains(term)));
        }
        if (filter.Status.HasValue) query = query.Where(j => j.Status == filter.Status);
        if (filter.Source.HasValue) query = query.Where(j => j.Source == filter.Source);
        if (filter.From.HasValue) query = query.Where(j => j.EntryDate >= filter.From);
        if (filter.To.HasValue) query = query.Where(j => j.EntryDate <= filter.To);
        if (filter.AccountId.HasValue)
            query = query.Where(j => j.Lines.Any(l => l.AccountId == filter.AccountId));

        var total = await query.CountAsync(ct);
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 200);

        var entries = await query
            .OrderByDescending(j => j.EntryDate)
            .ThenByDescending(j => j.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var userIds = entries.Select(e => e.CreatedByUserId).Concat(entries.Select(e => e.PostedByUserId))
            .Where(id => Guid.TryParse(id, out _)).Select(id => Guid.Parse(id!)).Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id.ToString(), u => u.Email, ct);

        var items = entries.Select(e => new JournalListItem(e.Id, e.Number, e.EntryDate, e.Memo, e.Reference,
            e.Source, e.Status, e.TotalDebit,
            e.CreatedByUserId != null && users.TryGetValue(e.CreatedByUserId, out var c) ? c : null,
            e.PostedByUserId != null && users.TryGetValue(e.PostedByUserId, out var p) ? p : null,
            e.PostedAt)).ToList();

        return new PagedResult<JournalListItem>(items, total, page, size);
    }

    public async Task<JournalEntry?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default) =>
        await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(j => j.Id == id && j.CompanyId == companyId, ct);

    public async Task<IReadOnlyList<JournalLine>> GetLinesAsync(Guid entryId, CancellationToken ct = default) =>
        await _db.JournalLines
            .Include(l => l.Account)
            .Where(l => l.JournalEntryId == entryId)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(ct);

    public async Task<JournalEntry> CreateDraftAsync(Guid companyId, JournalDraftRequest request,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.CreateJournal);
        if (request.Lines.Count == 0)
            throw new DomainValidationException("Add at least two journal lines.");

        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var settings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryDate = request.EntryDate,
            Memo = request.Memo?.Trim() ?? string.Empty,
            Reference = request.Reference,
            Source = JournalSource.Manual,
            Status = JournalStatus.Draft,
            CreatedByUserId = _user.UserId,
            CreatedAt = _clock.UtcNow,
            CurrencyCode = request.CurrencyCode,
            ExchangeRate = request.ExchangeRate
        };

        ApplyLines(entry, request.Lines);
        entry.RecalculateTotals();
        entry.EnsureValid();
        await EnsureAccountsPostableAsync(companyId, entry, ct);

        entry.Number = await _numbers.NextAsync(companyId, "journal", settings?.JournalPrefix ?? "JE", ct);
        _db.JournalEntries.Add(entry);

        _audit.Record(companyId, "JournalDrafted", nameof(JournalEntry), entry.Id.ToString(),
            $"{entry.Number} {entry.Memo}");
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<JournalEntry> UpdateDraftAsync(Guid companyId, Guid id, JournalDraftRequest request,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.CreateJournal);
        var entry = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Journal not found.");

        if (!entry.IsEditable)
            throw new DomainValidationException("Only draft or pending journals can be edited.");
        if (entry.Status == JournalStatus.PendingApproval)
            throw new DomainValidationException("Withdraw the journal from approval before editing it.");

        entry.EntryDate = request.EntryDate;
        entry.Memo = request.Memo?.Trim() ?? string.Empty;
        entry.Reference = request.Reference;
        entry.CurrencyCode = request.CurrencyCode;
        entry.ExchangeRate = request.ExchangeRate;

        _db.JournalLines.RemoveRange(entry.Lines);
        entry.Lines.Clear();
        ApplyLines(entry, request.Lines);
        entry.RecalculateTotals();
        entry.EnsureValid();
        await EnsureAccountsPostableAsync(companyId, entry, ct);

        _audit.Record(companyId, "JournalUpdated", nameof(JournalEntry), entry.Id.ToString(), entry.Number);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task SubmitForApprovalAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.CreateJournal);
        var entry = await _db.JournalEntries.FirstOrDefaultAsync(j => j.Id == id && j.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Journal not found.");
        if (entry.Status != JournalStatus.Draft)
            throw new DomainValidationException("Only draft journals can be submitted for approval.");
        if (string.IsNullOrEmpty(entry.CreatedByUserId)) entry.CreatedByUserId = _user.UserId;

        entry.Status = JournalStatus.PendingApproval;
        _audit.Record(companyId, "JournalSubmitted", nameof(JournalEntry), entry.Id.ToString(), entry.Number);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ApproveAndPostAsync(Guid companyId, Guid id, string? note = null,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ApproveJournal);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);
        var entry = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Journal not found.");
        if (entry.Status != JournalStatus.PendingApproval)
            throw new DomainValidationException("This journal is not awaiting approval.");

        entry.ApprovalNote = note;
        await _poster.PostAsync(entry, ct);
        _audit.Record(companyId, "JournalApproved", nameof(JournalEntry), entry.Id.ToString(), entry.Number);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RejectAsync(Guid companyId, Guid id, string reason, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ApproveJournal);
        var entry = await _db.JournalEntries.FirstOrDefaultAsync(j => j.Id == id && j.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Journal not found.");
        if (entry.Status != JournalStatus.PendingApproval)
            throw new DomainValidationException("This journal is not awaiting approval.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainValidationException("Give a reason for rejecting the journal.");

        entry.Status = JournalStatus.Draft;
        entry.ApprovalNote = "Rejected: " + reason.Trim();
        _audit.Record(companyId, "JournalRejected", nameof(JournalEntry), entry.Id.ToString(), reason);
        await _db.SaveChangesAsync(ct);
    }

    public async Task PostAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.PostJournal);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);
        var entry = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Journal not found.");
        await _poster.PostAsync(entry, ct);
        _audit.Record(companyId, "JournalPosted", nameof(JournalEntry), entry.Id.ToString(), entry.Number);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<JournalEntry> ReverseAsync(Guid companyId, Guid id, DateOnly reversalDate, string? memo,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.PostJournal);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var original = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Journal not found.");
        if (original.Status != JournalStatus.Posted)
            throw new DomainValidationException("Only posted journals can be reversed.");
        if (original.ReversedById.HasValue)
            throw new DomainValidationException("This journal has already been reversed.");

        var settings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        var number = await _numbers.NextAsync(companyId, "journal", settings?.JournalPrefix ?? "JE", ct);
        var reversal = original.CreateReversal(reversalDate, number, _user.UserId ?? "system", memo);
        reversal.CreatedAt = _clock.UtcNow;

        await _poster.PostAsync(reversal, ct);
        _db.JournalEntries.Add(reversal);

        // The original stays Posted: the reversal is a second posted entry that nets it to zero, so reports
        // that select Posted entries still tell the complete story. ReversedById marks it as corrected.
        original.ReversedById = reversal.Id;

        _audit.Record(companyId, "JournalReversed", nameof(JournalEntry), original.Id.ToString(),
            $"{original.Number} reversed by {reversal.Number}");
        await _db.SaveChangesAsync(ct);
        return reversal;
    }

    public async Task DeleteDraftAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.CreateJournal);
        var entry = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Journal not found.");
        if (entry.Status != JournalStatus.Draft)
            throw new DomainValidationException("Only draft journals can be deleted. Reverse posted entries instead.");

        _db.JournalLines.RemoveRange(entry.Lines);
        _db.JournalEntries.Remove(entry);
        _audit.Record(companyId, "JournalDeleted", nameof(JournalEntry), entry.Id.ToString(), entry.Number);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> CountPendingApprovalsAsync(Guid companyId, CancellationToken ct = default) =>
        await _db.JournalEntries.CountAsync(j =>
            j.CompanyId == companyId && j.Status == JournalStatus.PendingApproval, ct);

    private static void ApplyLines(JournalEntry entry, IEnumerable<JournalLineInput> inputs)
    {
        var number = 1;
        foreach (var input in inputs)
        {
            entry.Lines.Add(new JournalLine
            {
                CompanyId = entry.CompanyId,
                AccountId = input.AccountId,
                Description = input.Description,
                Debit = Money.Round(input.Debit),
                Credit = Money.Round(input.Credit),
                LineNumber = number++,
                DepartmentId = input.DepartmentId,
                PartyId = input.PartyId,
                TaxCodeId = input.TaxCodeId
            });
        }
    }

    private async Task EnsureAccountsPostableAsync(Guid companyId, JournalEntry entry, CancellationToken ct)
    {
        var ids = entry.Lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await _db.Accounts
            .Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
        var errors = new List<string>();
        foreach (var line in entry.Lines)
        {
            if (!accounts.TryGetValue(line.AccountId, out var account))
                errors.Add("One or more lines reference an account that does not belong to this company.");
            else if (!account.IsPostable)
                errors.Add($"Account {account.Code} {account.Name} is a summary account.");
            else if (!account.IsActive)
                errors.Add($"Account {account.Code} {account.Name} is inactive.");
        }
        if (errors.Count > 0) throw new DomainValidationException(errors);
    }
}
