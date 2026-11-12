using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>User accounts, password policy, company membership and role administration.</summary>
public sealed class IdentityService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public IdentityService(IAccountingDbContext db, ICurrentUser currentUser, IClock clock, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _audit = audit;
    }

    public async Task<bool> AnyUserAsync(CancellationToken ct = default) => await _db.Users.AnyAsync(ct);

    public async Task<IReadOnlyList<UserListItem>> ListUsersAsync(CancellationToken ct = default)
    {
        PermissionGuard.Demand(_currentUser, Permission.ManageUsers);
        var companies = await _db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        return await _db.Users.AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .Select(u => new UserListItem(u.Id, u.Email, u.DisplayName, u.IsActive, u.IsPlatformAdmin,
                u.CreatedAt, u.LastLoginAt, u.Memberships.Count(m => m.IsActive)))
            .ToListAsync(ct);
    }

    public async Task<User> CreateUserAsync(string email, string displayName, string password,
        bool platformAdmin = false, CancellationToken ct = default)
    {
        ValidateEmail(email);
        ValidatePassword(password);

        var normalized = email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == normalized, ct))
            throw new DomainValidationException("An account with that email already exists.");

        var (hash, salt, iterations) = PasswordHasher.Hash(password);
        var user = new User
        {
            Email = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            IsActive = true,
            IsPlatformAdmin = platformAdmin,
            CreatedAt = _clock.UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<(User? User, string? Error)> ValidateCredentialsAsync(string email, string password,
        CancellationToken ct = default)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null) return (null, "Email or password is incorrect.");
        if (!user.IsActive) return (null, "This account has been deactivated.");
        if (user.IsLockedOut)
            return (null, $"Too many failed attempts. Try again after {user.LockoutUntil:HH:mm} UTC.");

        if (!PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= 5)
            {
                user.LockoutUntil = _clock.UtcNow.AddMinutes(15);
                user.FailedLoginCount = 0;
            }
            await _db.SaveChangesAsync(ct);
            return (null, "Email or password is incorrect.");
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = _clock.UtcNow;

        if (PasswordHasher.NeedsRehash(user.PasswordIterations))
        {
            var (hash, salt, iterations) = PasswordHasher.Hash(password);
            user.PasswordHash = hash;
            user.PasswordSalt = salt;
            user.PasswordIterations = iterations;
        }

        await _db.SaveChangesAsync(ct);
        return (user, null);
    }

    public async Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword,
        CancellationToken ct = default)
    {
        ValidatePassword(newPassword);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User not found.");

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
            throw new DomainValidationException("The current password is incorrect.");

        var (hash, salt, iterations) = PasswordHasher.Hash(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordIterations = iterations;
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_currentUser, Permission.ManageUsers);
        ValidatePassword(newPassword);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User not found.");

        var (hash, salt, iterations) = PasswordHasher.Hash(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordIterations = iterations;
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateProfileAsync(Guid userId, string displayName, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User not found.");
        user.DisplayName = displayName.Trim();
        await _db.SaveChangesAsync(ct);
    }

    public async Task<CompanyMembership> AddMemberAsync(Guid companyId, string email, string displayName,
        UserRole role, string temporaryPassword, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_currentUser, Permission.ManageUsers);
        ValidateEmail(email);
        ValidatePassword(temporaryPassword);

        var normalized = email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null)
        {
            var (hash, salt, iterations) = PasswordHasher.Hash(temporaryPassword);
            user = new User
            {
                Email = normalized,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName.Trim(),
                PasswordHash = hash,
                PasswordSalt = salt,
                PasswordIterations = iterations,
                IsActive = true,
                CreatedAt = _clock.UtcNow
            };
            _db.Users.Add(user);
        }

        var existing = await _db.CompanyMemberships
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == user.Id, ct);
        if (existing is not null)
        {
            existing.Role = role;
            existing.IsActive = true;
        }
        else
        {
            existing = new CompanyMembership
            {
                CompanyId = companyId,
                UserId = user.Id,
                Role = role,
                IsActive = true,
                CreatedAt = _clock.UtcNow
            };
            _db.CompanyMemberships.Add(existing);
        }

        _audit.Record(companyId, "MemberAdded", nameof(CompanyMembership), existing.Id.ToString(),
            $"{normalized} as {role}");
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task UpdateMemberRoleAsync(Guid companyId, Guid userId, UserRole role,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_currentUser, Permission.ManageUsers);
        var membership = await _db.CompanyMemberships
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == userId, ct)
            ?? throw new NotFoundException("Membership not found.");

        if (membership.Role == UserRole.Owner && role != UserRole.Owner)
        {
            var owners = await _db.CompanyMemberships.CountAsync(m =>
                m.CompanyId == companyId && m.Role == UserRole.Owner && m.IsActive, ct);
            if (owners <= 1)
                throw new DomainValidationException("A company must keep at least one active owner.");
        }

        membership.Role = role;
        _audit.Record(companyId, "MemberRoleChanged", nameof(CompanyMembership), membership.Id.ToString(),
            $"{userId} \u2192 {role}");
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetMemberActiveAsync(Guid companyId, Guid userId, bool active,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_currentUser, Permission.ManageUsers);
        var membership = await _db.CompanyMemberships
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == userId, ct)
            ?? throw new NotFoundException("Membership not found.");

        if (!active && membership.Role == UserRole.Owner)
        {
            var owners = await _db.CompanyMemberships.CountAsync(m =>
                m.CompanyId == companyId && m.Role == UserRole.Owner && m.IsActive, ct);
            if (owners <= 1) throw new DomainValidationException("A company must keep at least one active owner.");
        }

        membership.IsActive = active;
        _audit.Record(companyId, active ? "MemberActivated" : "MemberDeactivated",
            nameof(CompanyMembership), membership.Id.ToString());
        await _db.SaveChangesAsync(ct);
    }

    public async Task<User?> FindByEmailAsync(string email, CancellationToken ct = default) =>
        await _db.Users.FirstOrDefaultAsync(u => u.Email == email.Trim().ToLowerInvariant(), ct);

    public async Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<CompanyMembership?> GetMembershipAsync(Guid companyId, Guid userId,
        CancellationToken ct = default) =>
        await _db.CompanyMemberships
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == userId && m.IsActive, ct);

    public static void ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > 256 ||
            email.Any(char.IsWhiteSpace))
            throw new DomainValidationException("Enter a valid email address.");
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
            throw new DomainValidationException("Use a password of at least 10 characters.");
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new DomainValidationException("The password must include a letter and a number.");
    }
}
