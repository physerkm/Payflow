using PayFlow.Api.Authorization;
using PayFlow.Api.Extensions;
using PayFlow.Api.Idempotency;
using PayFlow.Application.Authorization;
using PayFlow.Application.Orders;
using PayFlow.Application.Payments;

namespace PayFlow.Api.Endpoints;

internal static class OrderPaymentEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/orders").WithTags("Orders");

        group.MapPost("/", async (CreateOrderRequest request, OrderService orders, CancellationToken ct) =>
            {
                var order = await orders.CreateAsync(request, ct);
                return TypedResults.Created($"/api/v1/orders/{order.Id}", order);
            })
            .Validate<CreateOrderRequest>()
            .WithIdempotency()
            .RequirePermission(Permissions.OrdersWrite)
            .WithSummary("Create an order; prices and total are computed on the server");

        group.MapGet("/", async ([AsParameters] OrderListQuery query, OrderService orders, CancellationToken ct) =>
                TypedResults.Ok(await orders.ListAsync(Normalize(query), ct)))
            .RequirePermission(Permissions.OrdersRead)
            .WithSummary("Filter and page orders (sort: createdAt, total, orderNumber, status)");

        group.MapGet("/{id:guid}", async (Guid id, OrderService orders, CancellationToken ct) => TypedResults.Ok(await orders.GetAsync(id, ct)))
            .RequirePermission(Permissions.OrdersRead)
            .WithSummary("Order details with item snapshots");

        group.MapPatch("/{id:guid}/cancel", async (Guid id, OrderService orders, CancellationToken ct) => TypedResults.Ok(await orders.CancelAsync(id, ct)))
            .RequirePermission(Permissions.OrdersWrite)
            .WithSummary("Cancel an order (not allowed once paid or while the provider is processing)");

        group.MapGet("/{id:guid}/payments", async (Guid id, PaymentService payments, CancellationToken ct) =>
                TypedResults.Ok(await payments.ListForOrderAsync(id, ct)))
            .RequirePermission(Permissions.PaymentsRead)
            .WithSummary("Payments of an order");

        group.MapPost("/{id:guid}/payments", async (Guid id, InitiatePaymentRequest request, PaymentService payments, CancellationToken ct) =>
            {
                var accepted = await payments.InitiateAsync(id, request, ct);
                return TypedResults.Accepted(accepted.StatusUrl, accepted);
            })
            .Validate<InitiatePaymentRequest>()
            .WithIdempotency(required: true)
            .RequirePermission(Permissions.PaymentsWrite)
            .WithSummary("Start an asynchronous payment: 202 Accepted + paymentId (requires Idempotency-Key)");

        return api;
    }

    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/payments").WithTags("Payments & refunds");

        group.MapGet("/", async ([AsParameters] PaymentListQuery query, PaymentService payments, CancellationToken ct) =>
                TypedResults.Ok(await payments.ListAsync(Normalize(query), ct)))
            .RequirePermission(Permissions.PaymentsRead)
            .WithSummary("Payment list with status filters");

        group.MapGet("/{id:guid}", async (Guid id, PaymentService payments, CancellationToken ct) => TypedResults.Ok(await payments.GetAsync(id, ct)))
            .RequirePermission(Permissions.PaymentsRead)
            .WithSummary("Payment status and details (poll this after 202)");

        group.MapGet("/{id:guid}/attempts", async (Guid id, PaymentService payments, CancellationToken ct) =>
                TypedResults.Ok(await payments.GetAttemptsAsync(id, ct)))
            .RequirePermission(Permissions.PaymentsRead)
            .WithSummary("Provider attempt history");

        group.MapPost("/{id:guid}/retry", async (Guid id, RetryPaymentRequest? request, PaymentService payments, CancellationToken ct) =>
            {
                var accepted = await payments.RetryAsync(id, request ?? new RetryPaymentRequest(null), ct);
                return TypedResults.Accepted(accepted.StatusUrl, accepted);
            })
            .WithIdempotency(required: true)
            .RequirePermission(Permissions.PaymentsWrite)
            .WithSummary("Retry a failed payment with a new attempt (requires Idempotency-Key)");

        group.MapPost("/{id:guid}/refunds", async (Guid id, CreateRefundRequest request, RefundService refunds, CancellationToken ct) =>
            {
                var refund = await refunds.CreateAsync(id, request, ct);
                return TypedResults.Accepted($"/api/v1/payments/{id}/refunds", refund);
            })
            .Validate<CreateRefundRequest>()
            .WithIdempotency(required: true)
            .RequirePermission(Permissions.RefundsCreate)
            .WithSummary("Request a full or partial refund (requires Idempotency-Key)");

        group.MapGet("/{id:guid}/refunds", async (Guid id, RefundService refunds, CancellationToken ct) =>
                TypedResults.Ok(await refunds.ListForPaymentAsync(id, ct)))
            .RequirePermission(Permissions.PaymentsRead)
            .WithSummary("Refunds of a payment");

        api.MapGet("/refunds", async ([AsParameters] RefundListQuery query, RefundService refunds, CancellationToken ct) =>
                TypedResults.Ok(await refunds.ListAsync(query with { From = query.From?.ToUniversalTime(), To = query.To?.ToUniversalTime() }, ct)))
            .WithTags("Payments & refunds")
            .RequirePermission(Permissions.PaymentsRead)
            .WithSummary("All refunds of the tenant");

        return api;
    }

    // PostgreSQL timestamptz parameters must be UTC.
    private static OrderListQuery Normalize(OrderListQuery q) => q with { From = q.From?.ToUniversalTime(), To = q.To?.ToUniversalTime() };

    private static PaymentListQuery Normalize(PaymentListQuery q) => q with { From = q.From?.ToUniversalTime(), To = q.To?.ToUniversalTime() };
}
