using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Application.Orders;
using PayFlow.Domain.Refunds;

namespace PayFlow.Application.Payments;

public sealed record CreateRefundRequest(long AmountMinor, string? Reason);

public sealed record RefundDto(
    Guid Id,
    Guid PaymentId,
    Guid? OrderId,
    string? OrderNumber,
    long AmountMinor,
    string Currency,
    RefundStatus Status,
    string? Reason,
    string? ProviderReference,
    string? FailureCode,
    Guid RequestedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record RefundListQuery(RefundStatus? Status, Guid? PaymentId, DateTimeOffset? From, DateTimeOffset? To, int Page = 1, int PageSize = 20);

public sealed class CreateRefundRequestValidator : AbstractValidator<CreateRefundRequest>
{
    public CreateRefundRequestValidator()
    {
        RuleFor(x => x.AmountMinor).GreaterThan(0).WithMessage("Refund amount must be positive.");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class RefundService(
    IPaymentsDb db,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IOrderLookup orderLookup,
    IAuditTrail audit,
    TimeProvider clock)
{
    public async Task<RefundDto> CreateAsync(Guid paymentId, CreateRefundRequest request, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var refund = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                // Row lock: concurrent refund requests for the same payment are serialized, so the sum of
                // reserved + completed refunds can never exceed the captured amount.
                await db.LockPaymentAsync(paymentId, ct);
                var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == paymentId, ct)
                    ?? throw new NotFoundException("Payment");

                var created = Refund.Request(payment, request.AmountMinor, request.Reason, userId, clock.GetUtcNow());
                db.Refunds.Add(created);
                audit.Record(AuditActions.RefundRequested, "refund", created.Id.ToString(), new { paymentId, created.AmountMinor, created.Currency });
                await unitOfWork.SaveChangesAsync(ct);
                return (Refund: created, payment.OrderId);
            },
            cancellationToken);

        var numbers = await orderLookup.GetOrderNumbersAsync([refund.OrderId], cancellationToken);
        return ToDto(refund.Refund, refund.OrderId, numbers.GetValueOrDefault(refund.OrderId));
    }

    public async Task<IReadOnlyList<RefundDto>> ListForPaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => new { p.Id, p.OrderId }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Payment");
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.PaymentId == paymentId).OrderByDescending(r => r.CreatedAt).ToListAsync(cancellationToken);
        var numbers = await orderLookup.GetOrderNumbersAsync([payment.OrderId], cancellationToken);
        return refunds.Select(r => ToDto(r, payment.OrderId, numbers.GetValueOrDefault(payment.OrderId))).ToList();
    }

    public async Task<PagedResult<RefundDto>> ListAsync(RefundListQuery query, CancellationToken cancellationToken)
    {
        var refunds = db.Refunds.AsNoTracking();
        if (query.Status is { } status)
        {
            refunds = refunds.Where(r => r.Status == status);
        }

        if (query.PaymentId is { } paymentId)
        {
            refunds = refunds.Where(r => r.PaymentId == paymentId);
        }

        if (query.From is { } from)
        {
            refunds = refunds.Where(r => r.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            refunds = refunds.Where(r => r.CreatedAt < to);
        }

        var page = await refunds.OrderByDescending(r => r.CreatedAt)
            .ToPagedResultAsync(new PageRequest(query.Page, query.PageSize), q => q, cancellationToken);

        var paymentIds = page.Items.Select(r => r.PaymentId).Distinct().ToList();
        var orderIds = await db.Payments.AsNoTracking()
            .Where(p => paymentIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.OrderId, cancellationToken);
        var numbers = await orderLookup.GetOrderNumbersAsync(orderIds.Values.Distinct().ToList(), cancellationToken);

        return new PagedResult<RefundDto>(
            page.Items.Select(r =>
            {
                var orderId = orderIds.TryGetValue(r.PaymentId, out var oid) ? oid : (Guid?)null;
                return ToDto(r, orderId, orderId is { } id ? numbers.GetValueOrDefault(id) : null);
            }).ToList(),
            page.Page,
            page.PageSize,
            page.TotalCount);
    }

    private static RefundDto ToDto(Refund r, Guid? orderId, string? orderNumber) => new(
        r.Id, r.PaymentId, orderId, orderNumber, r.AmountMinor, r.Currency, r.Status, r.Reason, r.ProviderReference, r.FailureCode,
        r.RequestedByUserId, r.CreatedAt, r.CompletedAt);
}
