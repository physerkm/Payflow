using PayFlow.Contracts;

namespace PayFlow.Infrastructure.Messaging;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>"RabbitMq" (default) or "InMemory" (single-process development and tests).</summary>
    public string Transport { get; set; } = "RabbitMq";

    public RabbitMqOptions RabbitMq { get; set; } = new();

    public int OutboxBatchSize { get; set; } = 50;

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Transport-level redeliveries (with growing delay) before a message is dead-lettered.</summary>
    public int MaxDeliveryRetries { get; set; } = 3;

    public bool UseRabbitMq => string.Equals(Transport, "RabbitMq", StringComparison.OrdinalIgnoreCase);
}

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";

    public ushort PrefetchCount { get; set; } = 16;
}

/// <summary>A message read from the outbox, ready to hand to the broker.</summary>
public sealed record OutgoingMessage(Guid MessageId, string EventType, string Body, Guid? TenantId, string? CorrelationId, string? TraceParent);

public interface IMessageBus
{
    /// <summary>Publishes durably. Completes only after the broker confirmed the message (publisher confirms).</summary>
    Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>Routes a received message to its consumer. Implemented by the Worker (composition root).</summary>
public interface IMessageDispatcher
{
    Task DispatchAsync(MessageEnvelope envelope, string? traceParent, CancellationToken cancellationToken);
}

/// <summary>Errors that will never succeed on redelivery; such messages go straight to the dead-letter queue.</summary>
public sealed class NonRetryableMessageException : Exception
{
    public NonRetryableMessageException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public NonRetryableMessageException()
        : base("The message cannot be processed.")
    {
    }

    public NonRetryableMessageException(string message)
        : base(message)
    {
    }
}

/// <summary>Queue topology: one durable queue per consumer, each with retry queues and a dead-letter queue.</summary>
public static class Topology
{
    public const string EventsExchange = "payflow.events";
    public const string RetryExchange = "payflow.retry";
    public const string DeadLetterExchange = "payflow.dlx";

    public const string PaymentsQueue = "payflow.payments";
    public const string RefundsQueue = "payflow.refunds";
    public const string NotificationsQueue = "payflow.notifications";

    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    public static IReadOnlyDictionary<string, string[]> Bindings { get; } = new Dictionary<string, string[]>
    {
        [PaymentsQueue] = [Contracts.Events.PaymentRequestedV1.EventType],
        [RefundsQueue] = [Contracts.Events.RefundRequestedV1.EventType],
        [NotificationsQueue] =
        [
            Contracts.Events.PaymentSucceededV1.EventType,
            Contracts.Events.PaymentFailedV1.EventType,
            Contracts.Events.RefundSucceededV1.EventType,
            Contracts.Events.RefundFailedV1.EventType,
        ],
    };

    public static string RetryQueue(string queue, int level) => $"{queue}.retry.{level}";

    public static string DeadLetterQueue(string queue) => $"{queue}.dlq";
}
