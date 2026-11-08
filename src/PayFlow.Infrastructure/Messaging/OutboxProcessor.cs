using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayFlow.Infrastructure.Observability;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Infrastructure.Messaging;

/// <summary>
/// Publishes pending outbox messages. Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so several worker
/// instances can run in parallel. A message is marked published only after the broker confirmed it; a crash in
/// between causes a duplicate publish, which consumers absorb through the inbox (at-least-once delivery).
/// </summary>
public sealed class OutboxProcessor(PayFlowDbContext db, IMessageBus bus, TimeProvider clock, ILogger<OutboxProcessor> logger)
{
    public async Task<int> PublishPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var messages = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE status = 'Pending' AND available_at <= {now}
                ORDER BY available_at, created_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var message in messages)
        {
            using var activity = StartPublishActivity(message);
            try
            {
                await bus.PublishAsync(
                    new OutgoingMessage(message.MessageId, message.EventType, message.Payload, message.TenantId, message.CorrelationId, Activity.Current?.Id ?? message.TraceParent),
                    cancellationToken);
                message.Status = OutboxStatus.Published;
                message.PublishedAt = clock.GetUtcNow();
                message.LastError = null;
                published++;
                PayFlowMetrics.OutboxPublished.Add(1, new KeyValuePair<string, object?>("event_type", message.EventType));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Broker unavailable: keep the message (it is safe in PostgreSQL) and back off.
                message.PublishAttempts++;
                message.LastError = Truncate(ex.Message, 1000);
                message.AvailableAt = clock.GetUtcNow().AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(message.PublishAttempts, 6))));
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                PayFlowMetrics.OutboxPublishFailures.Add(1);
                logger.LogWarning(ex, "Outbox publish failed for message {MessageId} ({EventType}); attempt {Attempt}", message.MessageId, message.EventType, message.PublishAttempts);
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return published;
    }

    public async Task RefreshBacklogMetricsAsync(CancellationToken cancellationToken)
    {
        var stats = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.LongCount(), Oldest = g.Min(m => m.CreatedAt) })
            .SingleOrDefaultAsync(cancellationToken);

        var age = stats is null ? 0 : Math.Max(0, (clock.GetUtcNow() - stats.Oldest).TotalSeconds);
        PayFlowMetrics.SetOutboxBacklog(stats?.Count ?? 0, age);
    }

    private static Activity? StartPublishActivity(OutboxMessage message)
    {
        // Continue the trace of the HTTP request that wrote the outbox row.
        ActivityContext.TryParse(message.TraceParent, null, out var parent);
        var activity = PayFlowTelemetry.ActivitySource.StartActivity($"outbox publish {message.EventType}", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.message.id", message.MessageId.ToString());
        activity?.SetTag("messaging.destination.name", message.EventType);
        activity?.SetTag("payflow.tenant_id", message.TenantId?.ToString());
        return activity;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
