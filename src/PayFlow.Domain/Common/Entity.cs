namespace PayFlow.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; }
}

/// <summary>
/// Consistency boundary. Collects domain events that the persistence layer converts into
/// outbox messages inside the same database transaction.
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>Optimistic concurrency token (row_version). Incremented by the persistence layer on every update.</summary>
    public long RowVersion { get; private set; } = 1;

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>Marks an entity whose rows belong to exactly one tenant.</summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
