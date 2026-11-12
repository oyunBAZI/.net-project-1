using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>Pre-close checks and the year-end closing entry that rolls profit into retained earnings.</summary>
public sealed class YearEndService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly FiscalCalendarService _calendar;
    private readonly IJournalPoster _poster;
    private readonly NumberSequenceService _numbers;
    private readonly LedgerAccountResolver _accounts;

    public YearEndService(IAccountingDbContext db, ICurrentUser user, IClock clock, AuditService audit,
        FiscalCalendarService calendar, IJournalPoster poster, NumberSequenceService numbers,
        LedgerAccountResolver accounts)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = audit;
        _calendar = calendar;
        _poster = poster;
        _numbers = numbers;
        _accounts = accounts;
    }

    public async Task<IReadOnlyList<PreCloseCheck>> ChecklistAsync(Guid companyId, int year,
        CancellationToken ct = default)
    {
        var fiscalYear = await _calendar.FindYearAsync(companyId, year, ct)
            ?? throw new NotFoundException($"Fiscal year {year} has not been created.");

        var checks = new List<PreCloseCheck>();

        var drafts = await _db.JournalEntries.CountAsync(j =>
            j.CompanyId == companyId && j.Status == JournalStatus.Draft &&
            j.EntryDate >= fiscalYear.StartDate && j.EntryDate <= fiscalYear.EndDate, ct);
        checks.Add(new PreCloseCheck("Draft journals", drafts == 0 ? "OK" : "Action",
            drafts == 0 ? "No draft journals in the year." : $"{drafts} draft journal(s) must be posted or deleted.",
            drafts > 0));

        var pending = await _db.JournalEntries.CountAsync(j =>
            j.CompanyId == companyId && j.Status == JournalStatus.PendingApproval &&
            j.EntryDate >= fiscalYear.StartDate && j.EntryDate <= fiscalYear.EndDate, ct);
        checks.Add(new PreCloseCheck("Pending approvals", pending == 0 ? "OK" : "Action",
            pending == 0 ? "Nothing waiting for approval." : $"{pending} journal(s) await approval.", pending > 0));

        var periods = await _db.AccountingPeriods.CountAsync(p =>
            p.CompanyId == companyId && p.FiscalYearId == fiscalYear.Id, ct);
        var closed = await _db.AccountingPeriods.CountAsync(p =>
            p.CompanyId == companyId && p.FiscalYearId == fiscalYear.Id && p.Status == PeriodStatus.Closed, ct);
        checks.Add(new PreCloseCheck("Periods closed", closed == periods ? "OK" : "Info",
            $"{closed} of {periods} periods closed.", false));

        var unreconciled = await _db.BankStatementLines.CountAsync(l =>
            l.CompanyId == companyId && l.Status == BankLineStatus.Unmatched &&
            l.TransactionDate >= fiscalYear.StartDate && l.TransactionDate <= fiscalYear.EndDate, ct);
        checks.Add(new PreCloseCheck("Bank reconciliation", unreconciled == 0 ? "OK" : "Warn",
            unreconciled == 0 ? "All statement lines are matched." :
            $"{unreconciled} bank line(s) are unreconciled.", false));

        var imbalance = (decimal)(await (from l in _db.JournalLines
                               join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                               where j.CompanyId == companyId && j.Status == JournalStatus.Posted &&
                                     j.EntryDate <= fiscalYear.EndDate
                               select (double?)(l.Debit - l.Credit)).SumAsync(ct) ?? 0d);
        checks.Add(new PreCloseCheck("Trial balance", Money.IsZero(imbalance) ? "OK" : "Action",
            Money.IsZero(imbalance) ? "The ledger balances." : $"Ledger is out of balance by {imbalance:N2}.",
            !Money.IsZero(imbalance)));

        var cashBalances = await (from l in _db.JournalLines
                                  join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                                  join a in _db.Accounts on l.AccountId equals a.Id
                                  where j.CompanyId == companyId && j.Status == JournalStatus.Posted &&
                                        j.EntryDate <= fiscalYear.EndDate && a.IsCashAccount
                                  group new { l.Debit, l.Credit } by a.Id into g
                                  select new { Balance = g.Sum(x => (double)(x.Debit - x.Credit)) }).ToListAsync(ct);
        var negative = cashBalances.Count(c => (decimal)c.Balance < -0.005m);
        checks.Add(new PreCloseCheck("Cash balances", negative == 0 ? "OK" : "Warn",
            negative == 0 ? "No cash account is overdrawn." : $"{negative} cash account(s) have a credit balance.",
            false));

        var openInvoices = await _db.SalesInvoices.CountAsync(i =>
            i.CompanyId == companyId &&
            (i.Status == DocumentStatus.Issued || i.Status == DocumentStatus.PartiallyPaid), ct);
        checks.Add(new PreCloseCheck("Open receivables", "Info",
            $"{openInvoices} customer invoice(s) still open at year end.", false));

        return checks;
    }

    public async Task<FiscalYear> CloseFiscalYearAsync(Guid companyId, int year, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ClosePeriods);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var fiscalYear = await _calendar.FindYearAsync(companyId, year, ct)
            ?? throw new NotFoundException($"Fiscal year {year} has not been created.");
        if (fiscalYear.Status == FiscalYearStatus.Closed)
            throw new DomainValidationException($"Fiscal year {year} is already closed.");

        var checks = await ChecklistAsync(companyId, year, ct);
        var blocking = checks.Where(c => c.IsBlocking && c.Status != "OK").ToList();
        if (blocking.Count > 0)
            throw new DomainValidationException(blocking.Select(b => b.Detail));

        var balances = await (from l in _db.JournalLines
                              join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                              join a in _db.Accounts on l.AccountId equals a.Id
                              where j.CompanyId == companyId && j.Status == JournalStatus.Posted &&
                                    j.EntryDate >= fiscalYear.StartDate && j.EntryDate <= fiscalYear.EndDate &&
                                    (a.Type == AccountType.Revenue || a.Type == AccountType.Expense)
                              group new { l.Debit, l.Credit } by new { l.AccountId, a.Type } into g
                              select new
                              {
                                  g.Key.AccountId,
                                  g.Key.Type,
                                  Debit = g.Sum(x => (double)x.Debit),
                                  Credit = g.Sum(x => (double)x.Credit)
                              }).ToListAsync(ct);

        var retained = await _accounts.RetainedEarningsAsync(companyId, ct);
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);

        if (balances.Count > 0)
        {
            var entry = new JournalEntry
            {
                CompanyId = companyId,
                EntryDate = fiscalYear.EndDate,
                Reference = $"FY{year}",
                Memo = $"Year-end close {year}: profit transferred to retained earnings",
                Source = JournalSource.YearEndClose,
                CreatedByUserId = _user.UserId,
                CreatedAt = _clock.UtcNow
            };

            foreach (var balance in balances)
            {
                var signed = (decimal)balance.Debit - (decimal)balance.Credit;
                if (Money.IsZero(signed)) continue;
                entry.Lines.Add(new JournalLine
                {
                    CompanyId = companyId,
                    AccountId = balance.AccountId,
                    Description = $"Close {year}",
                    Debit = signed < 0 ? -signed : 0m,
                    Credit = signed > 0 ? signed : 0m,
                    LineNumber = entry.Lines.Count + 1
                });
            }

            var debits = entry.Lines.Sum(l => l.Debit);
            var credits = entry.Lines.Sum(l => l.Credit);
            var difference = Money.Round(debits - credits);
            if (!Money.IsZero(difference))
            {
                entry.Lines.Add(new JournalLine
                {
                    CompanyId = companyId,
                    AccountId = retained.Id,
                    Description = difference > 0 ? "Profit for the year" : "Loss for the year",
                    Debit = difference < 0 ? -difference : 0m,
                    Credit = difference > 0 ? difference : 0m,
                    LineNumber = entry.Lines.Count + 1
                });
            }

            if (entry.Lines.Count >= 2)
            {
                if (string.IsNullOrWhiteSpace(entry.Number))
                    entry.Number = await _numbers.NextAsync(companyId, "journal",
                        settings?.JournalPrefix ?? "JE", ct);
                await _poster.PostAsync(entry, ct);
                _db.JournalEntries.Add(entry);
            }
        }

        var periods = await _db.AccountingPeriods
            .Where(p => p.CompanyId == companyId && p.FiscalYearId == fiscalYear.Id)
            .ToListAsync(ct);
        foreach (var period in periods)
        {
            period.Status = PeriodStatus.Closed;
            period.ClosedAt ??= _clock.UtcNow;
            period.ClosedByUserId ??= _user.UserId;
        }

        fiscalYear.Status = FiscalYearStatus.Closed;
        fiscalYear.ClosedAt = _clock.UtcNow;
        fiscalYear.ClosedByUserId = _user.UserId;

        _audit.Record(companyId, "FiscalYearClosed", nameof(FiscalYear), fiscalYear.Id.ToString(), year.ToString());
        await _db.SaveChangesAsync(ct);
        return fiscalYear;
    }

    public async Task ReopenFiscalYearAsync(Guid companyId, int year, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageCompanies);
        var fiscalYear = await _calendar.FindYearAsync(companyId, year, ct)
            ?? throw new NotFoundException($"Fiscal year {year} was not found.");

        fiscalYear.Status = FiscalYearStatus.Open;
        fiscalYear.ClosedAt = null;
        fiscalYear.ClosedByUserId = null;

        var periods = await _db.AccountingPeriods
            .Where(p => p.CompanyId == companyId && p.FiscalYearId == fiscalYear.Id)
            .ToListAsync(ct);
        foreach (var period in periods)
        {
            period.Status = PeriodStatus.Open;
            period.ClosedAt = null;
            period.ClosedByUserId = null;
        }

        _audit.Record(companyId, "FiscalYearReopened", nameof(FiscalYear), fiscalYear.Id.ToString(), year.ToString());
        await _db.SaveChangesAsync(ct);
    }
}
