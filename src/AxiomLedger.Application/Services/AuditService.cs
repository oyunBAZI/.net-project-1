using AxiomLedger.Domain;

namespace AxiomLedger.Application.Services;

/// <summary>Writes an immutable audit record into the current unit of work.</summary>
public sealed class AuditService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;

    public AuditService(IAccountingDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public AuditLog Record(Guid companyId, string action, string entityType, string? entityId = null,
        string? details = null)
    {
        var entry = new AuditLog
        {
            CompanyId = companyId,
            UserId = _user.UserId,
            UserEmail = _user.Email,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details,
            IpAddress = _user.IpAddress,
            OccurredAt = DateTime.UtcNow
        };
        _db.AuditLogs.Add(entry);
        return entry;
    }
}
