using System.Security.Claims;
using AxiomLedger.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AxiomLedger.Web.Services;

public static class AppClaims
{
    public const string CompanyId = "company_id";
    public const string Role = "company_role";
    public const string DisplayName = "display_name";
}

/// <summary>Reads the signed-in user and the company currently selected for this session.</summary>
public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public string? UserId => Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email);
    public string? DisplayName => Principal?.FindFirstValue(AppClaims.DisplayName);
    public Guid? CompanyId => Guid.TryParse(Principal?.FindFirstValue(AppClaims.CompanyId), out var id) ? id : null;
    public UserRole? Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue(AppClaims.Role), out var role)
        ? role
        : null;
    public string? IpAddress => _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
}

public static class SignInHelper
{
    public static async Task SignInAsync(HttpContext context, User user, Guid companyId, UserRole role,
        bool persistent = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new(AppClaims.DisplayName, user.DisplayName),
            new(AppClaims.CompanyId, companyId.ToString()),
            new(AppClaims.Role, role.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = persistent,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(persistent ? 24 * 14 : 10),
                AllowRefresh = true
            });
    }

    public static async Task RefreshCompanyAsync(HttpContext context, Guid companyId, UserRole role)
    {
        var principal = context.User;
        if (principal.Identity is not ClaimsIdentity identity) return;

        var existing = identity.FindFirst(AppClaims.CompanyId);
        if (existing is not null) identity.RemoveClaim(existing);
        var roleClaim = identity.FindFirst(AppClaims.Role);
        if (roleClaim is not null) identity.RemoveClaim(roleClaim);

        identity.AddClaim(new Claim(AppClaims.CompanyId, companyId.ToString()));
        identity.AddClaim(new Claim(AppClaims.Role, role.ToString()));

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(10) });
    }
}

/// <summary>Product name and colours, read from configuration so deployments can white-label.</summary>
public sealed class BrandingService
{
    public BrandingService(IConfiguration configuration)
    {
        var section = configuration.GetSection("Branding");
        ProductName = section["ProductName"] ?? "Axiom Ledger";
        Tagline = section["Tagline"] ?? "Accounting for every size of business";
        Accent = section["Accent"] ?? "#1f7a5c";
    }

    public string ProductName { get; }
    public string Tagline { get; }
    public string Accent { get; }
}
