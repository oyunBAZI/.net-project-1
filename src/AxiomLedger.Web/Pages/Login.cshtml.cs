using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AxiomLedger.Web.Pages;

public sealed class LoginModel : PageModel
{
    private readonly IdentityService _identity;
    private readonly CompanyService _companies;

    public LoginModel(IdentityService identity, CompanyService companies)
    {
        _identity = identity;
        _companies = companies;
    }

    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty] public bool RememberMe { get; set; }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Index");
        if (!await _identity.AnyUserAsync()) return RedirectToPage("/Setup");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await _identity.AnyUserAsync()) return RedirectToPage("/Setup");

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Enter your email and password.";
            return Page();
        }

        var (user, error) = await _identity.ValidateCredentialsAsync(Email, Password);
        if (user is null)
        {
            ErrorMessage = error;
            return Page();
        }

        var companies = await _companies.ListForUserAsync(user.Id.ToString());
        if (companies.Count == 0)
        {
            ErrorMessage = "Your account is not linked to a company yet. Ask an administrator to invite you.";
            return Page();
        }

        var chosen = companies.FirstOrDefault(c => c.Id == user.PreferredCompanyId) ?? companies[0];
        await SignInHelper.SignInAsync(HttpContext, user, chosen.Id, chosen.Role, RememberMe);
        return RedirectToPage("/Index");
    }
}
