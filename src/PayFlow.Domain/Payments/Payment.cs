using PayFlow.Domain.Common;

namespace PayFlow.Domain.Payments;

public enum PaymentStatus
{
    Created,
    Pending,

    /// <summary>The provider outcome is not known (e.g. timeout). Must be reconciled before any new charge.</summary>
    Unknown,
    Succeeded,
    Failed,
    Cancelled,
    PartiallyRefunded,
    Refunded,
}

/// <summary>
/// A payment intent for an order. Each call to the provider is a separate <see cref="PaymentAttempt"/>.
/// The amount is taken from the order on the server; clients never send it.
/// </summary>
public sealed class Payment : AggregateRoot, ITenantScoped
{
    /// <summary>Automatic attempts per run (initial or manual retry) for transient provider errors.</summary>
    public const int MaxAutomaticAttemptsPerRun = 3;

    /// <summary>Hard cap on provider attempts for one payment, including manual retries.</summary>
    public const int MaxTotalAttempts = 10;

    private readonly List<PaymentAttempt> _attempts = [];

    private Payment()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public PaymentStatus Status { get; private set; }

    public long AmountMinor { get; private set; }

    public string Currency { get; private set; } = default!;

    public string Provider { get; private set; } = default!;

    /// <summary>Provider-issued payment method token (test tokens only, never card data).</summary>
    public string PaymentMethodToken { get; private set; } = default!;

    public string? FailureCode { get; private set; }

    /// <summary>Sum of succeeded refunds.</summary>
    public long RefundedAmountMinor { get; private set; }

    /// <summary>Sum of refunds that were requested but are not completed yet; reserved against the refund limit.</summary>
    public long PendingRefundAmountMinor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? SucceededAt { get; private set; }

    public DateTimeOffset? FailedAt { get; private set; }

    public IReadOnlyList<PaymentAttempt> Attempts => _attempts;

    public PaymentAttempt? CurrentAttempt => _attempts.MaxBy(a => a.AttemptNumber);

    public Money Amount => new(AmountMinor, Currency);

    public bool IsSettledSuccess => Status is PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded;

    public long RefundableAmountMinor => AmountMinor - RefundedAmountMinor - PendingRefundAmountMinor;

