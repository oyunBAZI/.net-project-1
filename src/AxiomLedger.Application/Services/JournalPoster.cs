using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>
/// The single gateway through which every journal reaches the ledger. It enforces balance, account
/// postability, period state, the hard lock date and the approval workflow before an entry becomes posted.
/// </summary>
public interface IJournalPoster
{
    Task<JournalEntry> PostAsync(JournalEntry entry, CancellationToken ct = default);
}

public sealed class JournalPoster : IJournalPoster
{
    private readonly IAccountingDbContext _db;
    private readonly FiscalCalendarService _calendar;
    private readonly NumberSequenceService _numbers;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public JournalPoster(IAccountingDbContext db, FiscalCalendarService calendar, NumberSequenceService numbers,
        ICurrentUser user, IClock clock)
    {
        _db = db;
        _calendar = calendar;
        _numbers = numbers;
        _user = user;
        _clock = clock;
    }

    public async Task<JournalEntry> PostAsync(JournalEntry entry, CancellationToken ct = default)
    {
        if (entry.Status == JournalStatus.Posted) return entry;
        if (entry.Status is JournalStatus.Reversed or JournalStatus.Void)
            throw new DomainValidationException("This journal has already been reversed or voided.");

        foreach (var line in entry.Lines) line.CompanyId = entry.CompanyId;
        entry.RecalculateTotals();
        entry.EnsureValid();

        if (string.IsNullOrWhiteSpace(entry.Memo))
            throw new DomainValidationException("A journal memo is required.");

        var accountIds = entry.Lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await _db.Accounts
            .Where(a => a.CompanyId == entry.CompanyId && accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        if (accounts.Count != accountIds.Count)
            throw new DomainValidationException("One or more journal accounts no longer exist in this company.");

        var accountErrors = new List<string>();
        foreach (var line in entry.Lines)
        {
            var account = accounts[line.AccountId];
            if (!account.IsPostable)
                accountErrors.Add($"Account {account.Code} {account.Name} is a summary account and cannot be posted to.");
            if (!account.IsActive)
                accountErrors.Add($"Account {account.Code} {account.Name} is inactive.");
        }
        if (accountErrors.Count > 0) throw new DomainValidationException(accountErrors);

        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == entry.CompanyId, ct)
            ?? new CompanySettings { CompanyId = entry.CompanyId };

        if (settings.LockDate.HasValue && entry.EntryDate <= settings.LockDate.Value)
            throw new DomainValidationException(
                $"The books are locked through {settings.LockDate.Value:yyyy-MM-dd}. This entry cannot be posted.");

        var period = await _calendar.GetOrCreatePeriodAsync(entry.CompanyId, entry.EntryDate, ct);
        if (period.Status == PeriodStatus.Closed)
            throw new DomainValidationException(
                $"{period.StartDate:MMMM yyyy} is closed. Reopen the period or choose another date.");
        if (period.Status == PeriodStatus.SoftClosed && !PermissionGuard.Allows(_user, Permission.ClosePeriods))
            throw new DomainValidationException(
                $"{period.StartDate:MMMM yyyy} is soft-closed. Ask an owner or accountant to post this adjustment.");

        if (entry.Status == JournalStatus.PendingApproval)
        {
            PermissionGuard.Demand(_user, Permission.ApproveJournal);
            if (settings.RequireSegregationOfDuties &&
                !string.IsNullOrEmpty(entry.CreatedByUserId) &&
                entry.CreatedByUserId == _user.UserId)
                throw new DomainValidationException(
                    "Segregation of duties: the preparer cannot approve their own journal.");
            entry.ApprovedByUserId = _user.UserId;
            entry.ApprovedAt = _clock.UtcNow;
        }
        else if (settings.RequireJournalApproval
                 && entry.Source == JournalSource.Manual
                 && entry.TotalDebit >= settings.ApprovalThreshold
                 && !PermissionGuard.Allows(_user, Permission.ApproveJournal))
        {
            throw new DomainValidationException(
                "This journal needs approval before posting. Submit it for approval instead.");
        }

        if (string.IsNullOrWhiteSpace(entry.Number))
            entry.Number = await _numbers.NextAsync(entry.CompanyId, "journal", settings.JournalPrefix, ct);

        entry.Status = JournalStatus.Posted;
        entry.PostedAt = _clock.UtcNow;
        entry.PostedByUserId ??= _user.UserId;
        entry.CurrencyCode ??= (await _db.Companies
            .Where(c => c.Id == entry.CompanyId).Select(c => c.BaseCurrencyCode).FirstOrDefaultAsync(ct)) ?? "USD";
        entry.ExchangeRate ??= 1m;

        if (_db.Entry(entry).State == EntityState.Detached)
            _db.JournalEntries.Add(entry);

        return entry;
    }
}
