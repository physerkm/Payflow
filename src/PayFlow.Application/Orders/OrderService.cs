using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Application.Customers;
using PayFlow.Application.Products;
using PayFlow.Domain.Orders;

namespace PayFlow.Application.Orders;

public sealed record CreateOrderItemRequest(Guid ProductId, int Quantity);

/// <summary>Clients send only product ids and quantities. Prices and totals are always computed on the server.</summary>
public sealed record CreateOrderRequest(Guid CustomerId, IReadOnlyList<CreateOrderItemRequest> Items, string? Notes);

public sealed record OrderItemDto(Guid Id, Guid ProductId, string Sku, string Name, int Quantity, long UnitPriceMinor, long LineTotalMinor, string Currency);

public sealed record OrderDto(
    Guid Id,
    string OrderNumber,
    Guid CustomerId,
    string? CustomerName,
    OrderStatus Status,
    long TotalMinor,
    string Currency,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? CancelledAt,
    IReadOnlyList<OrderItemDto> Items);

public sealed record OrderSummaryDto(
    Guid Id,
    string OrderNumber,
    Guid CustomerId,
    string? CustomerName,
    OrderStatus Status,
    long TotalMinor,
    string Currency,
    int ItemCount,
    DateTimeOffset CreatedAt);

public sealed record OrderListQuery(
    OrderStatus? Status,
    Guid? CustomerId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Search,
    string? Sort,
    int Page = 1,
    int PageSize = 20);

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Items).NotEmpty().WithMessage("At least one item is required.");
        RuleFor(x => x.Items.Count).LessThanOrEqualTo(Order.MaxLines).When(x => x.Items is not null);
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty();
            item.RuleFor(i => i.Quantity).InclusiveBetween(1, Order.MaxQuantity);
        });
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public sealed class OrderService(
    IOrdersDb db,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    ICustomerDirectory customers,
    IProductCatalog catalog,
    IOrderPaymentsPort payments,
    IAuditTrail audit,
    TimeProvider clock)
{
    private static readonly string[] SortFields = ["createdAt", "total", "orderNumber", "status"];

    public async Task<OrderDto> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();

        // A customer id from another tenant is invisible here (tenant query filter) and is reported as not found.
        var customer = await customers.FindAsync(request.CustomerId, cancellationToken)
            ?? throw new RequestValidationException("customerId", "Customer not found.");
        if (!customer.IsActive)
        {
            throw new RequestValidationException("customerId", "Customer is inactive.");
        }

        var quantities = request.Items
            .GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

        var products = await catalog.GetAsync(quantities.Keys.ToList(), cancellationToken);
        var lines = new List<OrderLine>(quantities.Count);
        foreach (var (productId, quantity) in quantities)
        {
            var product = products.SingleOrDefault(p => p.Id == productId)
                ?? throw new RequestValidationException("items", $"Product {productId} was not found.");
            if (!product.IsActive)
            {
                throw new RequestValidationException("items", $"Product '{product.Sku}' is inactive.");
            }

            // Snapshot: later price or name changes never alter this order.
            lines.Add(new OrderLine(product.Id, product.Sku, product.Name, product.PriceMinor, product.Currency, quantity));
        }

        var sequence = await db.NextOrderSequenceAsync(tenantId, cancellationToken);
        var order = Order.Create(tenantId, $"SO-{sequence:D6}", customer.Id, lines, request.Notes, clock.GetUtcNow());
        db.Orders.Add(order);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(order, customer.Name);
    }

    public async Task<OrderDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new NotFoundException("Order");
        var names = await customers.GetNamesAsync([order.CustomerId], cancellationToken);
        return ToDto(order, names.GetValueOrDefault(order.CustomerId));
    }

    public async Task<PagedResult<OrderSummaryDto>> ListAsync(OrderListQuery query, CancellationToken cancellationToken)
    {
        var orders = db.Orders.AsNoTracking();
        if (query.Status is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (query.CustomerId is { } customerId)
        {
            orders = orders.Where(o => o.CustomerId == customerId);
        }

        if (query.From is { } from)
        {
            orders = orders.Where(o => o.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            orders = orders.Where(o => o.CreatedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            orders = orders.Where(o => o.OrderNumber.Contains(term));
        }

        var sort = SortSpec.Parse(query.Sort, SortFields, new SortSpec("createdAt", true));
        orders = sort.Field switch
        {
            "total" => orders.OrderBy(o => o.TotalMinor, sort.Descending),
            "orderNumber" => orders.OrderBy(o => o.OrderNumber, sort.Descending),
            "status" => orders.OrderBy(o => o.Status, sort.Descending),
            _ => orders.OrderBy(o => o.CreatedAt, sort.Descending),
        };

        var page = await orders.ToPagedResultAsync(
            new PageRequest(query.Page, query.PageSize),
            q => q.Select(o => new { o.Id, o.OrderNumber, o.CustomerId, o.Status, o.TotalMinor, o.Currency, ItemCount = o.Items.Count, o.CreatedAt }),
            cancellationToken);

        var names = await customers.GetNamesAsync(page.Items.Select(o => o.CustomerId).Distinct().ToList(), cancellationToken);
        var items = page.Items
            .Select(o => new OrderSummaryDto(o.Id, o.OrderNumber, o.CustomerId, names.GetValueOrDefault(o.CustomerId), o.Status, o.TotalMinor, o.Currency, o.ItemCount, o.CreatedAt))
            .ToList();
        return new PagedResult<OrderSummaryDto>(items, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<OrderDto> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new NotFoundException("Order");

        if (order.Status == OrderStatus.PendingPayment)
        {
            // Throws 409 if the payment was already sent to the provider; both changes commit together.
            await payments.CancelActivePaymentAsync(order.Id, cancellationToken);
        }

        order.Cancel(clock.GetUtcNow());
        audit.Record(AuditActions.OrderCancelled, "order", order.Id.ToString(), new { order.OrderNumber });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var names = await customers.GetNamesAsync([order.CustomerId], cancellationToken);
        return ToDto(order, names.GetValueOrDefault(order.CustomerId));
    }

    private static OrderDto ToDto(Order o, string? customerName) => new(
        o.Id,
        o.OrderNumber,
        o.CustomerId,
        customerName,
        o.Status,
        o.TotalMinor,
        o.Currency,
        o.Notes,
        o.CreatedAt,
        o.UpdatedAt,
        o.PaidAt,
        o.CancelledAt,
        o.Items.Select(i => new OrderItemDto(i.Id, i.ProductId, i.SkuSnapshot, i.NameSnapshot, i.Quantity, i.UnitPriceMinor, i.LineTotalMinor, i.Currency)).ToList());
}
