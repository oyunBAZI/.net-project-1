using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Settings;

public sealed class TaxesModel : AppPageModel
{
    private readonly SettingsService _settings;
    private readonly ReferenceDataService _reference;

    public TaxesModel(SettingsService settings, ReferenceDataService reference)
    {
        _settings = settings;
        _reference = reference;
    }

    [BindProperty] public TaxCodeRequest Input { get; set; } = new();
    public Guid? EditingId { get; private set; }
    public IReadOnlyList<TaxCode> TaxCodes { get; private set; } = Array.Empty<TaxCode>();
    public IReadOnlyList<AccountOption> Accounts { get; private set; } = Array.Empty<AccountOption>();
    private Dictionary<Guid, string> AccountNames { get; set; } = new();

    public string AccountName(Guid id) => AccountNames.TryGetValue(id, out var name) ? name : "—";

    public async Task<IActionResult> OnGetAsync(Guid? id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManageSettings)) return ForbiddenResult();

        await LoadAsync();
        if (id is { } taxId)
        {
            var tax = TaxCodes.FirstOrDefault(t => t.Id == taxId);
            if (tax is null) return NotFound();
            EditingId = tax.Id;
            Input = new TaxCodeRequest
            {
                Code = tax.Code,
                Name = tax.Name,
                Rate = tax.Rate,
                Kind = tax.Kind,
                TaxAccountId = tax.TaxAccountId,
                InputTaxAccountId = tax.InputTaxAccountId,
                IsInclusive = tax.IsInclusive,
                IsActive = tax.IsActive,
                ReportGroup = tax.ReportGroup
            };
        }
        else
        {
            Input.Kind = TaxKind.Both;
            Input.IsActive = true;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        await LoadAsync();
        EditingId = id;

        if (!await TryAsync(() => _settings.SaveTaxCodeAsync(CompanyId.Value, Input, id)))
            return Page();

        Success("Tax code saved.");
        return RedirectToPage("/Settings/Taxes");
    }

    private async Task LoadAsync()
    {
        TaxCodes = await _settings.ListTaxCodesAsync(CompanyId!.Value);
        var accounts = await _reference.GetAllAccountsAsync(CompanyId.Value);
        Accounts = accounts;
        AccountNames = accounts.ToDictionary(a => a.Id, a => a.Display);
    }
}
