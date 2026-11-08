using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Infrastructure.PaymentProviders;

/// <summary>
/// Plays the provider's role for asynchronous outcomes: resolves pending charges after a delay and delivers a
/// signed webhook to PayFlow, retrying until PayFlow answers 2xx. The event id is stable per transaction, so
/// retried deliveries are recognized as duplicates by the receiver.
/// </summary>
public sealed class MockWebhookSimulator(
    SystemDataSource system,
    IHttpClientFactory httpClientFactory,
    IOptions<MockProviderOptions> options,
    TimeProvider clock,
    ILogger<MockWebhookSimulator> logger)
{
    public const string HttpClientName = "mock-provider-webhooks";

    public async Task<int> ResolvePendingChargesAsync(CancellationToken cancellationToken)
    {
        await using var command = system.DataSource.CreateCommand(
            """
            UPDATE mock_provider_transactions
            SET status = 'Succeeded', resolved_at = @now, webhook_status = 'Pending'
            WHERE kind = 'charge' AND status = 'Pending' AND created_at < @before
            """);
        var now = clock.GetUtcNow();
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("before", now - options.Value.PendingResolutionDelay);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeliverWebhooksAsync(CancellationToken cancellationToken)
    {
        var target = options.Value.WebhookTargetUrl;
        if (string.IsNullOrWhiteSpace(target))
        {
            return 0;
        }

        var pending = new List<(Guid Id, string Reference, long Amount, string Currency, string Status, string? Failure)>();
        await using (var select = system.DataSource.CreateCommand(
            """
            SELECT id, provider_reference, amount_minor, currency, status, failure_code
            FROM mock_provider_transactions
            WHERE webhook_status = 'Pending' AND webhook_attempts < 20
            ORDER BY resolved_at
            LIMIT 20
            """))
        {
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3).Trim(), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var delivered = 0;
        var client = httpClientFactory.CreateClient(HttpClientName);
        foreach (var tx in pending)
        {
            var body = JsonSerializer.Serialize(new
            {
                id = $"evt_{tx.Id:N}",
                type = tx.Status == "Succeeded" ? "charge.succeeded" : "charge.failed",
                created = clock.GetUtcNow().ToUnixTimeSeconds(),
                data = new { providerReference = tx.Reference, amountMinor = tx.Amount, currency = tx.Currency, failureCode = tx.Failure },
            });

            var ok = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, target)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Create(options.Value.WebhookSecret, clock.GetUtcNow(), body));
                using var response = await client.SendAsync(request, cancellationToken);
                ok = response.IsSuccessStatusCode;
                if (!ok)
                {
                    logger.LogWarning("Mock webhook {Reference} rejected with {Status}", tx.Reference, (int)response.StatusCode);
                }
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Mock webhook delivery to {Target} failed; will retry", target);
            }

            await using var update = system.DataSource.CreateCommand(ok
                ? "UPDATE mock_provider_transactions SET webhook_status = 'Delivered', webhook_attempts = webhook_attempts + 1 WHERE id = @id"
                : "UPDATE mock_provider_transactions SET webhook_attempts = webhook_attempts + 1 WHERE id = @id");
            update.Parameters.AddWithValue("id", tx.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
            delivered += ok ? 1 : 0;
        }

        return delivered;
    }
}
