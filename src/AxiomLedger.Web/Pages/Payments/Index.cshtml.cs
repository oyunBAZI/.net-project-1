using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Payments;

public sealed class IndexModel : AppPageModel
{
    private readonly PaymentService _payments;

    public IndexModel(PaymentService payments) => _payments = payments;

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public PaymentDirection? Direction { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;

    public PagedResult<PaymentListItem> Result { get; private set; } =
        new(Array.Empty<PaymentListItem>(), 0, 1, 20);

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();

        Result = await _payments.ListAsync(CompanyId.Value, new PaymentFilter
        {
            Search = Search,
            Direction = Direction,
            From = From,
            To = To,
            Page = Page,
            PageSize = 20
        });
        return Page();
    }
}
