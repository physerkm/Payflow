using PayFlow.Domain.Common;

namespace PayFlow.Domain.Payments;

public enum PaymentAttemptStatus
{
    /// <summary>Waiting to be sent to the provider (possibly scheduled in the future for backoff).</summary>
    Queued,

    /// <summary>Sent (or about to be sent) to the provider; outcome not recorded yet.</summary>
    Processing,

    /// <summary>Provider accepted the request and will confirm asynchronously.</summary>
    ProviderPending,
    Succeeded,

    /// <summary>Definitive rejection, e.g. card declined.</summary>
    Declined,

    /// <summary>Transient/technical error; no charge happened.</summary>
    Failed,

    /// <summary>Timeout/ambiguous; requires reconciliation.</summary>
    Unknown,
    Cancelled,
}

public enum AttemptTrigger
{
    Initial,
    AutomaticRetry,
    ManualRetry,
}

/// <summary>One call to the payment provider. Its Id is used as the provider idempotency key.</summary>
public sealed class PaymentAttempt : Entity, ITenantScoped
{
    private PaymentAttempt()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PaymentId { get; private set; }

    public int AttemptNumber { get; private set; }

    public AttemptTrigger Trigger { get; private set; }

    public string Provider { get; private set; } = default!;

    public string? ProviderReference { get; private set; }

    public PaymentAttemptStatus Status { get; private set; }

    public string? FailureCode { get; private set; }

    public DateTimeOffset ScheduledAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Reference used for provider idempotency and for status lookups when no provider reference exists.</summary>
    public string MerchantReference => Id.ToString("N");

    public bool IsResolved => Status is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.Declined
        or PaymentAttemptStatus.Failed or PaymentAttemptStatus.Cancelled;

    internal static PaymentAttempt Create(Payment payment, int attemptNumber, AttemptTrigger trigger, DateTimeOffset scheduledAt, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = payment.TenantId,
        PaymentId = payment.Id,
        AttemptNumber = attemptNumber,
        Trigger = trigger,
        Provider = payment.Provider,
        Status = PaymentAttemptStatus.Queued,
        ScheduledAt = scheduledAt,
        CreatedAt = now,
        UpdatedAt = now,
    };

    internal void MarkProcessing(DateTimeOffset now)
    {
        Transition(PaymentAttemptStatus.Processing, now, PaymentAttemptStatus.Queued);
        StartedAt = now;
    }

    internal void MarkSucceeded(string providerReference, DateTimeOffset now)
    {
        Transition(PaymentAttemptStatus.Succeeded, now, PaymentAttemptStatus.Processing, PaymentAttemptStatus.ProviderPending, PaymentAttemptStatus.Unknown);
        ProviderReference = providerReference;
        FailureCode = null;
        CompletedAt = now;
    }

    internal void MarkDeclined(string? providerReference, string failureCode, DateTimeOffset now)
    {
        Transition(PaymentAttemptStatus.Declined, now, PaymentAttemptStatus.Processing, PaymentAttemptStatus.ProviderPending, PaymentAttemptStatus.Unknown);
        ProviderReference = providerReference ?? ProviderReference;
        FailureCode = failureCode;
        CompletedAt = now;
    }

    internal void MarkProviderPending(string providerReference, DateTimeOffset now)
    {
        Transition(PaymentAttemptStatus.ProviderPending, now, PaymentAttemptStatus.Processing);
        ProviderReference = providerReference;
    }

    internal void MarkUnknown(string? providerReference, DateTimeOffset now)
    {
        Transition(PaymentAttemptStatus.Unknown, now, PaymentAttemptStatus.Processing);
        ProviderReference = providerReference ?? ProviderReference;
    }

    internal void MarkFailed(string failureCode, DateTimeOffset now)
    {
        Transition(PaymentAttemptStatus.Failed, now, PaymentAttemptStatus.Processing, PaymentAttemptStatus.Unknown);
        FailureCode = failureCode;
        CompletedAt = now;
    }

    internal void MarkCancelled(DateTimeOffset now)
    {
        Transition(PaymentAttemptStatus.Cancelled, now, PaymentAttemptStatus.Queued);
        CompletedAt = now;
    }

    private void Transition(PaymentAttemptStatus target, DateTimeOffset now, params PaymentAttemptStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw DomainException.InvalidTransition("payment_attempt", Status.ToString(), target.ToString());
        }

        Status = target;
        UpdatedAt = now;
    }
}
