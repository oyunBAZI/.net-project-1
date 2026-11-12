using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Banking;

public sealed class IndexModel : AppPageModel
{
    private readonly BankingService _banking;
    private readonly ReferenceDataService _reference;

    public IndexModel(BankingService banking, ReferenceDataService reference)
    {
        _banking = banking;
        _reference = reference;
    }

    [BindProperty(SupportsGet = true)] public Guid? BankId { get; set; }
    [BindProperty] public IFormFile? StatementFile { get; set; }

    public IReadOnlyList<BankAccountListItem> Accounts { get; private set; } = Array.Empty<BankAccountListItem>();
    public BankAccountListItem? SelectedBank { get; private set; }
    public IReadOnlyList<BankTransactionView> Lines { get; private set; } = Array.Empty<BankTransactionView>();
    public Dictionary<Guid, IReadOnlyList<MatchingCandidate>> Candidates { get; private set; } = new();
    public IReadOnlyList<AccountOption> Accounts2 { get; private set; } = Array.Empty<AccountOption>();
    public StatementImportResult? ImportResult { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(Guid bankId)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (StatementFile is null || StatementFile.Length == 0)
        {
            Error("Choose a CSV statement to import.");
            return RedirectToPage("/Banking/Index", new { bankId });
        }

        using var reader = new StreamReader(StatementFile.OpenReadStream());
        var csv = await reader.ReadToEndAsync();

        try
        {
            var result = await _banking.ImportCsvAsync(CompanyId.Value, bankId, csv);
            var matched = await _banking.AutoMatchAsync(CompanyId.Value, bankId);
            Success($"Imported {result.Imported} line(s); {result.Duplicates} duplicate(s) skipped; {matched} auto-matched.");
            return RedirectToPage("/Banking/Index", new { bankId });
        }
        catch (DomainException ex)
        {
            Error(ex.Message);
            return RedirectToPage("/Banking/Index", new { bankId });
        }
    }

    public async Task<IActionResult> OnPostAutoMatchAsync(Guid bankId)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(async () =>
            {
                var matched = await _banking.AutoMatchAsync(CompanyId.Value, bankId);
                Success($"{matched} statement line(s) matched automatically.");
            }))
        {
            Error("Auto-match failed.");
        }
        return RedirectToPage("/Banking/Index", new { bankId });
    }

    public async Task<IActionResult> OnPostMatchAsync(Guid bankId, Guid lineId, Guid journalLineId)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _banking.MatchAsync(CompanyId.Value, lineId, journalLineId)))
            Error("Could not match that line.");
        else Success("Matched.");
        return RedirectToPage("/Banking/Index", new { bankId });
    }

    public async Task<IActionResult> OnPostUnmatchAsync(Guid bankId, Guid lineId)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _banking.UnmatchAsync(CompanyId.Value, lineId))) Error("Could not unmatch.");
        else Success("Unmatched.");
        return RedirectToPage("/Banking/Index", new { bankId });
    }

    public async Task<IActionResult> OnPostExcludeAsync(Guid bankId, Guid lineId, bool excluded)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _banking.SetExcludedAsync(CompanyId.Value, lineId, excluded)))
            Error("Could not update the line.");
        return RedirectToPage("/Banking/Index", new { bankId });
    }

    public async Task<IActionResult> OnPostCreateEntryAsync(Guid bankId, Guid lineId, Guid contraAccountId,
        string? description)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(async () =>
            {
                await _banking.CreateEntryFromLineAsync(CompanyId.Value, lineId, contraAccountId, description);
                Success("Journal posted and line matched.");
            }))
        {
            Error("Could not create the entry.");
        }
        return RedirectToPage("/Banking/Index", new { bankId });
    }

    private async Task LoadAsync()
    {
        Accounts = await _banking.ListAccountsAsync(CompanyId!.Value);
        SelectedBank = BankId is { } id
            ? Accounts.FirstOrDefault(a => a.Id == id)
            : Accounts.FirstOrDefault();

        if (SelectedBank is not null)
        {
            Lines = await _banking.ListLinesAsync(CompanyId.Value, SelectedBank.Id);
            foreach (var line in Lines.Where(l => l.Status == BankLineStatus.Unmatched).Take(15))
            {
                var candidates = await _banking.GetCandidatesAsync(CompanyId.Value, line.Id);
                if (candidates.Count > 0) Candidates[line.Id] = candidates;
            }
        }

        Accounts2 = await _reference.GetPostableAccountsAsync(CompanyId.Value);
    }
}
