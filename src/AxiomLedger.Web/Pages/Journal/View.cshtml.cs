using AxiomLedger.Application;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Web.Pages.Journal;

public sealed class ViewModel : AppPageModel
{
    private readonly JournalService _journals;
    private readonly IAccountingDbContext _db;
    private readonly IClock _clock;

    public ViewModel(JournalService journals, IAccountingDbContext db, IClock clock)
    {
        _journals = journals;
        _db = db;
        _clock = clock;
    }

    public JournalEntry Entry { get; private set; } = new();
    public JournalEntry? Reversal { get; private set; }
    public string CurrencyCode { get; private set; } = "USD";
    public DateOnly Today => _clock.Today;

    private Dictionary<Guid, string> PartyNames { get; set; } = new();
    private Dictionary<Guid, string> DepartmentNames { get; set; } = new();

    public string PartyName(Guid? id) =>
        id is { } partyId && PartyNames.TryGetValue(partyId, out var name) ? name : "—";

    public string DepartmentName(Guid? id) =>
        id is { } departmentId && DepartmentNames.TryGetValue(departmentId, out var name) ? name : "—";

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();
        return await LoadAsync(id);
    }

    public async Task<IActionResult> OnPostPostAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _journals.PostAsync(CompanyId.Value, id))) return await LoadAsync(id);
        Success("Journal posted.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSubmitAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _journals.SubmitForApprovalAsync(CompanyId.Value, id))) return await LoadAsync(id);
        Success("Submitted for approval.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _journals.ApproveAndPostAsync(CompanyId.Value, id, "Approved")))
            return await LoadAsync(id);
        Success("Journal approved and posted.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, string reason)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _journals.RejectAsync(CompanyId.Value, id, reason))) return await LoadAsync(id);
        Success("Journal returned to draft.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReverseAsync(Guid id, DateOnly reversalDate, string? memo)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        try
        {
            var reversal = await _journals.ReverseAsync(CompanyId.Value, id, reversalDate, memo);
            Success($"Reversal {reversal.Number} posted.");
            return RedirectToPage("/Journal/View", new { id = reversal.Id });
        }
        catch (DomainException ex)
        {
            Error(ex.Message);
            return await LoadAsync(id);
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _journals.DeleteDraftAsync(CompanyId.Value, id))) return await LoadAsync(id);
        Success("Draft deleted.");
        return RedirectToPage("/Journal/Index");
    }

    private async Task<IActionResult> LoadAsync(Guid id)
    {
        var entry = await _journals.GetAsync(CompanyId!.Value, id);
        if (entry is null) return NotFound();
        Entry = entry;
        CurrencyCode = entry.CurrencyCode ?? "USD";

        if (entry.ReversedById is { } reversalId)
        {
            Reversal = await _db.JournalEntries.AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == reversalId);
        }

        var partyIds = entry.Lines.Where(l => l.PartyId.HasValue).Select(l => l.PartyId!.Value).Distinct().ToList();
        PartyNames = await _db.Parties.Where(p => partyIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);

        var departmentIds = entry.Lines.Where(l => l.DepartmentId.HasValue)
            .Select(l => l.DepartmentId!.Value).Distinct().ToList();
        DepartmentNames = await _db.Departments.Where(d => departmentIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name);

        return Page();
    }
}
