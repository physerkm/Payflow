using PayFlow.Domain.Common;

namespace PayFlow.Domain.Orders;

public enum OrderStatus
{
    Draft,
    PendingPayment,
    PaymentFailed,
    Paid,
    Cancelled,
}

/// <summary>Price/identity snapshot of a product taken when the order is created.</summary>
public sealed record OrderLine(Guid ProductId, string Sku, string Name, long UnitPriceMinor, string Currency, int Quantity);

/// <summary>
/// A customer's purchase intent. The total is always computed from item snapshots on the server;
/// clients never send prices or totals.
/// </summary>
public sealed class Order : AggregateRoot, ITenantScoped
{
    public const int MaxLines = 100;
    public const int MaxQuantity = 10_000;

    private readonly List<OrderItem> _items = [];

    private Order()
    {
    }

    public Guid TenantId { get; private set; }

    public string OrderNumber { get; private set; } = default!;

    public Guid CustomerId { get; private set; }

    public OrderStatus Status { get; private set; }

    public long TotalMinor { get; private set; }

    public string Currency { get; private set; } = default!;

    public string? Notes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;

    public Money Total => new(TotalMinor, Currency);

    public static Order Create(Guid tenantId, string orderNumber, Guid customerId, IReadOnlyList<OrderLine> lines, string? notes, DateTimeOffset now)
    {
        if (lines.Count == 0)
        {
            throw new DomainException("order_lines_required", "An order must contain at least one item.");
        }

        if (lines.Count > MaxLines)
        {
            throw new DomainException("order_too_many_lines", $"An order can contain at most {MaxLines} items.");
        }

        if (lines.Select(l => l.ProductId).Distinct().Count() != lines.Count)
        {
            throw new DomainException("order_duplicate_product", "Each product may appear only once per order; use quantity instead.");
        }

        var currency = CurrencyCatalog.Normalize(lines[0].Currency);
        if (lines.Any(l => !string.Equals(CurrencyCatalog.Normalize(l.Currency), currency, StringComparison.Ordinal)))
        {
            throw new DomainException("order_currency_mismatch", "All items of an order must use the same currency.");
        }

        var order = new Order
        {
            Id = Guid.CreateVersion7(now),
            TenantId = Guard.NotEmpty(tenantId, "tenant_required"),
            OrderNumber = Guard.NotEmpty(orderNumber, "order_number_required", "Order number is required", 50),
            CustomerId = Guard.NotEmpty(customerId, "customer_required"),
            Status = OrderStatus.Draft,
            Currency = currency,
            Notes = Guard.Optional(notes, "order_notes_invalid", 1000),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var total = Money.Zero(currency);
        foreach (var line in lines)
        {
            var item = OrderItem.Create(order, line, now);
            order._items.Add(item);
            total = total.Add(item.LineTotal);
        }

        order.TotalMinor = total.AmountMinor;
        order.Raise(new OrderCreated(order.Id, order.TenantId, order.TotalMinor, order.Currency, now));
        return order;
    }

    public bool CanAcceptPayment => Status is OrderStatus.Draft or OrderStatus.PaymentFailed;

    public void MarkPaymentPending(DateTimeOffset now)
    {
        if (Status == OrderStatus.PendingPayment)
        {
            return;
        }

        EnsureTransition(OrderStatus.PendingPayment, OrderStatus.Draft, OrderStatus.PaymentFailed);
        Status = OrderStatus.PendingPayment;
        UpdatedAt = now;
    }

    public void MarkPaid(DateTimeOffset now)
    {
        if (Status == OrderStatus.Paid)
        {
            return; // idempotent: duplicate confirmations are harmless
        }

        EnsureTransition(OrderStatus.Paid, OrderStatus.PendingPayment);
        Status = OrderStatus.Paid;
        PaidAt = now;
        UpdatedAt = now;
        Raise(new OrderPaid(Id, TenantId, now));
    }

    public void MarkPaymentFailed(DateTimeOffset now)
    {
        if (Status == OrderStatus.PaymentFailed)
        {
            return;
        }

        EnsureTransition(OrderStatus.PaymentFailed, OrderStatus.PendingPayment);
        Status = OrderStatus.PaymentFailed;
        UpdatedAt = now;
    }

    /// <summary>
    /// Cancels the order. The caller must make sure an in-flight payment (if any) has been cancelled first;
    /// a paid order can never be cancelled (use refunds instead).
    /// </summary>
    public void Cancel(DateTimeOffset now)
    {
        EnsureTransition(OrderStatus.Cancelled, OrderStatus.Draft, OrderStatus.PendingPayment, OrderStatus.PaymentFailed);
        Status = OrderStatus.Cancelled;
        CancelledAt = now;
        UpdatedAt = now;
        Raise(new OrderCancelled(Id, TenantId, now));
    }

    private void EnsureTransition(OrderStatus target, params OrderStatus[] allowedSources)
    {
        if (!allowedSources.Contains(Status))
        {
            throw DomainException.InvalidTransition("order", Status.ToString(), target.ToString());
        }
    }
}

public sealed class OrderItem : Entity, ITenantScoped
{
    private OrderItem()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid ProductId { get; private set; }

    public string SkuSnapshot { get; private set; } = default!;

    public string NameSnapshot { get; private set; } = default!;

    public int Quantity { get; private set; }

    public long UnitPriceMinor { get; private set; }

    public long LineTotalMinor { get; private set; }

    public string Currency { get; private set; } = default!;

    public Money LineTotal => new(LineTotalMinor, Currency);

    internal static OrderItem Create(Order order, OrderLine line, DateTimeOffset now)
    {
        if (line.Quantity is <= 0 or > Order.MaxQuantity)
        {
            throw new DomainException("order_quantity_invalid", $"Quantity must be between 1 and {Order.MaxQuantity}.");
        }

        if (line.UnitPriceMinor <= 0)
        {
            throw new DomainException("order_price_invalid", "Unit price must be positive.");
        }

        var unitPrice = new Money(line.UnitPriceMinor, line.Currency);
        var lineTotal = unitPrice.Multiply(line.Quantity);

        return new OrderItem
        {
            Id = Guid.CreateVersion7(now),
            TenantId = order.TenantId,
            OrderId = order.Id,
            ProductId = Guard.NotEmpty(line.ProductId, "product_required"),
            SkuSnapshot = Guard.NotEmpty(line.Sku, "order_sku_required", "SKU snapshot is required", 64),
            NameSnapshot = Guard.NotEmpty(line.Name, "order_name_required", "Name snapshot is required", 200),
            Quantity = line.Quantity,
            UnitPriceMinor = unitPrice.AmountMinor,
            LineTotalMinor = lineTotal.AmountMinor,
            Currency = unitPrice.Currency,
        };
    }
}

public sealed record OrderCreated(Guid OrderId, Guid TenantId, long TotalMinor, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderPaid(Guid OrderId, Guid TenantId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderCancelled(Guid OrderId, Guid TenantId, DateTimeOffset OccurredAt) : IDomainEvent;
