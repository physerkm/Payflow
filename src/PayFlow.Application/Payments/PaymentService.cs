using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Application.Orders;
using PayFlow.Domain.Common;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Refunds;

namespace PayFlow.Application.Payments;

/// <summary>The amount is never sent by the client; the token only selects a (test) payment method.</summary>
public sealed record InitiatePaymentRequest(string PaymentMethodToken);

public sealed record RetryPaymentRequest(string? PaymentMethodToken);

public sealed record PaymentAcceptedResponse(Guid PaymentId, Guid OrderId, PaymentStatus Status, long AmountMinor, string Currency, string StatusUrl);

public sealed record PaymentDto(
    Guid Id,
    Guid OrderId,
    string? OrderNumber,
    PaymentStatus Status,
    long AmountMinor,
    string Currency,
    string Provider,
    string? FailureCode,
    long RefundedAmountMinor,
    long PendingRefundAmountMinor,
    long RefundableAmountMinor,
    int AttemptCount,
    bool IsFinal,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SucceededAt,
    DateTimeOffset? FailedAt);

public sealed record PaymentAttemptDto(
    Guid Id,
    int AttemptNumber,
    AttemptTrigger Trigger,
    PaymentAttemptStatus Status,
    string Provider,
    string? ProviderReference,
    string? FailureCode,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record PaymentListQuery(
    PaymentStatus? Status,
    Guid? OrderId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Sort,
    int Page = 1,
    int PageSize = 20);

public static class PaymentMethodTokens
{
    public const string Pattern = "^pm_[a-z0-9_]{3,64}$";
}

public sealed class InitiatePaymentRequestValidator : AbstractValidator<InitiatePaymentRequest>
{
    public InitiatePaymentRequestValidator() =>
        RuleFor(x => x.PaymentMethodToken).NotEmpty().Matches(PaymentMethodTokens.Pattern)
            .WithMessage("Payment method token must look like 'pm_test_success'. Card data is never accepted.");
}

public sealed class RetryPaymentRequestValidator : AbstractValidator<RetryPaymentRequest>
{
    public RetryPaymentRequestValidator() =>
        RuleFor(x => x.PaymentMethodToken).Matches(PaymentMethodTokens.Pattern).When(x => x.PaymentMethodToken is not null);
}

public sealed class PaymentService(
    IPaymentsDb db,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    IOrderPaymentCoordinator orders,
    IOrderLookup orderLookup,
    IPaymentProvider provider,
    IAuditTrail audit,
    TimeProvider clock)
{
    public const string ActivePaymentPerOrderConstraint = "ux_payments_order_active";
    private static readonly string[] SortFields = ["createdAt", "amount", "status"];

    public async Task<PaymentAcceptedResponse> InitiateAsync(Guid orderId, InitiatePaymentRequest request, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var order = await orders.GetPayableOrderAsync(orderId, cancellationToken);

        // Payment + first attempt + PaymentRequested outbox message are saved in ONE transaction.
        var payment = Payment.Initiate(
            tenantId,
            order.OrderId,
            new Money(order.AmountMinor, order.Currency),
            request.PaymentMethodToken,
            provider.Name,
            clock.GetUtcNow());
        db.Payments.Add(payment);
        await orders.MarkPaymentPendingAsync(order.OrderId, cancellationToken);
        audit.Record(
            AuditActions.PaymentInitiated,
            "payment",
            payment.Id.ToString(),
            new { orderId = order.OrderId, order.OrderNumber, payment.AmountMinor, payment.Currency });

        await SaveGuardingActivePaymentAsync(cancellationToken);
        return new PaymentAcceptedResponse(payment.Id, order.OrderId, payment.Status, payment.AmountMinor, payment.Currency, StatusUrl(payment.Id));
    }

    public async Task<PaymentAcceptedResponse> RetryAsync(Guid paymentId, RetryPaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = await LoadAsync(paymentId, cancellationToken);
        payment.Retry(request.PaymentMethodToken, clock.GetUtcNow());
        await orders.MarkPaymentPendingAsync(payment.OrderId, cancellationToken);
        audit.Record(AuditActions.PaymentRetried, "payment", payment.Id.ToString(), new { attempt = payment.CurrentAttempt!.AttemptNumber });

        await SaveGuardingActivePaymentAsync(cancellationToken);
        return new PaymentAcceptedResponse(payment.Id, payment.OrderId, payment.Status, payment.AmountMinor, payment.Currency, StatusUrl(payment.Id));
    }

    public async Task<PaymentDto> GetAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await LoadAsync(paymentId, cancellationToken, tracking: false);
        var numbers = await orderLookup.GetOrderNumbersAsync([payment.OrderId], cancellationToken);
        return ToDto(payment, numbers.GetValueOrDefault(payment.OrderId));
    }

    public async Task<IReadOnlyList<PaymentAttemptDto>> GetAttemptsAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await LoadAsync(paymentId, cancellationToken, tracking: false);
        return payment.Attempts
            .OrderBy(a => a.AttemptNumber)
            .Select(a => new PaymentAttemptDto(a.Id, a.AttemptNumber, a.Trigger, a.Status, a.Provider, a.ProviderReference, a.FailureCode, a.ScheduledAt, a.StartedAt, a.CompletedAt))
            .ToList();
    }

    public async Task<PagedResult<PaymentDto>> ListAsync(PaymentListQuery query, CancellationToken cancellationToken)
    {
        var payments = db.Payments.AsNoTracking();
        if (query.Status is { } status)
        {
            payments = payments.Where(p => p.Status == status);
        }

        if (query.OrderId is { } orderId)
        {
            payments = payments.Where(p => p.OrderId == orderId);
        }

        if (query.From is { } from)
        {
            payments = payments.Where(p => p.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            payments = payments.Where(p => p.CreatedAt < to);
        }

        var sort = SortSpec.Parse(query.Sort, SortFields, new SortSpec("createdAt", true));
        payments = sort.Field switch
        {
            "amount" => payments.OrderBy(p => p.AmountMinor, sort.Descending),
            "status" => payments.OrderBy(p => p.Status, sort.Descending),
            _ => payments.OrderBy(p => p.CreatedAt, sort.Descending),
        };

        var page = await payments.ToPagedResultAsync(new PageRequest(query.Page, query.PageSize), q => q, cancellationToken);
        var numbers = await orderLookup.GetOrderNumbersAsync(page.Items.Select(p => p.OrderId).Distinct().ToList(), cancellationToken);
        return new PagedResult<PaymentDto>(
            page.Items.Select(p => ToDto(p, numbers.GetValueOrDefault(p.OrderId))).ToList(),
            page.Page,
            page.PageSize,
            page.TotalCount);
    }

    public Task<PagedResult<PaymentDto>> ListForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        ListAsync(new PaymentListQuery(null, orderId, null, null, null, 1, PageRequest.MaxPageSize), cancellationToken);

    private async Task SaveGuardingActivePaymentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException ex) when (ex.ConstraintName == ActivePaymentPerOrderConstraint)
        {
            // Database-level guard: at most one non-failed payment per order, even under concurrent requests
            // that use different idempotency keys.
            throw new ConflictException("payment_in_progress", "A payment for this order is already in progress or completed.");
        }
    }

    private async Task<Payment> LoadAsync(Guid paymentId, CancellationToken cancellationToken, bool tracking = true)
    {
        var query = tracking ? db.Payments : db.Payments.AsNoTracking();
        return await query.SingleOrDefaultAsync(p => p.Id == paymentId, cancellationToken) ?? throw new NotFoundException("Payment");
    }

    private static string StatusUrl(Guid paymentId) => $"/api/v1/payments/{paymentId}";

    internal static PaymentDto ToDto(Payment p, string? orderNumber) => new(
        p.Id,
        p.OrderId,
        orderNumber,
        p.Status,
        p.AmountMinor,
        p.Currency,
        p.Provider,
        p.FailureCode,
        p.RefundedAmountMinor,
        p.PendingRefundAmountMinor,
        p.RefundableAmountMinor,
        p.Attempts.Count,
        p.Status is PaymentStatus.Succeeded or PaymentStatus.Failed or PaymentStatus.Cancelled or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded,
        p.CreatedAt,
        p.UpdatedAt,
        p.SucceededAt,
        p.FailedAt);
}

