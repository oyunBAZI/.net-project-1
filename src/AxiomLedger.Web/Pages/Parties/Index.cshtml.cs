using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Parties;

public sealed class IndexModel : AppPageModel
{
    private readonly PartyService _parties;

    public IndexModel(PartyService parties) => _parties = parties;

    [BindProperty(SupportsGet = true)] public PartyType Type { get; set; } = PartyType.Customer;
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public bool IncludeInactive { get; set; }

    public IReadOnlyList<PartyListItem> Parties { get; private set; } = Array.Empty<PartyListItem>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();

        var filter = Type == PartyType.Both ? (PartyType?)null : Type;
        Parties = await _parties.ListAsync(CompanyId.Value, Search, filter, IncludeInactive);
        return Page();
    }
}
