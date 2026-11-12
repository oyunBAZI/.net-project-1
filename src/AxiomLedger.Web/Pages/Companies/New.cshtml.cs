using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Companies;

public sealed class NewModel : AppPageModel
{
    private readonly CompanyService _companies;

    public NewModel(CompanyService companies) => _companies = companies;

    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string? LegalName { get; set; }
    [BindProperty] public string? TaxId { get; set; }
    [BindProperty] public string BaseCurrencyCode { get; set; } = "USD";
    [BindProperty] public string CountryCode { get; set; } = "US";
    [BindProperty] public int FiscalYearStartMonth { get; set; } = 1;
    [BindProperty] public string Industry { get; set; } = "general";
    [BindProperty] public string? AddressLine1 { get; set; }
    [BindProperty] public string? City { get; set; }
    [BindProperty] public string? PostalCode { get; set; }

    public IActionResult OnGet()
    {
        if (!Can(Permission.ManageCompanies) && User.Identity?.IsAuthenticated != true)
            return RedirectToPage("/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentUserId)) return RedirectToPage("/Login");

        try
        {
            var company = await _companies.CreateAsync(new NewCompanyRequest
            {
                Name = Name,
                LegalName = LegalName,
                TaxId = TaxId,
                BaseCurrencyCode = BaseCurrencyCode,
                CountryCode = CountryCode,
                FiscalYearStartMonth = FiscalYearStartMonth,
                Industry = Industry,
                AddressLine1 = AddressLine1,
                City = City,
                PostalCode = PostalCode
            }, CurrentUserId);

            await SignInHelper.RefreshCompanyAsync(HttpContext, company.Id, UserRole.Owner);
            Success($"Company {company.Name} created.");
            return RedirectToPage("/Index");
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}
