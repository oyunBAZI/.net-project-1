using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Reports;

public sealed class GeneralLedgerModel : AppPageModel
{
    private readonly ReportingService _reports;
    private readonly ReferenceDataService _reference;
    private readonly CompanyService _companies;
    private readonly IClock _clock;

    public GeneralLedgerModel(ReportingService reports, ReferenceDataService reference, CompanyService companies,
        IClock clock)
    {
        _reports = reports;
        _reference = reference;
        _companies = companies;
        _clock = clock;
    }

    public GeneralLedgerReport? Report { get; private set; }
    public IReadOnlyList<AccountOption> Accounts { get; private set; } = Array.Empty<AccountOption>();
    public Guid? AccountId { get; private set; }
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public string CurrencyCode { get; private set; } = "USD";

    public async Task<IActionResult> OnGetAsync(Guid? accountId, DateOnly? from, DateOnly? to)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewReports)) return ForbiddenResult();

        var today = _clock.Today;
        From = from ?? new DateOnly(today.Year, 1, 1);
        To = to ?? today;
        AccountId = accountId;
        Accounts = await _reference.GetAllAccountsAsync(CompanyId.Value);

        var company = await _companies.GetAsync(CompanyId.Value);
        CurrencyCode = company?.BaseCurrencyCode ?? "USD";

        if (accountId is { } id)
            Report = await _reports.GeneralLedgerAsync(CompanyId.Value, id, From, To);

        return Page();
    }
}
