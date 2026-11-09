using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Orders;

namespace PayFlow.Application.Orders;

public sealed record PayableOrder(Guid OrderId, string OrderNumber, long AmountMinor, string Currency, OrderStatus Status);

/// <summary>
/// Public API of the Orders module used by the Payments module. Order state changes go through these
/// methods (never by touching the orders table directly) and join the caller's unit of work.
/// </summary>
public interface IOrderPaymentCoordinator
{
    /// <summary>Returns the order and the amount due (computed by the server), or throws 404/409.</summary>
    Task<PayableOrder> GetPayableOrderAsync(Guid orderId, CancellationToken cancellationToken);

    Task MarkPaymentPendingAsync(Guid orderId, CancellationToken cancellationToken);

    Task MarkPaidAsync(Guid orderId, CancellationToken cancellationToken);

    Task MarkPaymentFailedAsync(Guid orderId, CancellationToken cancellationToken);
}

public interface IOrderLookup
{
    Task<IReadOnlyDictionary<Guid, string>> GetOrderNumbersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);
}

/// <summary>
/// Port owned by the Orders module and implemented by the Payments module (dependency inversion), so that
/// Orders never depends on Payments.
/// </summary>
public interface IOrderPaymentsPort
{
    /// <summary>Cancels the order's in-flight payment if it has not been sent to the provider; otherwise throws 409.</summary>
    Task CancelActivePaymentAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Payment, attempt and refund events of the order, for its history (unsorted).</summary>
    Task<IReadOnlyList<OrderHistoryEntry>> GetPaymentHistoryAsync(Guid orderId, CancellationToken cancellationToken);
}

internal sealed class OrderPaymentCoordinator(IOrdersDb db, TimeProvider clock) : IOrderPaymentCoordinator, IOrderLookup
{
    public async Task<PayableOrder> GetPayableOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await LoadAsync(orderId, cancellationToken);
        if (!order.CanAcceptPayment)
        {
            throw order.Status switch
            {
                OrderStatus.Paid => new ConflictException("order_already_paid", "The order is already paid."),
                OrderStatus.Cancelled => new ConflictException("order_cancelled", "The order is cancelled."),
                OrderStatus.PendingPayment => new ConflictException("payment_in_progress", "A payment for this order is already in progress."),
                _ => new ConflictException("order_not_payable", $"The order cannot be paid in status {order.Status}."),
            };
        }

        return new PayableOrder(order.Id, order.OrderNumber, order.TotalMinor, order.Currency, order.Status);
    }

    public async Task MarkPaymentPendingAsync(Guid orderId, CancellationToken cancellationToken) =>
        (await LoadAsync(orderId, cancellationToken)).MarkPaymentPending(clock.GetUtcNow());

    public async Task MarkPaidAsync(Guid orderId, CancellationToken cancellationToken) =>
        (await LoadAsync(orderId, cancellationToken)).MarkPaid(clock.GetUtcNow());

    public async Task MarkPaymentFailedAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await LoadAsync(orderId, cancellationToken);
        if (order.Status == OrderStatus.PendingPayment)
        {
            order.MarkPaymentFailed(clock.GetUtcNow());
        }
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetOrderNumbersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await db.Orders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.OrderNumber, cancellationToken);
    }

    private async Task<Order> LoadAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken) ?? throw new NotFoundException("Order");
}
