using System.Security.Claims;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AxiomLedger.Web.Pages;

[Authorize]
public sealed class SwitchCompanyModel : PageModel
{
    private readonly CompanyService _companies;

    public SwitchCompanyModel(CompanyService companies) => _companies = companies;

    public IReadOnlyList<CompanyOption> Companies { get; private set; } = Array.Empty<CompanyOption>();

    public async Task<IActionResult> OnGetAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return RedirectToPage("/Login");
        Companies = await _companies.ListForUserAsync(userId);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid companyId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return RedirectToPage("/Login");

        var membership = await _companies.GetMembershipAsync(companyId, userId);
        if (membership is null) return RedirectToPage("/Forbidden");

        await SignInHelper.RefreshCompanyAsync(HttpContext, companyId, membership.Role);
        return RedirectToPage("/Index");
    }
}
