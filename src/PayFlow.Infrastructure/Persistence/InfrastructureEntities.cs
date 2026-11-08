using Microsoft.AspNetCore.Identity;

namespace PayFlow.Infrastructure.Persistence;

/// <summary>Platform-global user (ASP.NET Core Identity). Tenant roles live in tenant_memberships.</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = default!;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}

public enum OutboxStatus
{
    Pending,
    Published,
}

/// <summary>An integration event stored in the same transaction as the business change (Transactional Outbox).</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public Guid? TenantId { get; set; }

    public string EventType { get; set; } = default!;

    /// <summary>Full JSON envelope (see PayFlow.Contracts.MessageEnvelope).</summary>
    public string Payload { get; set; } = default!;

    public OutboxStatus Status { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Not published before this time; used for delayed retries (backoff) of payment attempts.</summary>
    public DateTimeOffset AvailableAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int PublishAttempts { get; set; }

    public string? LastError { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>W3C traceparent of the request that produced the event, so the worker continues the same trace.</summary>
    public string? TraceParent { get; set; }
}

/// <summary>A message already applied by a consumer (Inbox pattern).</summary>
public sealed class InboxMessage
{
    public string ConsumerName { get; set; } = default!;

    public Guid MessageId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Operation { get; set; } = default!;

    public string Key { get; set; } = default!;

    public string RequestHash { get; set; } = default!;

    public int? ResponseStatus { get; set; }

    public string? ResponseBody { get; set; }

    public string? ResponseLocation { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Provider-global webhook log; unique (provider, provider_event_id) gives deduplication.</summary>
public sealed class WebhookEvent
{
    public Guid Id { get; set; }

    public string Provider { get; set; } = default!;

    public string ProviderEventId { get; set; } = default!;

    public string Payload { get; set; } = default!;

    public string Status { get; set; } = default!;

    public string? Error { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the raw token. The raw value is never stored.</summary>
    public string TokenHash { get; set; } = default!;

    /// <summary>All tokens produced by rotation from one login share a family (revoked together on reuse).</summary>
    public Guid FamilyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public Guid? ReplacedById { get; set; }
}

/// <summary>Per-tenant counters (e.g. human-readable order numbers).</summary>
public sealed class TenantSequence
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = default!;

    public long Value { get; set; }
}

/// <summary>
/// Ledger of the simulated payment provider. It represents state that would live at the external provider,
/// which is why it is provider-global and accessed only by the mock adapter.
/// </summary>
public sealed class MockProviderTransaction
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = default!;

    public string IdempotencyKey { get; set; } = default!;

    public string ProviderReference { get; set; } = default!;

    public string? RelatedReference { get; set; }

    public string? PaymentMethodToken { get; set; }

    public long AmountMinor { get; set; }

    public string Currency { get; set; } = default!;

    public string Status { get; set; } = default!;

    public string? FailureCode { get; set; }

    public string? WebhookStatus { get; set; }

    public int WebhookAttempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}
