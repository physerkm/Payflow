using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Infrastructure.PaymentProviders;

public sealed class MockProviderOptions
{
    public const string SectionName = "MockProvider";

    /// <summary>Shared secret for HMAC-SHA256 webhook signatures (test value only).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Where the simulated provider delivers webhooks (the API's /webhooks/payments/mock endpoint).</summary>
    public string? WebhookTargetUrl { get; set; }

    /// <summary>How long a pm_test_pending charge stays pending before the provider resolves it.</summary>
    public TimeSpan PendingResolutionDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan SimulatedLatency { get; set; } = TimeSpan.FromMilliseconds(80);

    public bool EnableWebhookSimulator { get; set; } = true;
}

/// <summary>Test tokens understood by the mock provider. No real card data ever reaches PayFlow.</summary>
public static class MockTokens
{
    public const string Success = "pm_test_success";
    public const string Decline = "pm_test_decline";
    public const string InsufficientFunds = "pm_test_insufficient_funds";
    public const string Timeout = "pm_test_timeout";
    public const string Error = "pm_test_error";
    public const string PersistentError = "pm_test_error_persistent";
    public const string Pending = "pm_test_pending";
}

/// <summary>
/// Deterministic simulated provider. Its ledger table stands in for the provider's own database, so it is
/// written outside PayFlow's business transactions (via its own connection) - exactly like an external system.
/// Repeated calls with the same idempotency key return the original result (provider-side idempotency).
/// </summary>
public sealed class MockPaymentProvider(SystemDataSource system, IOptions<MockProviderOptions> options, TimeProvider clock, ILogger<MockPaymentProvider> logger)
    : IPaymentProvider
{
    public const string ProviderName = "mock";

    public string Name => ProviderName;

    public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken);

        var existing = await FindAsync("idempotency_key = @value", request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation("Mock provider: idempotent replay for key {Key}", request.IdempotencyKey);
            return ToChargeResult(existing);
        }

        switch (request.PaymentMethodToken)
        {
            case MockTokens.Success:
                return ToChargeResult(await InsertChargeAsync(request, "Succeeded", null, null, cancellationToken));

            case MockTokens.Decline:
                return ToChargeResult(await InsertChargeAsync(request, "Declined", "card_declined", null, cancellationToken));

            case MockTokens.InsufficientFunds:
                return ToChargeResult(await InsertChargeAsync(request, "Declined", "insufficient_funds", null, cancellationToken));

            case MockTokens.Timeout:
                // The provider executes the charge, but the response is "lost": the caller sees a timeout.
                await InsertChargeAsync(request, "Succeeded", null, null, cancellationToken);
                return new PaymentResult(ChargeOutcome.Timeout);

            case MockTokens.Error when request.AttemptNumber <= 1:
            case MockTokens.PersistentError:
                // Technical failure before anything is executed; nothing is recorded at the provider.
                return new PaymentResult(ChargeOutcome.TransientError, FailureCode: "provider_unavailable");

            case MockTokens.Error:
                return ToChargeResult(await InsertChargeAsync(request, "Succeeded", null, null, cancellationToken));

            case MockTokens.Pending:
                return ToChargeResult(await InsertChargeAsync(request, "Pending", null, null, cancellationToken));

            default:
                return ToChargeResult(await InsertChargeAsync(request, "Declined", "invalid_payment_method", null, cancellationToken));
        }
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken);

        var existing = await FindAsync("idempotency_key = @value", request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return existing.Status == "Succeeded"
                ? new RefundResult(RefundOutcome.Succeeded, existing.ProviderReference)
                : new RefundResult(RefundOutcome.Declined, existing.ProviderReference, existing.FailureCode);
        }

        var charge = await FindAsync("provider_reference = @value AND kind = 'charge'", request.ProviderPaymentReference, cancellationToken);
        if (charge is null || charge.Status != "Succeeded")
        {
            return await InsertRefundAsync(request, "Declined", "charge_not_refundable", cancellationToken);
        }

        await using var command = system.DataSource.CreateCommand(
            "SELECT COALESCE(SUM(amount_minor), 0) FROM mock_provider_transactions WHERE kind = 'refund' AND status = 'Succeeded' AND related_reference = @reference");
        command.Parameters.AddWithValue("reference", charge.ProviderReference);
        var alreadyRefunded = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (alreadyRefunded + request.AmountMinor > charge.AmountMinor)
        {
            return await InsertRefundAsync(request, "Declined", "amount_exceeds_charge", cancellationToken);
        }

        return await InsertRefundAsync(request, "Succeeded", null, cancellationToken);
    }

    public async Task<PaymentStatusResult> GetPaymentStatusAsync(string providerReference, CancellationToken cancellationToken)
    {
        await SimulateLatencyAsync(cancellationToken);
        var charge = await FindAsync("(provider_reference = @value OR idempotency_key = @value) AND kind = 'charge'", providerReference, cancellationToken);
        if (charge is null)
        {
            return new PaymentStatusResult(ProviderPaymentStatus.NotFound);
        }

        var status = charge.Status switch
        {
            "Succeeded" => ProviderPaymentStatus.Succeeded,
            "Declined" => ProviderPaymentStatus.Declined,
            _ => ProviderPaymentStatus.Pending,
        };
        return new PaymentStatusResult(status, charge.ProviderReference, charge.AmountMinor, charge.Currency, charge.FailureCode);
    }

    private static PaymentResult ToChargeResult(MockProviderTransaction tx) => tx.Status switch
    {
        "Succeeded" => new PaymentResult(ChargeOutcome.Succeeded, tx.ProviderReference),
        "Declined" => new PaymentResult(ChargeOutcome.Declined, tx.ProviderReference, tx.FailureCode),
        _ => new PaymentResult(ChargeOutcome.Pending, tx.ProviderReference),
    };

    private async Task<MockProviderTransaction> InsertChargeAsync(PaymentRequest request, string status, string? failureCode, string? related, CancellationToken cancellationToken) =>
        await InsertAsync("charge", request.IdempotencyKey, "mock_ch_", request.PaymentMethodToken, request.AmountMinor, request.Currency, status, failureCode, related, cancellationToken);

    private async Task<RefundResult> InsertRefundAsync(RefundRequest request, string status, string? failureCode, CancellationToken cancellationToken)
    {
        var tx = await InsertAsync("refund", request.IdempotencyKey, "mock_re_", null, request.AmountMinor, request.Currency, status, failureCode, request.ProviderPaymentReference, cancellationToken);
        return tx.Status == "Succeeded"
            ? new RefundResult(RefundOutcome.Succeeded, tx.ProviderReference)
            : new RefundResult(RefundOutcome.Declined, tx.ProviderReference, tx.FailureCode);
    }

    private async Task<MockProviderTransaction> InsertAsync(
        string kind,
        string idempotencyKey,
        string referencePrefix,
        string? token,
        long amountMinor,
        string currency,
        string status,
        string? failureCode,
        string? related,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var command = system.DataSource.CreateCommand(
            """
            INSERT INTO mock_provider_transactions
                (id, kind, idempotency_key, provider_reference, related_reference, payment_method_token, amount_minor, currency, status, failure_code, webhook_attempts, created_at, resolved_at)
            VALUES (@id, @kind, @key, @reference, @related, @token, @amount, @currency, @status, @failure, 0, @now, @resolved)
            ON CONFLICT (idempotency_key) DO NOTHING
            """);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7(now));
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.AddWithValue("reference", referencePrefix + Guid.NewGuid().ToString("N")[..24]);
        command.Parameters.AddWithValue("related", (object?)related ?? DBNull.Value);
        command.Parameters.AddWithValue("token", (object?)token ?? DBNull.Value);
        command.Parameters.AddWithValue("amount", amountMinor);
        command.Parameters.AddWithValue("currency", currency);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("failure", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("resolved", status == "Pending" ? DBNull.Value : now);
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Re-read: a concurrent call with the same key may have won the insert.
        return await FindAsync("idempotency_key = @value", idempotencyKey, cancellationToken)
            ?? throw new InvalidOperationException("Mock provider transaction was not persisted.");
    }

    private async Task<MockProviderTransaction?> FindAsync(string where, string value, CancellationToken cancellationToken)
    {
        await using var command = system.DataSource.CreateCommand(
            $"""
            SELECT id, kind, idempotency_key, provider_reference, related_reference, amount_minor, currency, status, failure_code, created_at
            FROM mock_provider_transactions WHERE {where} LIMIT 1
            """);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MockProviderTransaction
        {
            Id = reader.GetGuid(0),
            Kind = reader.GetString(1),
            IdempotencyKey = reader.GetString(2),
            ProviderReference = reader.GetString(3),
            RelatedReference = reader.IsDBNull(4) ? null : reader.GetString(4),
            AmountMinor = reader.GetInt64(5),
            Currency = reader.GetString(6).Trim(),
            Status = reader.GetString(7),
            FailureCode = reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        };
    }

    private Task SimulateLatencyAsync(CancellationToken cancellationToken)
    {
        var latency = options.Value.SimulatedLatency;
        return latency > TimeSpan.Zero ? Task.Delay(latency, clock, cancellationToken) : Task.CompletedTask;
    }
}

/// <summary>Adds an OpenTelemetry span and outcome metrics around every provider call.</summary>
internal static class ProviderActivity
{
    public static Activity? Start(string operation, string provider)
    {
        var activity = Observability.PayFlowTelemetry.ActivitySource.StartActivity($"payment_provider {operation}", ActivityKind.Client);
        activity?.SetTag("payflow.provider", provider);
        return activity;
    }
}
