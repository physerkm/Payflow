using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Application.Orders;
using PayFlow.Domain.Payments;

namespace PayFlow.Application.Payments.Processing;

public enum ReconciliationResult
{
    Succeeded,
    Declined,
    StillPending,
    RetryScheduled,
    Failed,
    NothingToDo,
}

/// <summary>
/// Resolves attempts whose outcome is not known (timeout), still pending at the provider, or stuck in
/// Processing, by asking the provider for the authoritative status. A new charge is only scheduled when
/// the provider confirms that the original request never arrived.
/// </summary>
public sealed class ReconcilePaymentHandler(
    IPaymentsDb db,
    IUnitOfWork unitOfWork,
    IPaymentProvider provider,
    IOrderPaymentCoordinator orders,
    IAuditTrail audit,
    IOptions<PaymentProcessingOptions> options,
    TimeProvider clock,
    ILogger<ReconcilePaymentHandler> logger)
{
    public async Task<ReconciliationResult> HandleAsync(Guid paymentId, Guid attemptId, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw new NotFoundException("Payment");
        var attempt = payment.GetAttempt(attemptId);
        if (attempt.Status is not (PaymentAttemptStatus.Unknown or PaymentAttemptStatus.ProviderPending or PaymentAttemptStatus.Processing))
        {
            return ReconciliationResult.NothingToDo;
        }

        var status = await provider.GetPaymentStatusAsync(attempt.ProviderReference ?? attempt.MerchantReference, cancellationToken);
        var now = clock.GetUtcNow();
        var before = payment.Status;
        ReconciliationResult result;

        switch (status.Status)
        {
            case ProviderPaymentStatus.Succeeded:
                if (status.AmountMinor is { } amount && (amount != payment.AmountMinor || !string.Equals(status.Currency, payment.Currency, StringComparison.Ordinal)))
                {
                    logger.LogError("Provider amount mismatch for payment {PaymentId}: expected {Expected} {Currency}", payment.Id, payment.AmountMinor, payment.Currency);
                    return ReconciliationResult.NothingToDo; // needs manual review; never auto-accept a mismatching amount
                }

                payment.RecordSucceeded(attempt.Id, status.ProviderReference ?? attempt.MerchantReference, now);
                await orders.MarkPaidAsync(payment.OrderId, cancellationToken);
                result = ReconciliationResult.Succeeded;
                break;

            case ProviderPaymentStatus.Declined:
                payment.RecordDeclined(attempt.Id, status.ProviderReference, status.FailureCode ?? "declined", now);
                await orders.MarkPaymentFailedAsync(payment.OrderId, cancellationToken);
                result = ReconciliationResult.Declined;
                break;

            case ProviderPaymentStatus.Pending:
                if (attempt.Status == PaymentAttemptStatus.Processing && status.ProviderReference is not null)
                {
                    payment.RecordProviderPending(attempt.Id, status.ProviderReference, now);
                }

                result = ReconciliationResult.StillPending;
                break;

            case ProviderPaymentStatus.NotFound when attempt.Status is PaymentAttemptStatus.Unknown or PaymentAttemptStatus.Processing:
                // The provider never received the request: no money moved, so a new attempt is safe.
                var delay = RetryBackoff.Compute(1, options.Value.RetryBaseDelay, options.Value.RetryMaxDelay);
                var next = payment.RecordTransientFailure(attempt.Id, "provider_request_lost", now.Add(delay), now);
                if (next is null)
                {
                    await orders.MarkPaymentFailedAsync(payment.OrderId, cancellationToken);
                    result = ReconciliationResult.Failed;
                }
                else
                {
                    result = ReconciliationResult.RetryScheduled;
                }

                break;

            default:
                logger.LogWarning("Provider has no record of pending attempt {AttemptId}; leaving it for manual review", attempt.Id);
                return ReconciliationResult.NothingToDo;
        }

        if (payment.Status != before)
        {
            audit.Record(
                AuditActions.PaymentStatusChanged,
                "payment",
                payment.Id.ToString(),
                new { from = before.ToString(), to = payment.Status.ToString(), source = "reconciliation" });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Reconciled attempt {AttemptId} of payment {PaymentId}: {Result}", attempt.Id, payment.Id, result);
        return result;
    }
}
