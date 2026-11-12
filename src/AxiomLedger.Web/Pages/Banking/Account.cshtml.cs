using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Banking;

public sealed class AccountModel : AppPageModel
{
    private readonly BankingService _banking;
    private readonly ReferenceDataService _reference;

    public AccountModel(BankingService banking, ReferenceDataService reference)
    {
        _banking = banking;
        _reference = reference;
    }

    [BindProperty] public BankAccountRequest Input { get; set; } = new();

    public IReadOnlyList<AccountOption> AssetAccounts { get; private set; } = Array.Empty<AccountOption>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManageBanking)) return ForbiddenResult();

        AssetAccounts = await _reference.GetAccountsByTypeAsync(CompanyId.Value, AccountType.Asset);
        Input.CreateLedgerAccount = true;
        Input.CurrencyCode = "USD";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        AssetAccounts = await _reference.GetAccountsByTypeAsync(CompanyId.Value, AccountType.Asset);

        if (Input.AccountId is null) Input.CreateLedgerAccount = true;

        try
        {
            await _banking.CreateAccountAsync(CompanyId.Value, Input);
            Success("Bank account created.");
            return RedirectToPage("/Banking/Index");
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}
