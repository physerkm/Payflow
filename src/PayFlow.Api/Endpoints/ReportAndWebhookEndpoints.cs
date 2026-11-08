using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PayFlow.Api.Authorization;
using PayFlow.Application.Authorization;
using PayFlow.Application.Payments.Processing;
using PayFlow.Application.Reporting;
using PayFlow.Infrastructure.Observability;
using PayFlow.Infrastructure.PaymentProviders;

namespace PayFlow.Api.Endpoints;

internal static class ReportAndWebhookEndpoints
{
    private const int MaxWebhookBytes = 64 * 1024;

    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/reports").WithTags("Reports").RequirePermission(Permissions.ReportsRead);

        group.MapGet("/overview", async (DateTimeOffset? from, DateTimeOffset? to, ReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.OverviewAsync(from, to, ct)))
            .WithSummary("Dashboard metrics per currency (no FX conversion)");

        group.MapGet("/revenue", async (DateTimeOffset? from, DateTimeOffset? to, string? currency, ReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.RevenueAsync(from, to, currency, ct)))
            .WithSummary("Daily gross / refunded / net revenue per currency in the tenant time zone");

        group.MapGet("/payments", async (DateTimeOffset? from, DateTimeOffset? to, ReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.PaymentsAsync(from, to, ct)))
            .WithSummary("Payment status distribution, success rate and failure codes");

        group.MapGet("/refunds", async (DateTimeOffset? from, DateTimeOffset? to, ReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.RefundsAsync(from, to, ct)))
            .WithSummary("Refund statistics per currency");

        group.MapGet("/orders", async (DateTimeOffset? from, DateTimeOffset? to, ReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.OrdersAsync(from, to, ct)))
            .WithSummary("Order volume and status statistics");

        return api;
    }

    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/webhooks/payments/mock", async (
                HttpRequest request,
                ProviderWebhookHandler handler,
                IOptions<MockProviderOptions> options,
                TimeProvider clock,
                ILoggerFactory loggers,
                CancellationToken ct) =>
            {
                var logger = loggers.CreateLogger("PayFlow.Webhooks");
                if (request.ContentLength > MaxWebhookBytes)
                {
                    return Results.Problem(statusCode: 413, title: "Payload too large");
                }

                using var reader = new StreamReader(request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                if (body.Length > MaxWebhookBytes)
                {
                    return Results.Problem(statusCode: 413, title: "Payload too large");
                }

                // 1) Authenticity + replay window, before the payload is even parsed.
                if (!WebhookSignature.Verify(request.Headers[WebhookSignature.HeaderName], body, options.Value.WebhookSecret, clock.GetUtcNow()))
                {
                    logger.LogWarning("Rejected mock webhook with an invalid or expired signature");
                    PayFlowMetrics.WebhooksReceived.Add(1, new KeyValuePair<string, object?>("result", "invalid_signature"));
                    return Results.Problem(statusCode: 401, title: "Invalid signature", extensions: new Dictionary<string, object?> { ["code"] = "invalid_signature" });
                }

                MockWebhookPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<MockWebhookPayload>(body, JsonSerializerOptions.Web);
                }
                catch (JsonException)
                {
                    payload = null;
                }

                if (payload?.Data is null || string.IsNullOrWhiteSpace(payload.Id) || string.IsNullOrWhiteSpace(payload.Data.ProviderReference))
                {
                    return Results.Problem(statusCode: 400, title: "Malformed webhook payload");
                }

                // 2) Business validation and idempotent application happen in the handler.
                var result = await handler.HandleAsync(
                    new ProviderWebhookEvent(
                        MockPaymentProvider.ProviderName,
                        payload.Id,
                        payload.Type ?? string.Empty,
                        payload.Data.ProviderReference,
                        payload.Data.AmountMinor,
                        payload.Data.Currency ?? string.Empty,
                        payload.Data.FailureCode,
                        body),
                    ct);

                PayFlowMetrics.WebhooksReceived.Add(1, new KeyValuePair<string, object?>("result", result.ToString()));
                return Results.Ok(new { status = result.ToString() });
            })
            .AllowAnonymous()
            .WithTags("Webhooks")
            .WithSummary("Mock provider webhook (HMAC-SHA256 signature, replay window, event-id dedup)");

        return api;
    }

    private sealed record MockWebhookPayload(string? Id, string? Type, long Created, MockWebhookData? Data);

    private sealed record MockWebhookData(string? ProviderReference, long AmountMinor, string? Currency, string? FailureCode);
}
