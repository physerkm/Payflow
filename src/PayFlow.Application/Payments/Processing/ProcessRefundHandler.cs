using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Refunds;

namespace PayFlow.Application.Payments.Processing;

/// <summary>Raised for transient provider errors so the transport retries the message with backoff (then DLQ).</summary>
public sealed class TransientProviderException : Exception
{
    public TransientProviderException(string message)
        : base(message)
    {
    }

    public TransientProviderException()
        : base("The payment provider returned a transient error.")
    {
    }

    public TransientProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Executes a requested refund through the provider (consumer of <c>RefundRequested.v1</c>).</summary>
public sealed class ProcessRefundHandler(
    IPaymentsDb db,
    IUnitOfWork unitOfWork,
    IInbox inbox,
    IPaymentProvider provider,
    IAuditTrail audit,
    TimeProvider clock,
    ILogger<ProcessRefundHandler> logger)
{
    public async Task<ProcessingResult> HandleAsync(MessageContext message, Guid refundId, Guid paymentId, CancellationToken cancellationToken)
    {
        if (await inbox.HasProcessedAsync(message, cancellationToken))
        {
            return ProcessingResult.Duplicate;
        }

        var refund = await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.Id == refundId && r.PaymentId == paymentId, cancellationToken)
            ?? throw new NotFoundException("Refund");
        if (refund.Status != RefundStatus.Pending)
        {
            return ProcessingResult.Skipped;
        }

        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw new NotFoundException("Payment");
        var capturedAttempt = payment.Attempts.SingleOrDefault(a => a.Status == PaymentAttemptStatus.Succeeded)
            ?? throw new InvalidOperationException($"Payment {paymentId} has no succeeded attempt to refund.");

        // Refund id = provider idempotency key: a redelivered message never refunds twice.
        var result = await provider.RefundAsync(
            new RefundRequest(refund.Id.ToString("N"), capturedAttempt.ProviderReference ?? capturedAttempt.MerchantReference, refund.AmountMinor, refund.Currency),
            cancellationToken);

        if (result.Outcome == RefundOutcome.TransientError)
        {
            throw new TransientProviderException($"Provider transient error while refunding {refundId}: {result.FailureCode}");
        }

        return await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                if (!await inbox.TryRegisterAsync(message, ct))
                {
                    return ProcessingResult.Duplicate;
                }

                await db.LockPaymentAsync(paymentId, ct);
                unitOfWork.ResetTracking();
                var lockedPayment = await db.Payments.SingleAsync(p => p.Id == paymentId, ct);
                var trackedRefund = await db.Refunds.SingleAsync(r => r.Id == refundId, ct);
                if (trackedRefund.Status != RefundStatus.Pending)
                {
                    await unitOfWork.SaveChangesAsync(ct);
                    return ProcessingResult.Skipped;
                }

                var now = clock.GetUtcNow();
                if (result.Outcome == RefundOutcome.Succeeded)
                {
                    trackedRefund.MarkSucceeded(lockedPayment, result.ProviderReference ?? trackedRefund.Id.ToString("N"), now);
                }
                else
                {
                    trackedRefund.MarkFailed(lockedPayment, result.FailureCode ?? "refund_declined", now);
                }

                audit.Record(
                    AuditActions.RefundCompleted,
                    "refund",
                    trackedRefund.Id.ToString(),
                    new { status = trackedRefund.Status.ToString(), paymentStatus = lockedPayment.Status.ToString(), trackedRefund.FailureCode });
                await unitOfWork.SaveChangesAsync(ct);
                logger.LogInformation("Refund {RefundId} completed with status {Status}", refundId, trackedRefund.Status);
                return ProcessingResult.Applied;
            },
            cancellationToken);
    }
}
