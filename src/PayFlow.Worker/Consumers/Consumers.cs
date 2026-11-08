using System.Diagnostics;
using System.Text.Json;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Notifications;
using PayFlow.Application.Payments.Processing;
using PayFlow.Contracts;
using PayFlow.Contracts.Events;
using PayFlow.Domain.Common;
using PayFlow.Infrastructure.Messaging;
using PayFlow.Infrastructure.Observability;

namespace PayFlow.Worker.Consumers;

public interface IMessageConsumer
{
    /// <summary>Stable consumer name used as the inbox deduplication scope.</summary>
    string Name { get; }

    IReadOnlyCollection<string> EventTypes { get; }

    Task ConsumeAsync(MessageEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Creates a DI scope per message, establishes the tenant context from the message (the consumer then only
/// sees that tenant's rows), continues the producer's trace and routes to the consumer for the event type.
/// </summary>
internal sealed class MessageDispatcher(IServiceScopeFactory scopes, ILogger<MessageDispatcher> logger) : IMessageDispatcher
{
    public async Task DispatchAsync(MessageEnvelope envelope, string? traceParent, CancellationToken cancellationToken)
    {
        ActivityContext.TryParse(traceParent, null, out var parent);
        using var activity = PayFlowTelemetry.ActivitySource.StartActivity($"consume {envelope.EventType}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.message.id", envelope.MessageId.ToString());
        activity?.SetTag("payflow.tenant_id", envelope.TenantId?.ToString());

        if (envelope.TenantId is not { } tenantId)
        {
            throw new NonRetryableMessageException($"Message {envelope.MessageId} has no tenant id.");
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<TenantContext>().SetSystem(tenantId);
        services.GetRequiredService<CorrelationContext>().CorrelationId = envelope.CorrelationId ?? envelope.MessageId.ToString("N");

        var consumer = services.GetServices<IMessageConsumer>().FirstOrDefault(c => c.EventTypes.Contains(envelope.EventType))
            ?? throw new NonRetryableMessageException($"No consumer is registered for {envelope.EventType}.");

        using (logger.BeginScope(new Dictionary<string, object?>
               {
                   ["MessageId"] = envelope.MessageId,
                   ["EventType"] = envelope.EventType,
                   ["TenantId"] = tenantId,
                   ["CorrelationId"] = envelope.CorrelationId,
                   ["Consumer"] = consumer.Name,
               }))
        {
            try
            {
                await consumer.ConsumeAsync(envelope, cancellationToken);
            }
            catch (Exception ex) when (ex is NotFoundException or DomainException or JsonException)
            {
                // Retrying cannot fix a message that references data outside its tenant or violates the state machine.
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw new NonRetryableMessageException($"Message {envelope.MessageId} cannot be processed: {ex.Message}", ex);
            }
        }
    }
}

internal sealed class PaymentRequestedConsumer(ProcessPaymentAttemptHandler handler) : IMessageConsumer
{
    public string Name => "payments.process-attempt";

    public IReadOnlyCollection<string> EventTypes { get; } = [PaymentRequestedV1.EventType];

    public async Task ConsumeAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = envelope.GetPayload<PaymentRequestedV1>();
        await handler.HandleAsync(new MessageContext(envelope.MessageId, Name), payload.PaymentId, payload.PaymentAttemptId, cancellationToken);
    }
}

internal sealed class RefundRequestedConsumer(ProcessRefundHandler handler) : IMessageConsumer
{
    public string Name => "refunds.process";

    public IReadOnlyCollection<string> EventTypes { get; } = [RefundRequestedV1.EventType];

    public async Task ConsumeAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = envelope.GetPayload<RefundRequestedV1>();
        await handler.HandleAsync(new MessageContext(envelope.MessageId, Name), payload.RefundId, payload.PaymentId, cancellationToken);
    }
}

internal sealed class NotificationConsumer(NotificationHandler handler) : IMessageConsumer
{
    public string Name => "notifications.in-app";

    public IReadOnlyCollection<string> EventTypes { get; } =
        [PaymentSucceededV1.EventType, PaymentFailedV1.EventType, RefundSucceededV1.EventType, RefundFailedV1.EventType];

    public Task ConsumeAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var message = new MessageContext(envelope.MessageId, Name);
        if (envelope.EventType == PaymentSucceededV1.EventType)
        {
            var e = envelope.GetPayload<PaymentSucceededV1>();
            return handler.PaymentSucceededAsync(message, e.PaymentId, e.OrderId, e.AmountMinor, e.Currency, cancellationToken);
        }

        if (envelope.EventType == PaymentFailedV1.EventType)
        {
            var e = envelope.GetPayload<PaymentFailedV1>();
            return handler.PaymentFailedAsync(message, e.PaymentId, e.OrderId, e.FailureCode, cancellationToken);
        }

        if (envelope.EventType == RefundSucceededV1.EventType)
        {
            var e = envelope.GetPayload<RefundSucceededV1>();
            return handler.RefundSucceededAsync(message, e.RefundId, e.PaymentId, e.AmountMinor, e.Currency, cancellationToken);
        }

        var failed = envelope.GetPayload<RefundFailedV1>();
        return handler.RefundFailedAsync(message, failed.RefundId, failed.PaymentId, failed.FailureCode, cancellationToken);
    }
}
