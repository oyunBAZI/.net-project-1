using System.Security.Claims;
using AxiomLedger.Application;
using AxiomLedger.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AxiomLedger.Web.Services;

public abstract class AppPageModel : PageModel
{
    public string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);
    public string? CurrentEmail => User.FindFirstValue(ClaimTypes.Email);
    public string? CurrentDisplayName => User.FindFirstValue(AppClaims.DisplayName);

    public Guid? CompanyId => Guid.TryParse(User.FindFirstValue(AppClaims.CompanyId), out var id) ? id : null;

    public UserRole? CurrentRole => Enum.TryParse<UserRole>(User.FindFirstValue(AppClaims.Role), out var role)
        ? role
        : null;

    public bool Can(Permission permission) =>
        CurrentRole is { } role && RolePermissions.Allows(role, permission);

    public ICurrentUser Actor => HttpContext.RequestServices.GetRequiredService<ICurrentUser>();

    protected Guid RequireCompany() =>
        CompanyId ?? throw new DomainValidationException("Choose a company before continuing.");

    protected void Success(string message) => TempData["Success"] = message;
    protected void Error(string message) => TempData["Error"] = message;

    /// <summary>Runs an action, turning expected business failures into a user-facing message on the page.</summary>
    protected async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Error(ex.Message);
            return false;
        }
    }

    protected IActionResult ForbiddenResult() => Redirect("/forbidden");
}
