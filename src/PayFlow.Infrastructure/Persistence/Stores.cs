using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Payments;

namespace PayFlow.Infrastructure.Persistence;

/// <summary>Inbox: INSERT ... ON CONFLICT DO NOTHING inside the business transaction.</summary>
internal sealed class Inbox(PayFlowDbContext db, TimeProvider clock) : IInbox
{
    public Task<bool> HasProcessedAsync(MessageContext message, CancellationToken cancellationToken) =>
        db.InboxMessages.AsNoTracking().AnyAsync(m => m.ConsumerName == message.ConsumerName && m.MessageId == message.MessageId, cancellationToken);

    public async Task<bool> TryRegisterAsync(MessageContext message, CancellationToken cancellationToken)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inbox_messages (consumer_name, message_id, processed_at)
            VALUES ({message.ConsumerName}, {message.MessageId}, {clock.GetUtcNow()})
            ON CONFLICT (consumer_name, message_id) DO NOTHING
            """,
            cancellationToken);
        return rows == 1;
    }
}

/// <summary>Idempotency records reserved inside the caller's transaction (see <see cref="IIdempotencyStore"/>).</summary>
internal sealed class IdempotencyStore(PayFlowDbContext db, TimeProvider clock) : IIdempotencyStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<IdempotencyBeginResult> BeginAsync(Guid tenantId, string operation, string key, string requestHash, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);

        // If another transaction holds the same key, this INSERT waits for it to finish, then does nothing.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO idempotency_records (id, tenant_id, operation, key, request_hash, created_at, expires_at)
            VALUES ({id}, {tenantId}, {operation}, {key}, {requestHash}, {now}, {now.Add(Retention)})
            ON CONFLICT (tenant_id, operation, key) DO NOTHING
            """,
            cancellationToken);
        if (inserted == 1)
        {
            return new IdempotencyBeginResult.Started(id);
        }

        var existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleAsync(r => r.TenantId == tenantId && r.Operation == operation && r.Key == key, cancellationToken);

        if (existing.ExpiresAt <= now)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE idempotency_records
                SET request_hash = {requestHash}, response_status = NULL, response_body = NULL, response_location = NULL,
                    created_at = {now}, completed_at = NULL, expires_at = {now.Add(Retention)}
                WHERE id = {existing.Id}
                """,
                cancellationToken);
            return new IdempotencyBeginResult.Started(existing.Id);
        }

        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new IdempotencyBeginResult.Mismatch();
        }

        if (existing.ResponseStatus is null)
        {
            throw new ConflictException("idempotency_in_progress", "A request with this idempotency key is still being processed.");
        }

        return new IdempotencyBeginResult.Replay(existing.ResponseStatus.Value, existing.ResponseBody, existing.ResponseLocation);
    }

    public Task CompleteAsync(Guid recordId, int statusCode, string? responseBody, string? location, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE idempotency_records
            SET response_status = {statusCode}, response_body = {responseBody}, response_location = {location}, completed_at = {clock.GetUtcNow()}
            WHERE id = {recordId}
            """,
            cancellationToken);
}

internal sealed class WebhookEventStore(PayFlowDbContext db, TimeProvider clock) : IWebhookEventStore
{
    public async Task<bool> TryRecordAsync(string provider, string providerEventId, string payload, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO webhook_events (id, provider, provider_event_id, payload, status, received_at)
            VALUES ({Guid.CreateVersion7(now)}, {provider}, {providerEventId}, CAST({payload} AS jsonb), 'Received', {now})
            ON CONFLICT (provider, provider_event_id) DO NOTHING
            """,
            cancellationToken);
        return rows == 1;
    }

    public Task MarkAsync(string provider, string providerEventId, string status, string? error, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE webhook_events SET status = {status}, error = {error}, processed_at = {clock.GetUtcNow()}
            WHERE provider = {provider} AND provider_event_id = {providerEventId}
            """,
            cancellationToken);
}

/// <summary>A second data source for system-level, cross-tenant lookups (may use a role that bypasses RLS).</summary>
public sealed class SystemDataSource(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource { get; } = dataSource;
}

internal sealed class SystemPaymentQueries(SystemDataSource system) : ISystemPaymentQueries
{
    public async Task<AttemptLocator?> FindAttemptByProviderReferenceAsync(string provider, string providerReference, CancellationToken cancellationToken)
    {
        await using var command = system.DataSource.CreateCommand(
            "SELECT tenant_id, payment_id, id, status FROM payment_attempts WHERE provider = @provider AND provider_reference = @reference LIMIT 1");
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("reference", providerReference);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLocator(reader) : null;
    }

    public async Task<IReadOnlyList<AttemptLocator>> FindAttemptsNeedingReconciliationAsync(
        DateTimeOffset unknownBefore,
        DateTimeOffset providerPendingBefore,
        DateTimeOffset processingBefore,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = system.DataSource.CreateCommand(
            """
            SELECT tenant_id, payment_id, id, status FROM payment_attempts
            WHERE (status = 'Unknown' AND updated_at < @unknownBefore)
               OR (status = 'ProviderPending' AND updated_at < @pendingBefore)
               OR (status = 'Processing' AND updated_at < @processingBefore)
            ORDER BY updated_at
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("unknownBefore", unknownBefore);
        command.Parameters.AddWithValue("pendingBefore", providerPendingBefore);
        command.Parameters.AddWithValue("processingBefore", processingBefore);
        command.Parameters.AddWithValue("limit", limit);

        var result = new List<AttemptLocator>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadLocator(reader));
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<PaymentAttemptStatus, long>> CountUnresolvedAttemptsAsync(CancellationToken cancellationToken)
    {
        await using var command = system.DataSource.CreateCommand(
            """
            SELECT status, count(*) FROM payment_attempts
            WHERE status IN ('Processing', 'ProviderPending', 'Unknown')
            GROUP BY status
            """);
        var counts = new Dictionary<PaymentAttemptStatus, long>
        {
            [PaymentAttemptStatus.Processing] = 0,
            [PaymentAttemptStatus.ProviderPending] = 0,
            [PaymentAttemptStatus.Unknown] = 0,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[Enum.Parse<PaymentAttemptStatus>(reader.GetString(0))] = reader.GetInt64(1);
        }

        return counts;
    }

    private static AttemptLocator ReadLocator(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        Enum.Parse<PaymentAttemptStatus>(reader.GetString(3)));
}
