using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AxiomLedger.Web.Pages;

public sealed class SetupModel : PageModel
{
    private readonly IdentityService _identity;
    private readonly CompanyService _companies;

    public SetupModel(IdentityService identity, CompanyService companies)
    {
        _identity = identity;
        _companies = companies;
    }

    [BindProperty] public string DisplayName { get; set; } = string.Empty;
    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;
    [BindProperty] public string CompanyName { get; set; } = string.Empty;
    [BindProperty] public string BaseCurrencyCode { get; set; } = "USD";
    [BindProperty] public int FiscalYearStartMonth { get; set; } = 1;
    [BindProperty] public string Industry { get; set; } = "general";
    [BindProperty] public string CountryCode { get; set; } = "US";

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Index");
        if (await _identity.AnyUserAsync()) return RedirectToPage("/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await _identity.AnyUserAsync())
            return RedirectToPage("/Login");

        if (string.IsNullOrWhiteSpace(DisplayName) || string.IsNullOrWhiteSpace(Email) ||
            string.IsNullOrWhiteSpace(CompanyName))
        {
            ErrorMessage = "Complete your name, email and company name.";
            return Page();
        }

        if (Password != ConfirmPassword)
        {
            ErrorMessage = "The passwords do not match.";
            return Page();
        }

        try
        {
            var user = await _identity.CreateUserAsync(Email, DisplayName, Password, platformAdmin: true);
            var company = await _companies.CreateAsync(new NewCompanyRequest
            {
                Name = CompanyName,
                BaseCurrencyCode = BaseCurrencyCode,
                CountryCode = string.IsNullOrWhiteSpace(CountryCode) ? "US" : CountryCode.ToUpperInvariant(),
                FiscalYearStartMonth = FiscalYearStartMonth,
                Industry = Industry
            }, user.Id.ToString());

            await SignInHelper.SignInAsync(HttpContext, user, company.Id, UserRole.Owner);
            return RedirectToPage("/Index");
        }
        catch (DomainException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
