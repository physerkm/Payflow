namespace PayFlow.Application.Abstractions;

/// <summary>
/// Abstraction over an external payment provider. The first implementation is a mock provider that moves
/// no real money. Real providers must be integrated only through their sandbox / hosted checkout and
/// tokenization APIs - PayFlow never handles raw card data.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken cancellationToken);

    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up a charge by provider reference, or by merchant reference (idempotency key) when the original
    /// call timed out before a provider reference was returned.
    /// </summary>
    Task<PaymentStatusResult> GetPaymentStatusAsync(string providerReference, CancellationToken cancellationToken);
}

/// <param name="IdempotencyKey">Stable per attempt; the provider must return the same result for repeated calls.</param>
public sealed record PaymentRequest(
    string IdempotencyKey,
    Guid PaymentId,
    long AmountMinor,
    string Currency,
    string PaymentMethodToken,
    int AttemptNumber);

public enum ChargeOutcome
{
    Succeeded,
    Declined,

    /// <summary>Accepted, final result will arrive by webhook or status polling.</summary>
    Pending,

    /// <summary>No answer in time: outcome unknown, must be reconciled.</summary>
    Timeout,

    /// <summary>Technical error before the charge was executed; safe to retry.</summary>
    TransientError,

    /// <summary>The circuit breaker is open; the provider was not called.</summary>
    CircuitOpen,
}

public sealed record PaymentResult(ChargeOutcome Outcome, string? ProviderReference = null, string? FailureCode = null);

public sealed record RefundRequest(string IdempotencyKey, string ProviderPaymentReference, long AmountMinor, string Currency);

public enum RefundOutcome
{
    Succeeded,
    Declined,
    TransientError,
}

public sealed record RefundResult(RefundOutcome Outcome, string? ProviderReference = null, string? FailureCode = null);

public enum ProviderPaymentStatus
{
    Succeeded,
    Declined,
    Pending,
    NotFound,
}

public sealed record PaymentStatusResult(
    ProviderPaymentStatus Status,
    string? ProviderReference = null,
    long? AmountMinor = null,
    string? Currency = null,
    string? FailureCode = null);
