using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Accounts;

public sealed class IndexModel : AppPageModel
{
    private readonly SettingsService _settings;
    private readonly ReportingService _reports;
    private readonly CompanyService _companies;
    private readonly IClock _clock;

    public IndexModel(SettingsService settings, ReportingService reports, CompanyService companies, IClock clock)
    {
        _settings = settings;
        _reports = reports;
        _companies = companies;
        _clock = clock;
    }

    [BindProperty(SupportsGet = true)] public AccountType? Type { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public bool IncludeInactive { get; set; }

    public sealed record Row(Account Account, decimal Balance);

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    public DateOnly AsOf { get; private set; }
    public string CurrencyCode { get; private set; } = "USD";

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewAccounts)) return ForbiddenResult();

        AsOf = _clock.Today;
        var accounts = await _settings.ListAccountsAsync(CompanyId.Value, IncludeInactive);
        var balances = await _reports.AccountBalancesAsync(CompanyId.Value, AsOf, Type);
        var balanceMap = balances.ToDictionary(b => b.AccountId, b => b.Balance);

        var query = accounts.AsEnumerable();
        if (Type.HasValue) query = query.Where(a => a.Type == Type);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(a => a.Code.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     a.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        Rows = query.OrderBy(a => a.Code)
            .Select(a => new Row(a, balanceMap.TryGetValue(a.Id, out var balance) ? balance : 0m))
            .ToList();

        var company = await _companies.GetAsync(CompanyId.Value);
        CurrencyCode = company?.BaseCurrencyCode ?? "USD";
        return Page();
    }
}
