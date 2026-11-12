using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages;

public sealed class AccountModel : AppPageModel
{
    private readonly IdentityService _identity;
    private readonly CompanyService _companies;

    public AccountModel(IdentityService identity, CompanyService companies)
    {
        _identity = identity;
        _companies = companies;
    }

    [BindProperty] public string DisplayName { get; set; } = string.Empty;
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;

    public string CompanyName { get; private set; } = string.Empty;
    public string PermissionSummary { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        DisplayName = CurrentDisplayName ?? string.Empty;
        if (CompanyId is { } companyId)
        {
            var company = await _companies.GetAsync(companyId);
            CompanyName = company?.Name ?? "—";
        }
        PermissionSummary = CurrentRole is { } role
            ? string.Join(", ", Enum.GetValues<Permission>()
                .Where(p => p is not (Permission.None or Permission.All or Permission.ReadOnly) &&
                            RolePermissions.Allows(role, p))
                .Select(p => SplitWords(p.ToString())))
            : "No access";
        return Page();
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        if (!Guid.TryParse(CurrentUserId, out var userId)) return RedirectToPage("/Login");
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            Error("Enter a display name.");
            return await OnGetAsync();
        }
        await _identity.UpdateProfileAsync(userId, DisplayName);
        Success("Profile updated. Sign in again to refresh the name shown in the sidebar.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        if (!Guid.TryParse(CurrentUserId, out var userId)) return RedirectToPage("/Login");
        if (!await TryAsync(() => _identity.ChangePasswordAsync(userId, CurrentPassword, NewPassword)))
            return await OnGetAsync();

        Success("Password changed.");
        return RedirectToPage();
    }

    private static string SplitWords(string value) =>
        string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
}
