using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Payments;

public sealed class ViewModel : AppPageModel
{
    private readonly PaymentService _payments;

    public ViewModel(PaymentService payments) => _payments = payments;

    public Payment Payment { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();
        return await LoadAsync(id);
    }

    public async Task<IActionResult> OnPostVoidAsync(Guid id, string reason)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _payments.VoidAsync(CompanyId.Value, id, reason))) return await LoadAsync(id);
        Success("Payment voided.");
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> LoadAsync(Guid id)
    {
        var payment = await _payments.GetAsync(CompanyId!.Value, id);
        if (payment is null) return NotFound();
        Payment = payment;
        return Page();
    }
}
