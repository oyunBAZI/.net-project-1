namespace AxiomLedger.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

/// <summary>An entity whose data is isolated to a single company (tenant).</summary>
public abstract class CompanyEntity : Entity
{
    public Guid CompanyId { get; set; }
}

public abstract class AuditableEntity : CompanyEntity
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByUserId { get; set; }
}
