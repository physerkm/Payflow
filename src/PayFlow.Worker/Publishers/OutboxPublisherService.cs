using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Messaging;

namespace PayFlow.Worker.Publishers;

/// <summary>
/// Continuously drains the transactional outbox. When the broker is down, messages stay safely in PostgreSQL
/// with a growing back-off and are published as soon as the broker returns.
/// </summary>
internal sealed class OutboxPublisherService(
    IServiceScopeFactory scopes,
    IOptions<MessagingOptions> options,
    WorkerHealth health,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastMetrics = DateTimeOffset.MinValue;
        logger.LogInformation("Outbox publisher started (batch {BatchSize}, poll {Interval})", options.Value.OutboxBatchSize, options.Value.OutboxPollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                published = await processor.PublishPendingAsync(options.Value.OutboxBatchSize, stoppingToken);

                if (DateTimeOffset.UtcNow - lastMetrics > MetricsInterval)
                {
                    await processor.RefreshBacklogMetricsAsync(stoppingToken);
                    lastMetrics = DateTimeOffset.UtcNow;
                }

                health.OutboxHeartbeat = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox publishing cycle failed");
                await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            if (published < options.Value.OutboxBatchSize)
            {
                await DelayAsync(options.Value.OutboxPollInterval, stoppingToken);
            }
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
