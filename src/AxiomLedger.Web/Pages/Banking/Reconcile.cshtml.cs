using AxiomLedger.Domain;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Banking;

public sealed class ReconcileModel : AppPageModel
{
    private readonly BankingService _banking;
    private readonly CompanyService _companies;

    public ReconcileModel(BankingService banking, CompanyService companies)
    {
        _banking = banking;
        _companies = companies;
    }

    public ReconciliationSummary Summary { get; private set; } = new(Guid.Empty, string.Empty,
        default, default, 0m, 0m, 0m, 0m, 0m, 0, Array.Empty<ReconciliationLine>());

    public string CurrencyCode { get; private set; } = "USD";

    public async Task<IActionResult> OnGetAsync(Guid bankId, DateOnly? from, DateOnly? to)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();
        return await LoadAsync(bankId, from, to);
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid bankId, DateOnly from, DateOnly to)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _banking.CompleteReconciliationAsync(CompanyId.Value, bankId, from, to)))
            return await LoadAsync(bankId, from, to);

        Success("Reconciliation completed.");
        return RedirectToPage("/Banking/Index", new { bankId });
    }

    private async Task<IActionResult> LoadAsync(Guid bankId, DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        Summary = await _banking.GetReconciliationAsync(CompanyId!.Value, bankId, start, end);
        var company = await _companies.GetAsync(CompanyId.Value);
        CurrencyCode = company?.BaseCurrencyCode ?? "USD";
        return Page();
    }
}
