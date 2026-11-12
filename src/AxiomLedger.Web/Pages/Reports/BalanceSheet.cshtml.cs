using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Reports;

public sealed class BalanceSheetModel : AppPageModel
{
    private readonly ReportingService _reports;
    private readonly CompanyService _companies;
    private readonly IClock _clock;

    public BalanceSheetModel(ReportingService reports, CompanyService companies, IClock clock)
    {
        _reports = reports;
        _companies = companies;
        _clock = clock;
    }

    public BalanceSheetReport Report { get; private set; } =
        new(default, Array.Empty<StatementSection>(), 0m, 0m, 0m, 0m, 0m, false);

    public string CompanyName { get; private set; } = string.Empty;
    public string CurrencyCode { get; private set; } = "USD";

    public async Task<IActionResult> OnGetAsync(DateOnly? asOf)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewReports)) return ForbiddenResult();

        Report = await _reports.BalanceSheetAsync(CompanyId.Value, asOf ?? _clock.Today);
        var company = await _companies.GetAsync(CompanyId.Value);
        CompanyName = company?.Name ?? "Company";
        CurrencyCode = company?.BaseCurrencyCode ?? "USD";
        return Page();
    }
}
