using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Bills;

public sealed class ViewModel : AppPageModel
{
    private readonly PurchaseService _purchases;

    public ViewModel(PurchaseService purchases) => _purchases = purchases;

    public Bill Bill { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();
        return await LoadAsync(id);
    }

    public async Task<IActionResult> OnPostReceiveAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _purchases.ReceiveAsync(CompanyId.Value, id))) return await LoadAsync(id);
        Success("Bill received and posted.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostVoidAsync(Guid id, string reason)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _purchases.VoidAsync(CompanyId.Value, id, reason))) return await LoadAsync(id);
        Success("Bill voided.");
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> LoadAsync(Guid id)
    {
        var bill = await _purchases.GetAsync(CompanyId!.Value, id);
        if (bill is null) return NotFound();
        Bill = bill;
        return Page();
    }
}
