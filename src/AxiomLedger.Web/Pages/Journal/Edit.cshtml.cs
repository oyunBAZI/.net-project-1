using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Journal;

public sealed class EditModel : AppPageModel
{
    private readonly JournalService _journals;
    private readonly ReferenceDataService _reference;

    public EditModel(JournalService journals, ReferenceDataService reference)
    {
        _journals = journals;
        _reference = reference;
    }

    public Guid? Id { get; private set; }
    public bool IsNew => Id is null;
    public string Number { get; private set; } = string.Empty;

    [BindProperty] public JournalDraftRequest Input { get; set; } = new();

    public IReadOnlyList<AccountOption> Accounts { get; private set; } = Array.Empty<AccountOption>();
    public IReadOnlyList<PartyOption> Parties { get; private set; } = Array.Empty<PartyOption>();
    public IReadOnlyList<Department> Departments { get; private set; } = Array.Empty<Department>();

    public async Task<IActionResult> OnGetAsync(Guid? id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.CreateJournal) && !Can(Permission.PostJournal)) return ForbiddenResult();

        await LoadReferenceAsync(CompanyId.Value);
        if (id is { } entryId)
        {
            var entry = await _journals.GetAsync(CompanyId.Value, entryId);
            if (entry is null) return NotFound();
            if (!entry.IsEditable)
            {
                Error("Only draft or pending journals can be edited.");
                return RedirectToPage("/Journal/View", new { id = entryId });
            }

            Id = entry.Id;
            Number = entry.Number;
            Input = new JournalDraftRequest
            {
                EntryDate = entry.EntryDate,
                Memo = entry.Memo,
                Reference = entry.Reference,
                CurrencyCode = entry.CurrencyCode,
                ExchangeRate = entry.ExchangeRate ?? 1m,
                Lines = entry.Lines.OrderBy(l => l.LineNumber).Select(l => new JournalLineInput(
                    l.AccountId, l.Description, l.Debit, l.Credit, l.DepartmentId, l.PartyId, l.TaxCodeId)).ToList()
            };
        }

        if (Input.Lines.Count == 0)
            Input.Lines.Add(new JournalLineInput(Guid.Empty, null, 0m, 0m));
        if (Input.Lines.Count < 2)
            Input.Lines.Add(new JournalLineInput(Guid.Empty, null, 0m, 0m));

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, string action = "save")
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        await LoadReferenceAsync(CompanyId.Value);

        Id = id;
        if (string.IsNullOrWhiteSpace(Input.CurrencyCode))
            Input.CurrencyCode = (await _reference.GetCurrenciesAsync())
                .FirstOrDefault()?.Code ?? "USD";

        Input.Lines = Input.Lines.Where(l => l.AccountId != Guid.Empty || l.Debit != 0 || l.Credit != 0).ToList();
        if (Input.Lines.Count < 2)
        {
            ModelState.AddModelError(string.Empty, "Enter at least two complete lines.");
            return Page();
        }

        if (!Can(Permission.CreateJournal))
        {
            ModelState.AddModelError(string.Empty, "Your role cannot create journals.");
            return Page();
        }

        try
        {
            JournalEntry entry;
            if (id is null)
            {
                entry = await _journals.CreateDraftAsync(CompanyId.Value, Input);
            }
            else
            {
                entry = await _journals.UpdateDraftAsync(CompanyId.Value, id.Value, Input);
            }

            if (string.Equals(action, "post", StringComparison.OrdinalIgnoreCase))
            {
                await _journals.PostAsync(CompanyId.Value, entry.Id);
                Success($"Journal {entry.Number} posted.");
                return RedirectToPage("/Journal/View", new { id = entry.Id });
            }

            Success($"Journal {entry.Number} saved as a draft.");
            return RedirectToPage("/Journal/View", new { id = entry.Id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Number = id is null ? string.Empty : Number;
            return Page();
        }
    }

    private async Task LoadReferenceAsync(Guid companyId)
    {
        Accounts = await _reference.GetPostableAccountsAsync(companyId);
        Parties = await _reference.GetPartiesAsync(companyId);
        Departments = await _reference.GetDepartmentsAsync(companyId);
    }
}
