using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Journal;

public sealed class IndexModel : AppPageModel
{
    private readonly JournalService _journals;

    public IndexModel(JournalService journals) => _journals = journals;

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public JournalStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public JournalSource? Source { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;

    public PagedResult<JournalListItem> Result { get; private set; } =
        new(Array.Empty<JournalListItem>(), 0, 1, 25);

    public int PendingApprovals { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();

        Result = await _journals.ListAsync(CompanyId.Value, new JournalFilter
        {
            Search = Search,
            Status = Status,
            Source = Source,
            From = From,
            To = To,
            Page = Page,
            PageSize = 25
        });
        PendingApprovals = await _journals.CountPendingApprovalsAsync(CompanyId.Value);
        return Page();
    }
}
