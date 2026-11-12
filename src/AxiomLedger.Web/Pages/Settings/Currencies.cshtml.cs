using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Settings;

public sealed class CurrenciesModel : AppPageModel
{
    private readonly SettingsService _settings;
    private readonly ReferenceDataService _reference;
    private readonly CompanyService _companies;
    private readonly IClock _clock;

    public CurrenciesModel(SettingsService settings, ReferenceDataService reference, CompanyService companies,
        IClock clock)
    {
        _settings = settings;
        _reference = reference;
        _companies = companies;
        _clock = clock;
    }

    [BindProperty] public ExchangeRateRequest Input { get; set; } = new();

    public IReadOnlyList<ExchangeRate> Rates { get; private set; } = Array.Empty<ExchangeRate>();
    public IReadOnlyList<Currency> Currencies { get; private set; } = Array.Empty<Currency>();
    public string BaseCurrency { get; private set; } = "USD";
    public DateOnly Today => _clock.Today;

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManageSettings)) return ForbiddenResult();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (string.IsNullOrWhiteSpace(Input.CurrencyCode))
            ModelState.AddModelError(string.Empty, "Choose a currency.");

        if (Input.RateDate == default) Input.RateDate = _clock.Today;

        if (!await TryAsync(() => _settings.UpsertRateAsync(CompanyId.Value, Input)))
        {
            await LoadAsync();
            return Page();
        }

        Success("Exchange rate saved.");
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Rates = await _reference.GetExchangeRatesAsync(CompanyId!.Value);
        Currencies = await _reference.GetCurrenciesAsync();
        var company = await _companies.GetAsync(CompanyId.Value);
        BaseCurrency = company?.BaseCurrencyCode ?? "USD";
    }
}
