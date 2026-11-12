namespace AxiomLedger.Domain;

public class User : Entity
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? PasswordSalt { get; set; }
    public int PasswordIterations { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsPlatformAdmin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntil { get; set; }
    public Guid? PreferredCompanyId { get; set; }
    public string? Theme { get; set; }
    public List<CompanyMembership> Memberships { get; set; } = new();

    public bool IsLockedOut => LockoutUntil.HasValue && LockoutUntil.Value > DateTime.UtcNow;
}

public class CompanyMembership : Entity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public UserRole Role { get; set; } = UserRole.Accountant;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog : CompanyEntity
{
    public string? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The authenticated actor for the current request, used for permission checks and audit.</summary>
public interface ICurrentUser
{
    string? UserId { get; }
    string? Email { get; }
    string? DisplayName { get; }
    Guid? CompanyId { get; }
    UserRole? Role { get; }
    string? IpAddress { get; }
    bool IsAuthenticated { get; }
}

public sealed class SystemCurrentUser : ICurrentUser
{
    public string? UserId { get; init; } = "system";
    public string? Email { get; init; } = "system@localhost";
    public string? DisplayName { get; init; } = "System";
    public Guid? CompanyId { get; init; }
    public UserRole? Role { get; init; } = UserRole.Owner;
    public string? IpAddress { get; init; }
    public bool IsAuthenticated => true;
}

public static class PermissionGuard
{
    public static void Demand(ICurrentUser user, Permission permission)
    {
        if (!user.IsAuthenticated) throw new PermissionDeniedException("Sign in to continue.");
        if (user.Role is null)
            throw new PermissionDeniedException("You are not a member of this company.");
        if (!RolePermissions.Allows(user.Role.Value, permission))
            throw new PermissionDeniedException(
                $"Your role ({user.Role}) does not allow this action.");
    }

    public static bool Allows(ICurrentUser user, Permission permission) =>
        user.IsAuthenticated && user.Role is not null && RolePermissions.Allows(user.Role.Value, permission);
}
