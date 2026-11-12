using AxiomLedger.Application;
using System.Text;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Reports;

public sealed class ProfitLossModel : AppPageModel
{
    private readonly ReportingService _reports;
    private readonly CompanyService _companies;
    private readonly IClock _clock;

    public ProfitLossModel(ReportingService reports, CompanyService companies, IClock clock)
    {
        _reports = reports;
        _companies = companies;
        _clock = clock;
    }

    public ProfitAndLossReport Report { get; private set; } =
        new(default, default, Array.Empty<StatementSection>(), 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    public string CompanyName { get; private set; } = string.Empty;
    public string CurrencyCode { get; private set; } = "USD";
    public bool Compare { get; private set; }

    public async Task<IActionResult> OnGetAsync(DateOnly? from, DateOnly? to, bool compare = false)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewReports)) return ForbiddenResult();

        var today = _clock.Today;
        var start = from ?? new DateOnly(today.Year, 1, 1);
        var end = to ?? today;
        Compare = compare;

        DateOnly? compareFrom = null, compareTo = null;
        if (compare)
        {
            var days = end.DayNumber - start.DayNumber + 1;
            compareTo = start.AddDays(-1);
            compareFrom = compareTo.Value.AddDays(-(days - 1));
        }

        Report = await _reports.ProfitAndLossAsync(CompanyId.Value, start, end, compareFrom, compareTo);
        await LoadCompanyAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync(DateOnly? from, DateOnly? to, bool compare = false)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        var today = _clock.Today;
        var start = from ?? new DateOnly(today.Year, 1, 1);
        var end = to ?? today;

        DateOnly? compareFrom = null, compareTo = null;
        if (compare)
        {
            var days = end.DayNumber - start.DayNumber + 1;
            compareTo = start.AddDays(-1);
            compareFrom = compareTo.Value.AddDays(-(days - 1));
        }

        var report = await _reports.ProfitAndLossAsync(CompanyId.Value, start, end, compareFrom, compareTo);
        var rows = new List<object?[]>();
        foreach (var section in report.Sections)
        {
            rows.Add(new object?[] { section.Title, null, null });
            foreach (var row in section.Rows)
                rows.Add(new object?[] { $"  {row.Code} {row.Label}", row.Amount,
                    report.Comparative?.Sections.FirstOrDefault(s => s.Key == section.Key)?.Rows
                        .FirstOrDefault(r => r.AccountId == row.AccountId)?.Amount });
            rows.Add(new object?[] { $"Total {section.Title}", section.Total,
                report.Comparative?.Sections.FirstOrDefault(s => s.Key == section.Key)?.Total });
        }
        rows.Add(new object?[] { "Gross profit", report.GrossProfit, report.Comparative?.GrossProfit });
        rows.Add(new object?[] { "Operating profit", report.OperatingProfit, report.Comparative?.OperatingProfit });
        rows.Add(new object?[] { "Net profit", report.NetProfit, report.Comparative?.NetProfit });

        var csv = CsvWriter.Write(new[] { "Line", "Amount", "Comparative" }, rows);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"profit-and-loss-{end:yyyy-MM-dd}.csv");
    }

    private async Task LoadCompanyAsync()
    {
        var company = await _companies.GetAsync(CompanyId!.Value);
        CompanyName = company?.Name ?? "Company";
        CurrencyCode = company?.BaseCurrencyCode ?? "USD";
    }
}
