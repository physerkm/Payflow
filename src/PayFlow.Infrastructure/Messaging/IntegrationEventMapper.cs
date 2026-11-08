using System.Diagnostics;
using PayFlow.Application.Abstractions;
using PayFlow.Contracts;
using PayFlow.Contracts.Events;
using PayFlow.Domain.Common;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Refunds;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Infrastructure.Messaging;

/// <summary>Translates domain events into versioned integration events stored in the outbox.</summary>
public interface IIntegrationEventMapper
{
    /// <returns>An outbox message, or <c>null</c> when the domain event is internal only.</returns>
    OutboxMessage? Map(IDomainEvent domainEvent, DateTimeOffset now);
}

internal sealed class IntegrationEventMapper(ICorrelationContext correlation) : IIntegrationEventMapper
{
    public OutboxMessage? Map(IDomainEvent domainEvent, DateTimeOffset now) => domainEvent switch
    {
        PaymentRequested e => Create(e.TenantId, e.OccurredAt, new PaymentRequestedV1(e.PaymentId, e.PaymentAttemptId), now, availableAt: e.AvailableAt),
        PaymentSucceeded e => Create(e.TenantId, e.OccurredAt, new PaymentSucceededV1(e.PaymentId, e.OrderId, e.AmountMinor, e.Currency), now),
        PaymentFailed e => Create(e.TenantId, e.OccurredAt, new PaymentFailedV1(e.PaymentId, e.OrderId, e.FailureCode), now),
        RefundRequested e => Create(e.TenantId, e.OccurredAt, new RefundRequestedV1(e.RefundId, e.PaymentId), now),
        RefundSucceeded e => Create(e.TenantId, e.OccurredAt, new RefundSucceededV1(e.RefundId, e.PaymentId, e.AmountMinor, e.Currency), now),
        RefundFailed e => Create(e.TenantId, e.OccurredAt, new RefundFailedV1(e.RefundId, e.PaymentId, e.FailureCode), now),
        _ => null,
    };

    private OutboxMessage Create<T>(Guid tenantId, DateTimeOffset occurredAt, T payload, DateTimeOffset now, DateTimeOffset? availableAt = null)
        where T : IIntegrationEvent
    {
        var messageId = Guid.CreateVersion7(now);
        var envelope = MessageEnvelope.Create(messageId, occurredAt, tenantId, correlation.CorrelationId, payload);
        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            MessageId = messageId,
            TenantId = tenantId,
            EventType = T.EventType,
            Payload = envelope.Serialize(),
            Status = OutboxStatus.Pending,
            OccurredAt = occurredAt,
            CreatedAt = now,
            AvailableAt = availableAt is { } at && at > now ? at : now,
            CorrelationId = correlation.CorrelationId,
            TraceParent = Activity.Current?.Id,
        };
    }
}
