using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Reports;

public sealed class TaxModel : AppPageModel
{
    private readonly ReportingService _reports;
    private readonly CompanyService _companies;
    private readonly IClock _clock;

    public TaxModel(ReportingService reports, CompanyService companies, IClock clock)
    {
        _reports = reports;
        _companies = companies;
        _clock = clock;
    }

    public TaxReport Report { get; private set; } =
        new(default, default, Array.Empty<TaxSummaryRow>(), Array.Empty<TaxDetailRow>(), 0m, 0m, 0m);
    public string CompanyName { get; private set; } = string.Empty;
    public string CurrencyCode { get; private set; } = "USD";

    public async Task<IActionResult> OnGetAsync(DateOnly? from, DateOnly? to)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewReports)) return ForbiddenResult();

        var today = _clock.Today;
        var start = from ?? new DateOnly(today.Year, 1, 1);
        var end = to ?? today;

        Report = await _reports.TaxReportAsync(CompanyId.Value, start, end);
        var company = await _companies.GetAsync(CompanyId.Value);
        CompanyName = company?.Name ?? "Company";
        CurrencyCode = company?.BaseCurrencyCode ?? "USD";
        return Page();
    }
}
