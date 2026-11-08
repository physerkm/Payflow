using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Application.Orders;
using PayFlow.Domain.Payments;

namespace PayFlow.Application.Payments.Processing;

public enum ProcessingResult
{
    Applied,
    Skipped,
    Duplicate,
}

/// <summary>
/// Executes one queued payment attempt (consumer of <c>PaymentRequested.v1</c>).
/// <list type="number">
/// <item>Mark the attempt as Processing and commit (so it can no longer be cancelled).</item>
/// <item>Call the provider outside any database transaction, with the attempt id as idempotency key.</item>
/// <item>Apply the outcome, update the order and register the message in the inbox - in one transaction.</item>
/// </list>
/// A redelivered message re-sends the same idempotency key, so the provider never charges twice.
/// </summary>
public sealed class ProcessPaymentAttemptHandler(
    IPaymentsDb db,
    IUnitOfWork unitOfWork,
    IInbox inbox,
    IPaymentProvider provider,
    IOrderPaymentCoordinator orders,
    IAuditTrail audit,
    IOptions<PaymentProcessingOptions> options,
    TimeProvider clock,
    ILogger<ProcessPaymentAttemptHandler> logger)
{
    public async Task<ProcessingResult> HandleAsync(MessageContext message, Guid paymentId, Guid attemptId, CancellationToken cancellationToken)
    {
        if (await inbox.HasProcessedAsync(message, cancellationToken))
        {
            return ProcessingResult.Duplicate;
        }

        // Phase 1: claim the attempt.
        var payment = await LoadAsync(paymentId, cancellationToken);
        if (!payment.BeginAttempt(attemptId, clock.GetUtcNow()))
        {
            logger.LogInformation("Attempt {AttemptId} of payment {PaymentId} needs no processing (payment {Status})", attemptId, paymentId, payment.Status);
            await RegisterOnlyAsync(message, cancellationToken);
            return ProcessingResult.Skipped;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var attempt = payment.GetAttempt(attemptId);

        // Phase 2: provider call (no open transaction, no locks held).
        var result = await provider.ChargeAsync(
            new PaymentRequest(attempt.MerchantReference, payment.Id, payment.AmountMinor, payment.Currency, payment.PaymentMethodToken, attempt.AttemptNumber),
            cancellationToken);

        logger.LogInformation(
            "Provider returned {Outcome} for attempt {AttemptNumber} of payment {PaymentId}",
            result.Outcome,
            attempt.AttemptNumber,
            payment.Id);

        // Phase 3: persist the outcome atomically with the inbox record.
        return await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                if (!await inbox.TryRegisterAsync(message, ct))
                {
                    return ProcessingResult.Duplicate;
                }

                unitOfWork.ResetTracking();
                var fresh = await LoadAsync(paymentId, ct);
                var freshAttempt = fresh.GetAttempt(attemptId);
                if (freshAttempt.Status != PaymentAttemptStatus.Processing)
                {
                    // A webhook or reconciliation already resolved this attempt.
                    await unitOfWork.SaveChangesAsync(ct);
                    return ProcessingResult.Skipped;
                }

                await ApplyChargeResultAsync(fresh, freshAttempt, result, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return ProcessingResult.Applied;
            },
            cancellationToken);
    }

    private async Task ApplyChargeResultAsync(Payment payment, PaymentAttempt attempt, PaymentResult result, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var before = payment.Status;
        switch (result.Outcome)
        {
            case ChargeOutcome.Succeeded:
                payment.RecordSucceeded(attempt.Id, result.ProviderReference ?? attempt.MerchantReference, now);
                await orders.MarkPaidAsync(payment.OrderId, cancellationToken);
                break;

            case ChargeOutcome.Declined:
                payment.RecordDeclined(attempt.Id, result.ProviderReference, result.FailureCode ?? "declined", now);
                await orders.MarkPaymentFailedAsync(payment.OrderId, cancellationToken);
                break;

            case ChargeOutcome.Pending:
                payment.RecordProviderPending(attempt.Id, result.ProviderReference ?? attempt.MerchantReference, now);
                break;

            case ChargeOutcome.Timeout:
                // Never assume failure on timeout: a blind retry could charge twice. Reconciliation decides.
                payment.RecordUnknown(attempt.Id, result.ProviderReference, now);
                break;

            case ChargeOutcome.TransientError:
            case ChargeOutcome.CircuitOpen:
                var attemptInRun = CountAttemptsInRun(payment);
                var delay = RetryBackoff.Compute(attemptInRun, options.Value.RetryBaseDelay, options.Value.RetryMaxDelay);
                var failureCode = result.Outcome == ChargeOutcome.CircuitOpen ? "provider_circuit_open" : result.FailureCode ?? "provider_error";
                var next = payment.RecordTransientFailure(attempt.Id, failureCode, now.Add(delay), now);
                if (next is null)
                {
                    await orders.MarkPaymentFailedAsync(payment.OrderId, cancellationToken);
                }
                else
                {
                    logger.LogWarning(
                        "Transient provider error for payment {PaymentId}; attempt {AttemptNumber} scheduled in {Delay}",
                        payment.Id,
                        next.AttemptNumber,
                        delay);
                }

                break;

            default:
                throw new InvalidOperationException($"Unsupported charge outcome {result.Outcome}.");
        }

        if (payment.Status != before)
        {
            audit.Record(
                AuditActions.PaymentStatusChanged,
                "payment",
                payment.Id.ToString(),
                new { from = before.ToString(), to = payment.Status.ToString(), attempt = attempt.AttemptNumber, payment.FailureCode });
        }
    }

    private static int CountAttemptsInRun(Payment payment)
    {
        var count = 0;
        foreach (var a in payment.Attempts.OrderByDescending(a => a.AttemptNumber))
        {
            count++;
            if (a.Trigger != AttemptTrigger.AutomaticRetry)
            {
                break;
            }
        }

        return count;
    }

    private async Task RegisterOnlyAsync(MessageContext message, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                await inbox.TryRegisterAsync(message, ct);
                await unitOfWork.SaveChangesAsync(ct);
            },
            cancellationToken);

    private async Task<Payment> LoadAsync(Guid paymentId, CancellationToken cancellationToken) =>
        // The tenant query filter is active: a message whose tenantId does not own the payment finds nothing.
        await db.Payments.SingleOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
        ?? throw new NotFoundException("Payment");
}
