using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Parties;

public sealed class EditModel : AppPageModel
{
    private readonly PartyService _parties;
    private readonly ReferenceDataService _reference;

    public EditModel(PartyService parties, ReferenceDataService reference)
    {
        _parties = parties;
        _reference = reference;
    }

    public Guid? Id { get; private set; }
    public bool IsNew => Id is null;

    [BindProperty] public PartyRequest Input { get; set; } = new();

    public IReadOnlyList<Currency> Currencies { get; private set; } = Array.Empty<Currency>();

    public async Task<IActionResult> OnGetAsync(Guid? id, PartyType? type)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManageParties)) return ForbiddenResult();

        Currencies = await _reference.GetCurrenciesAsync();
        Input.Type = type ?? PartyType.Customer;
        Input.CurrencyCode = Currencies.FirstOrDefault()?.Code ?? "USD";

        if (id is { } partyId)
        {
            var party = await _parties.GetAsync(CompanyId.Value, partyId);
            if (party is null) return NotFound();
            Id = party.Id;
            Input = new PartyRequest
            {
                Type = party.Type,
                Code = party.Code,
                Name = party.Name,
                Email = party.Email,
                Phone = party.Phone,
                ContactName = party.ContactName,
                TaxId = party.TaxId,
                AddressLine1 = party.AddressLine1,
                AddressLine2 = party.AddressLine2,
                City = party.City,
                Region = party.Region,
                PostalCode = party.PostalCode,
                CountryCode = party.CountryCode,
                CurrencyCode = party.CurrencyCode,
                PaymentTermsDays = party.PaymentTermsDays,
                CreditLimit = party.CreditLimit,
                IsActive = party.IsActive,
                Notes = party.Notes
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        Currencies = await _reference.GetCurrenciesAsync();
        Id = id;

        try
        {
            if (id is null)
            {
                var created = await _parties.CreateAsync(CompanyId.Value, Input);
                Success($"{Input.Name} created.");
                return RedirectToPage("/Parties/View", new { id = created.Id });
            }

            await _parties.UpdateAsync(CompanyId.Value, id.Value, Input);
            Success("Party updated.");
            return RedirectToPage("/Parties/View", new { id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid? id)
    {
        if (CompanyId is null || id is null) return RedirectToPage("/SwitchCompany");
        try
        {
            var party = await _parties.GetAsync(CompanyId.Value, id.Value);
            if (party is null) return NotFound();
            await _parties.SetActiveAsync(CompanyId.Value, id.Value, !party.IsActive);
            Success(party.IsActive ? "Party deactivated." : "Party reactivated.");
            return RedirectToPage("/Parties/View", new { id });
        }
        catch (DomainException ex)
        {
            Error(ex.Message);
            return RedirectToPage("/Parties/View", new { id });
        }
    }
}
