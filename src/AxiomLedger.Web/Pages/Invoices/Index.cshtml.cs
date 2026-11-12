using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Invoices;

public sealed class IndexModel : AppPageModel
{
    private readonly SalesService _sales;

    public IndexModel(SalesService sales) => _sales = sales;

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public DocumentStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public bool Overdue { get; set; }
    [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;

    public PagedResult<InvoiceListItem> Result { get; private set; } =
        new(Array.Empty<InvoiceListItem>(), 0, 1, 20);

    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();

        Result = await _sales.ListAsync(CompanyId.Value, new InvoiceFilter
        {
            Search = Search,
            Status = Status,
            From = From,
            To = To,
            Overdue = Overdue ? true : null,
            Page = Page,
            PageSize = 20
        });
        return Page();
    }
}
