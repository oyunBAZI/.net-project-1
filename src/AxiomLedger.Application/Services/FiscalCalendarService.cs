using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>Owns fiscal years and monthly accounting periods, including period locking.</summary>
public sealed class FiscalCalendarService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly AuditService _audit;

    public FiscalCalendarService(IAccountingDbContext db, ICurrentUser user, AuditService audit)
    {
        _db = db;
        _user = user;
        _audit = audit;
    }

    public static (DateOnly Start, DateOnly End, int Label) Bounds(DateOnly date, int fiscalStartMonth)
    {
        var month = Math.Clamp(fiscalStartMonth, 1, 12);
        var startThisYear = new DateOnly(date.Year, month, 1);
        var start = date >= startThisYear ? startThisYear : startThisYear.AddYears(-1);
        return (start, start.AddYears(1).AddDays(-1), start.Year);
    }

    public async Task<FiscalYear?> FindYearAsync(Guid companyId, int year, CancellationToken ct = default) =>
        await _db.FiscalYears.FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Year == year, ct);

    public async Task<AccountingPeriod?> FindPeriodAsync(Guid companyId, DateOnly date, CancellationToken ct = default) =>
        await _db.AccountingPeriods
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.StartDate <= date && p.EndDate >= date, ct);

    public async Task<FiscalYear> EnsureYearAsync(Guid companyId, DateOnly date, CancellationToken ct = default)
    {
        // FindAsync checks tracked entities first, so a company created earlier in the same unit of work
        // (its SaveChanges still pending) is found without a database round trip.
        var company = await _db.Companies.FindAsync(new object?[] { companyId }, ct)
            ?? throw new NotFoundException("Company not found.");

        var (start, end, label) = Bounds(date, company.FiscalYearStartMonth);
        var year = await _db.FiscalYears
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.StartDate == start, ct);

        if (year is not null) return year;

        year = new FiscalYear
        {
            CompanyId = companyId,
            Year = label,
            StartDate = start,
            EndDate = end,
            Status = FiscalYearStatus.Open
        };
        _db.FiscalYears.Add(year);
        AddPeriods(year);
        return year;
    }

    public async Task<AccountingPeriod> GetOrCreatePeriodAsync(Guid companyId, DateOnly date,
        CancellationToken ct = default)
    {
        var period = await FindPeriodAsync(companyId, date, ct);
        if (period is not null) return period;

        var year = await EnsureYearAsync(companyId, date, ct);

        // A year can exist with missing months (e.g. fiscal year start changed); fill the gaps.
        var existing = await _db.AccountingPeriods
            .Where(p => p.CompanyId == companyId && p.FiscalYearId == year.Id)
            .Select(p => p.Month)
            .ToListAsync(ct);
        var missing = Enumerable.Range(0, 12)
            .Select(i => year.StartDate.AddMonths(i))
            .Where(start => !existing.Contains(start.Month))
            .ToList();

        foreach (var start in missing)
        {
            _db.AccountingPeriods.Add(new AccountingPeriod
            {
                CompanyId = companyId,
                FiscalYearId = year.Id,
                Year = year.Year,
                Month = start.Month,
                StartDate = start,
                EndDate = start.AddMonths(1).AddDays(-1),
                Status = PeriodStatus.Open
            });
        }

        if (missing.Count > 0)
            return _db.AccountingPeriods.Local.First(p =>
                p.CompanyId == companyId && p.StartDate <= date && p.EndDate >= date);

        throw new DomainValidationException($"No accounting period covers {date:yyyy-MM-dd}.");
    }

    private void AddPeriods(FiscalYear year)
    {
        for (var i = 0; i < 12; i++)
        {
            var start = year.StartDate.AddMonths(i);
            _db.AccountingPeriods.Add(new AccountingPeriod
            {
                CompanyId = year.CompanyId,
                FiscalYearId = year.Id,
                Year = year.Year,
                Month = start.Month,
                StartDate = start,
                EndDate = start.AddMonths(1).AddDays(-1),
                Status = PeriodStatus.Open
            });
        }
    }

    public async Task<IReadOnlyList<PeriodView>> ListPeriodsAsync(Guid companyId, int year,
        CancellationToken ct = default)
    {
        var periods = await _db.AccountingPeriods
            .Where(p => p.CompanyId == companyId && p.Year == year)
            .OrderBy(p => p.StartDate)
            .ToListAsync(ct);

        return periods.Select(p => new PeriodView(p.Id, p.Year, p.Month,
            p.StartDate.ToString("MMMM yyyy"), p.StartDate, p.EndDate, p.Status, p.ClosedAt)).ToList();
    }

    public async Task ClosePeriodAsync(Guid companyId, Guid periodId, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ClosePeriods);
        var period = await _db.AccountingPeriods
            .FirstOrDefaultAsync(p => p.Id == periodId && p.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Accounting period not found.");

        if (period.Status == PeriodStatus.Closed) return;

        var blocking = await _db.JournalEntries.CountAsync(j =>
            j.CompanyId == companyId &&
            j.EntryDate >= period.StartDate && j.EntryDate <= period.EndDate &&
            (j.Status == JournalStatus.Draft || j.Status == JournalStatus.PendingApproval), ct);

        if (blocking > 0)
            throw new DomainValidationException(
                $"{blocking} unposted journal(s) fall inside {period.StartDate:MMMM yyyy}. Post or delete them first.");

        period.Status = PeriodStatus.Closed;
        period.ClosedAt = DateTime.UtcNow;
        period.ClosedByUserId = _user.UserId;
        _audit.Record(companyId, "PeriodClosed", nameof(AccountingPeriod), period.Id.ToString(),
            $"{period.StartDate:yyyy-MM} closed");
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReopenPeriodAsync(Guid companyId, Guid periodId, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageCompanies);
        var period = await _db.AccountingPeriods
            .FirstOrDefaultAsync(p => p.Id == periodId && p.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Accounting period not found.");

        var year = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == period.FiscalYearId, ct);
        if (year is { Status: FiscalYearStatus.Closed })
            throw new DomainValidationException("Reopen the fiscal year before reopening a period inside it.");

        period.Status = PeriodStatus.Open;
        period.ClosedAt = null;
        period.ClosedByUserId = null;
        _audit.Record(companyId, "PeriodReopened", nameof(AccountingPeriod), period.Id.ToString(),
            $"{period.StartDate:yyyy-MM} reopened");
        await _db.SaveChangesAsync(ct);
    }
}