    public static Payment Initiate(Guid tenantId, Guid orderId, Money amount, string paymentMethodToken, string provider, DateTimeOffset now)
    {
        if (amount.AmountMinor <= 0)
        {
            throw new DomainException("payment_amount_invalid", "Payment amount must be positive.");
        }

        var payment = new Payment
        {
            Id = Guid.CreateVersion7(now),
            TenantId = Guard.NotEmpty(tenantId, "tenant_required"),
            OrderId = Guard.NotEmpty(orderId, "order_required"),
            Status = PaymentStatus.Created,
            AmountMinor = amount.AmountMinor,
            Currency = amount.Currency,
            Provider = Guard.NotEmpty(provider, "payment_provider_required", "Provider is required", 32),
            PaymentMethodToken = Guard.NotEmpty(paymentMethodToken, "payment_method_required", "Payment method token is required", 128),
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Created -> Pending: the first attempt is queued in the same transaction (published via the outbox).
        payment.Status = PaymentStatus.Pending;
        payment.QueueAttempt(AttemptTrigger.Initial, now, now);
        return payment;
    }

    public PaymentAttempt GetAttempt(Guid attemptId) =>
        _attempts.SingleOrDefault(a => a.Id == attemptId)
        ?? throw new DomainException("payment_attempt_not_found", "The payment attempt does not belong to this payment.", DomainErrorKind.Conflict);

    /// <summary>
    /// Called by the worker before contacting the provider. Returns <c>true</c> when the attempt must be sent.
    /// A redelivered message for an attempt already in <see cref="PaymentAttemptStatus.Processing"/> is re-sent
    /// with the same provider idempotency key, so the provider never charges twice.
    /// </summary>
    public bool BeginAttempt(Guid attemptId, DateTimeOffset now)
    {
        var attempt = GetAttempt(attemptId);
        if (Status != PaymentStatus.Pending || attempt.Id != CurrentAttempt!.Id)
        {
            return false;
        }

        switch (attempt.Status)
        {
            case PaymentAttemptStatus.Queued:
                attempt.MarkProcessing(now);
                UpdatedAt = now;
                return true;
            case PaymentAttemptStatus.Processing:
                return true;
            default:
                return false;
        }
    }

    public void RecordSucceeded(Guid attemptId, string providerReference, DateTimeOffset now)
    {
        var attempt = GetAttempt(attemptId);
        if (IsSettledSuccess)
        {
            if (attempt.Status == PaymentAttemptStatus.Succeeded)
            {
                return; // duplicate confirmation (redelivery, webhook + reconciliation)
            }

            throw new DomainException("payment_already_succeeded", "The payment has already succeeded with another attempt.", DomainErrorKind.Conflict);
        }

        EnsureStatus(PaymentStatus.Succeeded, PaymentStatus.Pending, PaymentStatus.Unknown);
        attempt.MarkSucceeded(providerReference, now);
        Status = PaymentStatus.Succeeded;
        FailureCode = null;
        SucceededAt = now;
        UpdatedAt = now;
        Raise(new PaymentSucceeded(Id, OrderId, TenantId, AmountMinor, Currency, now));
    }

    /// <summary>Definitive decline (e.g. card declined). Never retried automatically.</summary>
    public void RecordDeclined(Guid attemptId, string? providerReference, string failureCode, DateTimeOffset now)
    {
        var attempt = GetAttempt(attemptId);
        if (Status == PaymentStatus.Failed && attempt.Status == PaymentAttemptStatus.Declined)
        {
            return;
        }

        if (IsSettledSuccess)
        {
            throw new DomainException("payment_already_succeeded", "A succeeded payment can never become failed.", DomainErrorKind.Conflict);
        }

        EnsureStatus(PaymentStatus.Failed, PaymentStatus.Pending, PaymentStatus.Unknown);
        attempt.MarkDeclined(providerReference, failureCode, now);
        Fail(failureCode, now);
    }

    /// <summary>The provider accepted the charge but will confirm it later (webhook or status polling).</summary>
    public void RecordProviderPending(Guid attemptId, string providerReference, DateTimeOffset now)
    {
        var attempt = GetAttempt(attemptId);
        if (attempt.Status == PaymentAttemptStatus.ProviderPending)
        {
            return;
        }

        EnsureStatus(PaymentStatus.Pending, PaymentStatus.Pending);
        attempt.MarkProviderPending(providerReference, now);
        UpdatedAt = now;
    }

    /// <summary>Timeout or ambiguous response: the outcome must be reconciled, never assumed failed.</summary>
    public void RecordUnknown(Guid attemptId, string? providerReference, DateTimeOffset now)
    {
        var attempt = GetAttempt(attemptId);
        if (Status == PaymentStatus.Unknown && attempt.Status == PaymentAttemptStatus.Unknown)
        {
            return;
        }

        EnsureStatus(PaymentStatus.Unknown, PaymentStatus.Pending);
        attempt.MarkUnknown(providerReference, now);
        Status = PaymentStatus.Unknown;
        UpdatedAt = now;
    }

    /// <summary>
    /// Transient/technical provider error where no charge happened. Schedules a new attempt (with backoff
    /// computed by the caller) until the per-run limit is reached, then fails the payment.
    /// </summary>
    /// <returns>The newly queued attempt, or <c>null</c> when the payment was failed instead.</returns>
    public PaymentAttempt? RecordTransientFailure(Guid attemptId, string failureCode, DateTimeOffset nextAttemptAt, DateTimeOffset now)
    {
        var attempt = GetAttempt(attemptId);
        EnsureStatus(PaymentStatus.Pending, PaymentStatus.Pending, PaymentStatus.Unknown);
        attempt.MarkFailed(failureCode, now);

        if (AttemptsInCurrentRun() < MaxAutomaticAttemptsPerRun && _attempts.Count < MaxTotalAttempts)
        {
            Status = PaymentStatus.Pending;
            return QueueAttempt(AttemptTrigger.AutomaticRetry, nextAttemptAt, now);
        }

        Fail("provider_unavailable", now);
        return null;
    }

    /// <summary>Manual retry of a failed payment: opens a new attempt instead of reusing the old one.</summary>
    public PaymentAttempt Retry(string? newPaymentMethodToken, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Failed)
        {
            throw new DomainException("payment_not_retryable", $"Only failed payments can be retried (current status: {Status}).", DomainErrorKind.Conflict);
        }

        if (_attempts.Count >= MaxTotalAttempts)
        {
            throw new DomainException("payment_retry_limit", $"A payment can have at most {MaxTotalAttempts} attempts. Start a new payment instead.", DomainErrorKind.Conflict);
        }

        if (!string.IsNullOrWhiteSpace(newPaymentMethodToken))
        {
            PaymentMethodToken = Guard.NotEmpty(newPaymentMethodToken, "payment_method_required", "Payment method token is required", 128);
        }

        Status = PaymentStatus.Pending;
        FailureCode = null;
        FailedAt = null;
        return QueueAttempt(AttemptTrigger.ManualRetry, now, now);
    }

