using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Bills;

public sealed class IndexModel : AppPageModel
{
    private readonly PurchaseService _purchases;

    public IndexModel(PurchaseService purchases) => _purchases = purchases;

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public DocumentStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;

    public PagedResult<BillListItem> Result { get; private set; } =
        new(Array.Empty<BillListItem>(), 0, 1, 20);

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();

        Result = await _purchases.ListAsync(CompanyId.Value, new InvoiceFilter
        {
            Search = Search,
            Status = Status,
            From = From,
            To = To,
            Page = Page,
            PageSize = 20
        });
        return Page();
    }
}
