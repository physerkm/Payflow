using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Application.Orders;
using PayFlow.Domain.Common;

namespace PayFlow.Application.Payments.Processing;

/// <summary>A provider webhook whose signature and timestamp were already verified by the API.</summary>
public sealed record ProviderWebhookEvent(
    string Provider,
    string EventId,
    string Type,
    string ProviderReference,
    long AmountMinor,
    string Currency,
    string? FailureCode,
    string RawPayload);

public enum WebhookResult
{
    Processed,
    Duplicate,
    Unmatched,
    Rejected,
    Ignored,
}

/// <summary>
/// Applies a verified provider webhook. The payload is not trusted blindly: the payment is located by the
/// provider reference, amount and currency must match, and the state machine rejects backward transitions.
/// </summary>
public sealed class ProviderWebhookHandler(
    IPaymentsDb db,
    IUnitOfWork unitOfWork,
    IWebhookEventStore webhookEvents,
    ISystemPaymentQueries systemQueries,
    TenantContext tenantContext,
    IOrderPaymentCoordinator orders,
    IAuditTrail audit,
    TimeProvider clock,
    ILogger<ProviderWebhookHandler> logger)
{
    public const string ChargeSucceeded = "charge.succeeded";
    public const string ChargeFailed = "charge.failed";

    public async Task<WebhookResult> HandleAsync(ProviderWebhookEvent webhook, CancellationToken cancellationToken)
    {
        var locator = await systemQueries.FindAttemptByProviderReferenceAsync(webhook.Provider, webhook.ProviderReference, cancellationToken);
        if (locator is null)
        {
            // Stored for investigation; reconciliation polling will still resolve the payment.
            var recorded = await webhookEvents.TryRecordAsync(webhook.Provider, webhook.EventId, webhook.RawPayload, cancellationToken);
            if (recorded)
            {
                await webhookEvents.MarkAsync(webhook.Provider, webhook.EventId, "Unmatched", "unknown_provider_reference", cancellationToken);
            }

            return recorded ? WebhookResult.Unmatched : WebhookResult.Duplicate;
        }

        // From here on, all reads are scoped to the tenant that owns the payment.
        tenantContext.SetSystem(locator.TenantId);

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    if (!await webhookEvents.TryRecordAsync(webhook.Provider, webhook.EventId, webhook.RawPayload, ct))
                    {
                        return WebhookResult.Duplicate;
                    }

                    var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == locator.PaymentId, ct)
                        ?? throw new NotFoundException("Payment");

                    if (payment.AmountMinor != webhook.AmountMinor || !string.Equals(payment.Currency, webhook.Currency, StringComparison.OrdinalIgnoreCase))
                    {
                        await webhookEvents.MarkAsync(webhook.Provider, webhook.EventId, "Rejected", "amount_mismatch", ct);
                        logger.LogWarning("Webhook {EventId} rejected: amount/currency mismatch for payment {PaymentId}", webhook.EventId, payment.Id);
                        return WebhookResult.Rejected;
                    }

                    var before = payment.Status;
                    var now = clock.GetUtcNow();
                    switch (webhook.Type)
                    {
                        case ChargeSucceeded:
                            payment.RecordSucceeded(locator.AttemptId, webhook.ProviderReference, now);
                            await orders.MarkPaidAsync(payment.OrderId, ct);
                            break;
                        case ChargeFailed:
                            payment.RecordDeclined(locator.AttemptId, webhook.ProviderReference, webhook.FailureCode ?? "declined", now);
                            await orders.MarkPaymentFailedAsync(payment.OrderId, ct);
                            break;
                        default:
                            await webhookEvents.MarkAsync(webhook.Provider, webhook.EventId, "Ignored", null, ct);
                            return WebhookResult.Ignored;
                    }

                    if (payment.Status != before)
                    {
                        audit.Record(
                            AuditActions.PaymentStatusChanged,
                            "payment",
                            payment.Id.ToString(),
                            new { from = before.ToString(), to = payment.Status.ToString(), source = "webhook", eventId = webhook.EventId });
                    }

                    await webhookEvents.MarkAsync(webhook.Provider, webhook.EventId, "Processed", null, ct);
                    await unitOfWork.SaveChangesAsync(ct);
                    return WebhookResult.Processed;
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            // Out-of-order or contradictory event (e.g. "failed" after "succeeded"): keep the record, change nothing.
            unitOfWork.ResetTracking();
            logger.LogWarning("Webhook {EventId} rejected by the payment state machine: {Code}", webhook.EventId, ex.Code);
            if (await webhookEvents.TryRecordAsync(webhook.Provider, webhook.EventId, webhook.RawPayload, cancellationToken))
            {
                await webhookEvents.MarkAsync(webhook.Provider, webhook.EventId, "Rejected", ex.Code, cancellationToken);
            }

            return WebhookResult.Rejected;
        }
    }
}
