using PayFlow.Domain.Payments;

namespace PayFlow.Application.Abstractions;

/// <summary>Identifies the integration message being processed by a consumer.</summary>
public sealed record MessageContext(Guid MessageId, string ConsumerName);

/// <summary>
/// Consumer-side deduplication (Inbox pattern). <see cref="TryRegisterAsync"/> must run inside the same
/// transaction as the business effect, so a message is applied exactly once even with at-least-once delivery.
/// </summary>
public interface IInbox
{
    Task<bool> HasProcessedAsync(MessageContext message, CancellationToken cancellationToken);

    /// <returns><c>false</c> when the message was already processed by this consumer.</returns>
    Task<bool> TryRegisterAsync(MessageContext message, CancellationToken cancellationToken);
}

public sealed record AttemptLocator(Guid TenantId, Guid PaymentId, Guid AttemptId, PaymentAttemptStatus Status);

public sealed record RefundLocator(Guid TenantId, Guid RefundId, Guid PaymentId);

/// <summary>
/// Cross-tenant lookups used only by system processes (webhooks, reconciliation). They return identifiers
/// only; the actual work then runs in a normal tenant-scoped context for the located tenant.
/// </summary>
public interface ISystemPaymentQueries
{
    Task<AttemptLocator?> FindAttemptByProviderReferenceAsync(string provider, string providerReference, CancellationToken cancellationToken);

    Task<IReadOnlyList<AttemptLocator>> FindAttemptsNeedingReconciliationAsync(
        DateTimeOffset unknownBefore,
        DateTimeOffset providerPendingBefore,
        DateTimeOffset processingBefore,
        int limit,
        CancellationToken cancellationToken);
}
