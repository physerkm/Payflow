using PayFlow.Domain.Common;
using PayFlow.Domain.Payments;

namespace PayFlow.Domain.Refunds;

public enum RefundStatus
{
    Pending,
    Succeeded,
    Failed,
}

/// <summary>A full or partial refund of a succeeded payment.</summary>
public sealed class Refund : AggregateRoot, ITenantScoped
{
    private Refund()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PaymentId { get; private set; }

    public long AmountMinor { get; private set; }

    public string Currency { get; private set; } = default!;

    public RefundStatus Status { get; private set; }

    public string? Reason { get; private set; }

    public string? ProviderReference { get; private set; }

    public string? FailureCode { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// Creates a refund and reserves its amount on the payment. The caller must hold a row lock on the
    /// payment so that concurrent refund requests cannot exceed the refundable amount.
    /// </summary>
    public static Refund Request(Payment payment, long amountMinor, string? reason, Guid requestedBy, DateTimeOffset now)
    {
        payment.ReserveRefund(amountMinor, now);

        var refund = new Refund
        {
            Id = Guid.CreateVersion7(now),
            TenantId = payment.TenantId,
            PaymentId = payment.Id,
            AmountMinor = amountMinor,
            Currency = payment.Currency,
            Status = RefundStatus.Pending,
            Reason = Guard.Optional(reason, "refund_reason_invalid", 500),
            RequestedByUserId = requestedBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
        refund.Raise(new RefundRequested(refund.Id, payment.Id, refund.TenantId, now));
        return refund;
    }

    public void MarkSucceeded(Payment payment, string providerReference, DateTimeOffset now)
    {
        if (Status == RefundStatus.Succeeded)
        {
            return;
        }

        EnsurePending(RefundStatus.Succeeded);
        EnsureSamePayment(payment);
        payment.CompleteRefund(AmountMinor, now);
        Status = RefundStatus.Succeeded;
        ProviderReference = providerReference;
        CompletedAt = now;
        UpdatedAt = now;
        Raise(new RefundSucceeded(Id, PaymentId, TenantId, AmountMinor, Currency, now));
    }

    public void MarkFailed(Payment payment, string failureCode, DateTimeOffset now)
    {
        if (Status == RefundStatus.Failed)
        {
            return;
        }

        EnsurePending(RefundStatus.Failed);
        EnsureSamePayment(payment);
        payment.ReleaseRefund(AmountMinor, now);
        Status = RefundStatus.Failed;
        FailureCode = failureCode;
        CompletedAt = now;
        UpdatedAt = now;
        Raise(new RefundFailed(Id, PaymentId, TenantId, failureCode, now));
    }

    private void EnsurePending(RefundStatus target)
    {
        if (Status != RefundStatus.Pending)
        {
            throw DomainException.InvalidTransition("refund", Status.ToString(), target.ToString());
        }
    }

    private void EnsureSamePayment(Payment payment)
    {
        if (payment.Id != PaymentId || payment.TenantId != TenantId)
        {
            throw new DomainException("refund_payment_mismatch", "Refund does not belong to the given payment.", DomainErrorKind.Conflict);
        }
    }
}

public sealed record RefundRequested(Guid RefundId, Guid PaymentId, Guid TenantId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RefundSucceeded(Guid RefundId, Guid PaymentId, Guid TenantId, long AmountMinor, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RefundFailed(Guid RefundId, Guid PaymentId, Guid TenantId, string FailureCode, DateTimeOffset OccurredAt) : IDomainEvent;