    /// <summary>Cancels a payment whose current attempt has not been sent to the provider yet.</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Cancelled)
        {
            return;
        }

        if (Status != PaymentStatus.Pending || CurrentAttempt?.Status != PaymentAttemptStatus.Queued)
        {
            throw new DomainException(
                "payment_not_cancellable",
                "The payment is already being processed by the provider and cannot be cancelled.",
                DomainErrorKind.Conflict);
        }

        CurrentAttempt.MarkCancelled(now);
        Status = PaymentStatus.Cancelled;
        UpdatedAt = now;
    }

    /// <summary>Reserves part of the refundable amount. Must run under a row lock on the payment.</summary>
    public void ReserveRefund(long amountMinor, DateTimeOffset now)
    {
        if (!IsSettledSuccess)
        {
            throw new DomainException("payment_not_refundable", "Only succeeded payments can be refunded.", DomainErrorKind.Conflict);
        }

        if (amountMinor <= 0)
        {
            throw new DomainException("refund_amount_invalid", "Refund amount must be positive.");
        }

        if (amountMinor > RefundableAmountMinor)
        {
            throw new DomainException(
                "refund_amount_exceeds_refundable",
                $"Refund amount exceeds the refundable amount ({RefundableAmountMinor} minor units).");
        }

        PendingRefundAmountMinor = checked(PendingRefundAmountMinor + amountMinor);
        UpdatedAt = now;
    }

    public void CompleteRefund(long amountMinor, DateTimeOffset now)
    {
        if (amountMinor <= 0 || amountMinor > PendingRefundAmountMinor)
        {
            throw new DomainException("refund_reservation_mismatch", "Refund amount does not match the reserved amount.", DomainErrorKind.Conflict);
        }

        PendingRefundAmountMinor -= amountMinor;
        RefundedAmountMinor = checked(RefundedAmountMinor + amountMinor);
        Status = RefundedAmountMinor >= AmountMinor ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        UpdatedAt = now;
    }

    public void ReleaseRefund(long amountMinor, DateTimeOffset now)
    {
        if (amountMinor <= 0 || amountMinor > PendingRefundAmountMinor)
        {
            throw new DomainException("refund_reservation_mismatch", "Refund amount does not match the reserved amount.", DomainErrorKind.Conflict);
        }

        PendingRefundAmountMinor -= amountMinor;
        UpdatedAt = now;
    }

    private int AttemptsInCurrentRun()
    {
        var count = 0;
        foreach (var attempt in _attempts.OrderByDescending(a => a.AttemptNumber))
        {
            count++;
            if (attempt.Trigger != AttemptTrigger.AutomaticRetry)
            {
                break;
            }
        }

        return count;
    }

    private PaymentAttempt QueueAttempt(AttemptTrigger trigger, DateTimeOffset scheduledAt, DateTimeOffset now)
    {
        var attempt = PaymentAttempt.Create(this, _attempts.Count + 1, trigger, scheduledAt, now);
        _attempts.Add(attempt);
        UpdatedAt = now;
        Raise(new PaymentRequested(Id, attempt.Id, TenantId, scheduledAt, now));
        return attempt;
    }

    private void Fail(string failureCode, DateTimeOffset now)
    {
        Status = PaymentStatus.Failed;
        FailureCode = failureCode;
        FailedAt = now;
        UpdatedAt = now;
        Raise(new PaymentFailed(Id, OrderId, TenantId, failureCode, now));
    }

    private void EnsureStatus(PaymentStatus target, params PaymentStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw DomainException.InvalidTransition("payment", Status.ToString(), target.ToString());
        }
    }
}

public sealed record PaymentRequested(Guid PaymentId, Guid PaymentAttemptId, Guid TenantId, DateTimeOffset AvailableAt, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentSucceeded(Guid PaymentId, Guid OrderId, Guid TenantId, long AmountMinor, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentFailed(Guid PaymentId, Guid OrderId, Guid TenantId, string FailureCode, DateTimeOffset OccurredAt) : IDomainEvent;
