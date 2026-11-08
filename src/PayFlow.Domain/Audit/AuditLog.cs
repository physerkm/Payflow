using PayFlow.Domain.Common;

namespace PayFlow.Domain.Audit;

/// <summary>
/// Immutable record of a security- or finance-relevant action. Must never contain secrets,
/// tokens, card data or full webhook secrets.
/// </summary>
public sealed class AuditLog : Entity
{
    private AuditLog()
    {
    }

    /// <summary>Null for platform-level events such as login.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>Null for system actions (worker, webhook, reconciliation).</summary>
    public Guid? ActorUserId { get; private set; }

    public string Action { get; private set; } = default!;

    public string ResourceType { get; private set; } = default!;

    public string? ResourceId { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>Small, non-sensitive JSON document with extra context.</summary>
    public string? Metadata { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static AuditLog Create(
        Guid? tenantId,
        Guid? actorUserId,
        string action,
        string resourceType,
        string? resourceId,
        string? correlationId,
        string? metadata,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            ActorUserId = actorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            CorrelationId = correlationId,
            Metadata = metadata,
            OccurredAt = now,
        };
}
