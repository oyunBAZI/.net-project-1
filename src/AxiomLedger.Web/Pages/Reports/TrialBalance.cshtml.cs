using AxiomLedger.Application;
using System.Text;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Reports;

public sealed class TrialBalanceModel : AppPageModel
{
    private readonly ReportingService _reports;
    private readonly CompanyService _companies;
    private readonly IClock _clock;

    public TrialBalanceModel(ReportingService reports, CompanyService companies, IClock clock)
    {
        _reports = reports;
        _companies = companies;
        _clock = clock;
    }

    public TrialBalanceReport Report { get; private set; } =
        new(default, default, Array.Empty<TrialBalanceRow>(), 0m, 0m, 0m, 0m);

    public string CompanyName { get; private set; } = string.Empty;
    public string CurrencyCode { get; private set; } = "USD";
    public bool IncludeZero { get; private set; }

    public async Task<IActionResult> OnGetAsync(DateOnly? from, DateOnly? to, bool includeZero = false)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewReports)) return ForbiddenResult();

        var today = _clock.Today;
        var start = from ?? new DateOnly(today.Year, 1, 1);
        var end = to ?? today;
        IncludeZero = includeZero;

        Report = await _reports.TrialBalanceAsync(CompanyId.Value, start, end, includeZero);
        await LoadCompanyAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync(DateOnly? from, DateOnly? to, bool includeZero = false)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");

        var today = _clock.Today;
        var start = from ?? new DateOnly(today.Year, 1, 1);
        var end = to ?? today;
        var report = await _reports.TrialBalanceAsync(CompanyId.Value, start, end, includeZero);

        var csv = CsvWriter.Write(
            new[] { "Code", "Account", "Type", "Opening", "Debit", "Credit", "Closing" },
            report.Rows.Select(r => new object?[]
            {
                r.Code, r.Name, r.Type.ToString(), r.OpeningBalance, r.Debit, r.Credit, r.ClosingBalance
            }).Append(new object?[]
            {
                "", "TOTAL", "", report.OpeningTotal, report.TotalDebit, report.TotalCredit, report.ClosingTotal
            }));

        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"trial-balance-{end:yyyy-MM-dd}.csv");
    }

    private async Task LoadCompanyAsync()
    {
        var company = await _companies.GetAsync(CompanyId!.Value);
        CompanyName = company?.Name ?? "Company";
        CurrencyCode = company?.BaseCurrencyCode ?? "USD";
    }
}
