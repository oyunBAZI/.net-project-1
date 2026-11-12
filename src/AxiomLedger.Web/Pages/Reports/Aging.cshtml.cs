using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Reports;

public sealed class AgingModel : AppPageModel
{
    public enum AgingKind { Receivable, Payable }

    private readonly ReportingService _reports;
    private readonly CompanyService _companies;
    private readonly IClock _clock;

    public AgingModel(ReportingService reports, CompanyService companies, IClock clock)
    {
        _reports = reports;
        _companies = companies;
        _clock = clock;
    }

    public AgingKind Kind { get; private set; } = AgingKind.Receivable;
    public AgingReport Report { get; private set; } =
        new(default, Array.Empty<AgingRow>(), 0m, 0m, 0m, 0m, 0m, 0m);
    public string CompanyName { get; private set; } = string.Empty;
    public string CurrencyCode { get; private set; } = "USD";

    public async Task<IActionResult> OnGetAsync(string? kind, DateOnly? asOf)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewReports)) return ForbiddenResult();

        Kind = kind?.Equals("payable", StringComparison.OrdinalIgnoreCase) == true
            ? AgingKind.Payable
            : AgingKind.Receivable;

        var asOfDate = asOf ?? _clock.Today;
        Report = Kind == AgingKind.Receivable
            ? await _reports.ReceivablesAgingAsync(CompanyId.Value, asOfDate)
            : await _reports.PayablesAgingAsync(CompanyId.Value, asOfDate);

        var company = await _companies.GetAsync(CompanyId.Value);
        CompanyName = company?.Name ?? "Company";
        CurrencyCode = company?.BaseCurrencyCode ?? "USD";
        return Page();
    }
}
