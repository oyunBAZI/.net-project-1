using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Parties;

public sealed class ViewModel : AppPageModel
{
    private readonly PartyService _parties;
    private readonly ReportingService _reports;
    private readonly PaymentService _payments;
    private readonly IClock _clock;

    public ViewModel(PartyService parties, ReportingService reports, PaymentService payments, IClock clock)
    {
        _parties = parties;
        _reports = reports;
        _payments = payments;
        _clock = clock;
    }

    public Party Party { get; private set; } = new();
    public PartyStatement Statement { get; private set; } =
        new(Guid.Empty, string.Empty, default, default, 0m, Array.Empty<PartyStatementRow>(), 0m, 0m, 0m);
    public IReadOnlyList<OpenDocument> OpenDocuments { get; private set; } = Array.Empty<OpenDocument>();
    public decimal Receivable { get; private set; }
    public decimal Payable { get; private set; }
    public decimal UnappliedCredits { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();

        var party = await _parties.GetAsync(CompanyId.Value, id);
        if (party is null) return NotFound();
        Party = party;

        var from = new DateOnly(_clock.Today.Year, 1, 1);
        Statement = await _reports.PartyStatementAsync(CompanyId.Value, id, from, _clock.Today);

        var listed = await _parties.ListAsync(CompanyId.Value, null, null, true);
        var row = listed.FirstOrDefault(p => p.Id == id);
        Receivable = row?.Receivable ?? 0m;
        Payable = row?.Payable ?? 0m;
        UnappliedCredits = await _payments.GetUnappliedCreditsAsync(CompanyId.Value, id);

        OpenDocuments = party.IsVendor
            ? await _payments.GetOpenDocumentsAsync(CompanyId.Value, id, PaymentDirection.Disbursement)
            : await _payments.GetOpenDocumentsAsync(CompanyId.Value, id, PaymentDirection.Receipt);

        return Page();
    }
}
