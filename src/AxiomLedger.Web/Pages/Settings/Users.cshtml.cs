using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Settings;

public sealed class UsersModel : AppPageModel
{
    private readonly CompanyService _companies;
    private readonly IdentityService _identity;

    public UsersModel(CompanyService companies, IdentityService identity)
    {
        _companies = companies;
        _identity = identity;
    }

    public sealed class InviteRequest
    {
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.Accountant;
        public string Password { get; set; } = string.Empty;
    }

    [BindProperty] public InviteRequest Invite { get; set; } = new();

    public IReadOnlyList<MembershipView> Members { get; private set; } = Array.Empty<MembershipView>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManageUsers)) return ForbiddenResult();
        Members = await _companies.ListMembersAsync(CompanyId.Value);
        return Page();
    }

    public async Task<IActionResult> OnPostInviteAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(async () =>
            {
                await _identity.AddMemberAsync(CompanyId.Value, Invite.Email, Invite.DisplayName, Invite.Role,
                    Invite.Password);
                Success($"{Invite.Email} added as {Invite.Role}.");
            }))
        {
            Members = await _companies.ListMembersAsync(CompanyId.Value);
            return Page();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRoleAsync(Guid userId, UserRole role)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _identity.UpdateMemberRoleAsync(CompanyId.Value, userId, role)))
            return RedirectToPage();
        Success("Role updated.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid userId, bool active)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _identity.SetMemberActiveAsync(CompanyId.Value, userId, active)))
            return RedirectToPage();
        Success(active ? "Member enabled." : "Member disabled.");
        return RedirectToPage();
    }
}