/// <summary>Implements the Orders module port: cancelling an order cancels its not-yet-submitted payment.</summary>
internal sealed class OrderPaymentsPort(IPaymentsDb db, TimeProvider clock) : IOrderPaymentsPort
{
    public async Task CancelActivePaymentAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var active = await db.Payments
            .Where(p => p.OrderId == orderId && (p.Status == PaymentStatus.Created || p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Unknown))
            .ToListAsync(cancellationToken);

        foreach (var payment in active)
        {
            payment.Cancel(clock.GetUtcNow());
        }
    }

    public async Task<IReadOnlyList<OrderHistoryEntry>> GetPaymentHistoryAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var payments = await db.Payments.AsNoTracking()
            .Include(p => p.Attempts)
            .Where(p => p.OrderId == orderId)
            .ToListAsync(cancellationToken);
        var paymentIds = payments.Select(p => p.Id).ToList();
        var refunds = await db.Refunds.AsNoTracking()
            .Where(r => paymentIds.Contains(r.PaymentId))
            .ToListAsync(cancellationToken);

        var entries = new List<OrderHistoryEntry>();
        foreach (var payment in payments)
        {
            entries.Add(new(payment.CreatedAt, OrderHistoryEvents.PaymentInitiated, AmountMinor: payment.AmountMinor, Currency: payment.Currency, PaymentId: payment.Id));

            // One entry per provider attempt, at its outcome time (or when it started / is scheduled to start).
            foreach (var attempt in payment.Attempts)
            {
                entries.Add(new(
                    attempt.CompletedAt ?? attempt.StartedAt ?? attempt.ScheduledAt,
                    OrderHistoryEvents.PaymentAttempt,
                    attempt.Status.ToString(),
                    Code: attempt.FailureCode,
                    PaymentId: payment.Id,
                    AttemptNumber: attempt.AttemptNumber,
                    Trigger: attempt.Trigger.ToString()));
            }

            if (payment.SucceededAt is { } succeededAt)
            {
                entries.Add(new(succeededAt, OrderHistoryEvents.PaymentSucceeded, AmountMinor: payment.AmountMinor, Currency: payment.Currency, PaymentId: payment.Id));
            }

            if (payment.FailedAt is { } failedAt)
            {
                entries.Add(new(failedAt, OrderHistoryEvents.PaymentFailed, Code: payment.FailureCode, PaymentId: payment.Id));
            }

            if (payment.Status == PaymentStatus.Cancelled)
            {
                // Cancelled is terminal, so the last update is the cancellation.
                entries.Add(new(payment.UpdatedAt, OrderHistoryEvents.PaymentCancelled, PaymentId: payment.Id));
            }
        }

        foreach (var refund in refunds)
        {
            entries.Add(new(refund.CreatedAt, OrderHistoryEvents.RefundRequested, AmountMinor: refund.AmountMinor, Currency: refund.Currency, PaymentId: refund.PaymentId, Note: refund.Reason));
            if (refund.CompletedAt is { } completedAt)
            {
                var type = refund.Status == RefundStatus.Succeeded ? OrderHistoryEvents.RefundSucceeded : OrderHistoryEvents.RefundFailed;
                entries.Add(new(completedAt, type, AmountMinor: refund.AmountMinor, Currency: refund.Currency, Code: refund.FailureCode, PaymentId: refund.PaymentId));
            }
        }

        return entries;
    }
}
