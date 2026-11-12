using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Accounts;

public sealed class EditModel : AppPageModel
{
    private readonly SettingsService _settings;
    private readonly ReferenceDataService _reference;

    public EditModel(SettingsService settings, ReferenceDataService reference)
    {
        _settings = settings;
        _reference = reference;
    }

    public Guid? Id { get; private set; }
    public bool IsNew => Id is null;

    [BindProperty] public AccountRequest Input { get; set; } = new();

    public IReadOnlyList<AccountOption> Parents { get; private set; } = Array.Empty<AccountOption>();

    public async Task<IActionResult> OnGetAsync(Guid? id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManageAccounts)) return ForbiddenResult();

        Parents = await _reference.GetAllAccountsAsync(CompanyId.Value);
        if (id is { } accountId)
        {
            var account = await _reference.GetAccountAsync(CompanyId.Value, accountId);
            if (account is null) return NotFound();
            Id = account.Id;
            Input = new AccountRequest
            {
                Code = account.Code,
                Name = account.Name,
                Type = account.Type,
                Group = account.Group,
                ParentId = account.ParentId,
                Description = account.Description,
                IsPostable = account.IsPostable,
                IsActive = account.IsActive,
                IsCashAccount = account.IsCashAccount,
                IsContra = account.IsContra,
                CashFlowCategory = account.CashFlowCategory,
                CurrencyCode = account.CurrencyCode,
                DefaultTaxCodeId = account.DefaultTaxCodeId
            };
        }
        else
        {
            Input.Type = AccountType.Expense;
            Input.Group = AccountGroup.OperatingExpense;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        Parents = await _reference.GetAllAccountsAsync(CompanyId.Value);
        Id = id;

        try
        {
            if (id is null)
            {
                var created = await _settings.CreateAccountAsync(CompanyId.Value, Input);
                Success($"Account {created.Code} created.");
            }
            else
            {
                await _settings.UpdateAccountAsync(CompanyId.Value, id.Value, Input);
                Success("Account updated.");
            }
            return RedirectToPage("/Accounts/Index");
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid? id)
    {
        if (CompanyId is null || id is null) return RedirectToPage("/SwitchCompany");
        try
        {
            var account = await _reference.GetAccountAsync(CompanyId.Value, id.Value);
            if (account is null) return NotFound();
            await _settings.SetAccountActiveAsync(CompanyId.Value, id.Value, !account.IsActive);
            Success(account.IsActive ? "Account deactivated." : "Account reactivated.");
            return RedirectToPage("/Accounts/Edit", new { id });
        }
        catch (DomainException ex)
        {
            Error(ex.Message);
            return RedirectToPage("/Accounts/Edit", new { id });
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid? id)
    {
        if (CompanyId is null || id is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _settings.DeleteAccountAsync(CompanyId.Value, id.Value)))
            return RedirectToPage("/Accounts/Edit", new { id });
        Success("Account deleted.");
        return RedirectToPage("/Accounts/Index");
    }
}
