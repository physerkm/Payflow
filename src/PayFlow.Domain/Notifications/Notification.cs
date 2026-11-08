using PayFlow.Domain.Common;

namespace PayFlow.Domain.Notifications;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>In-app notification about a payment/order event (MVP notification channel).</summary>
public sealed class Notification : Entity, ITenantScoped
{
    private Notification()
    {
    }

    public Guid TenantId { get; private set; }

    public string Type { get; private set; } = default!;

    public NotificationSeverity Severity { get; private set; }

    public string Title { get; private set; } = default!;

    public string Message { get; private set; } = default!;

    public string? ResourceType { get; private set; }

    public Guid? ResourceId { get; private set; }

    /// <summary>Id of the integration message that produced this notification (deduplication).</summary>
    public Guid SourceMessageId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Notification Create(
        Guid tenantId,
        string type,
        NotificationSeverity severity,
        string title,
        string message,
        string? resourceType,
        Guid? resourceId,
        Guid sourceMessageId,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Type = type,
            Severity = severity,
            Title = title,
            Message = message,
            ResourceType = resourceType,
            ResourceId = resourceId,
            SourceMessageId = sourceMessageId,
            CreatedAt = now,
        };
}
