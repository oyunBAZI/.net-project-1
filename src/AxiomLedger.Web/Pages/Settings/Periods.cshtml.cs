using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Web.Pages.Settings;

public sealed class PeriodsModel : AppPageModel
{
    private readonly FiscalCalendarService _calendar;
    private readonly YearEndService _yearEnd;
    private readonly IAccountingDbContext _db;
    private readonly IClock _clock;

    public PeriodsModel(FiscalCalendarService calendar, YearEndService yearEnd, IAccountingDbContext db,
        IClock clock)
    {
        _calendar = calendar;
        _yearEnd = yearEnd;
        _db = db;
        _clock = clock;
    }

    [BindProperty(SupportsGet = true)] public int? Year { get; set; }

    public IReadOnlyList<FiscalYear> Years { get; private set; } = Array.Empty<FiscalYear>();
    public FiscalYear? SelectedYear { get; private set; }
    public IReadOnlyList<PeriodView> Periods { get; private set; } = Array.Empty<PeriodView>();
    public IReadOnlyList<PreCloseCheck> Checklist { get; private set; } = Array.Empty<PreCloseCheck>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewReports)) return ForbiddenResult();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateNextAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ClosePeriods)) return ForbiddenResult();

        var latest = await _db.FiscalYears.Where(f => f.CompanyId == CompanyId)
            .OrderByDescending(f => f.StartDate).FirstOrDefaultAsync();
        var seedDate = latest?.EndDate.AddDays(1) ?? _clock.Today;

        if (!await TryAsync(async () =>
            {
                var year = await _calendar.EnsureYearAsync(CompanyId.Value, seedDate);
                Success($"Fiscal year {year.Year} is ready.");
            }))
        {
            return RedirectToPage();
        }
        return RedirectToPage(new { year = _clock.Today.Year });
    }

    public async Task<IActionResult> OnPostCloseAsync(Guid periodId)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _calendar.ClosePeriodAsync(CompanyId.Value, periodId)))
            return RedirectToPage();
        Success("Period closed.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReopenAsync(Guid periodId)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _calendar.ReopenPeriodAsync(CompanyId.Value, periodId)))
            return RedirectToPage();
        Success("Period reopened.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCloseYearAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        var year = Year ?? _clock.Today.Year;
        if (!await TryAsync(async () =>
            {
                await _yearEnd.CloseFiscalYearAsync(CompanyId.Value, year);
                Success($"Fiscal year {year} closed.");
            }))
        {
            return RedirectToPage(new { year });
        }
        return RedirectToPage(new { year });
    }

    public async Task<IActionResult> OnPostReopenYearAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        var year = Year ?? _clock.Today.Year;
        if (!await TryAsync(() => _yearEnd.ReopenFiscalYearAsync(CompanyId.Value, year)))
            return RedirectToPage(new { year });
        Success($"Fiscal year {year} reopened.");
        return RedirectToPage(new { year });
    }

    private async Task LoadAsync()
    {
        Years = await _db.FiscalYears.AsNoTracking()
            .Where(f => f.CompanyId == CompanyId)
            .OrderByDescending(f => f.StartDate)
            .ToListAsync();

        SelectedYear = Year is { } requested
            ? Years.FirstOrDefault(y => y.Year == requested)
            : Years.FirstOrDefault();
        if (SelectedYear is null && Years.Count > 0) SelectedYear = Years[0];

        Year = SelectedYear?.Year;
        if (SelectedYear is not null)
        {
            Periods = await _calendar.ListPeriodsAsync(CompanyId!.Value, SelectedYear.Year);
            Checklist = await _yearEnd.ChecklistAsync(CompanyId.Value, SelectedYear.Year);
        }
    }
}
