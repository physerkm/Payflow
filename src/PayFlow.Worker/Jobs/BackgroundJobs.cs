using Microsoft.Extensions.Options;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Payments;
using PayFlow.Application.Payments.Processing;
using PayFlow.Infrastructure.Observability;
using PayFlow.Infrastructure.PaymentProviders;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Worker.Jobs;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan MockProviderInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>Base class for interval jobs: runs, logs failures, never crashes the host.</summary>
internal abstract class IntervalJob(ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    protected abstract Task RunOnceAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Job} run failed", GetType().Name);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Resolves unknown, provider-pending and stuck attempts by asking the provider (never by blind retry).</summary>
internal sealed class PaymentReconciliationJob(
    IServiceScopeFactory scopes,
    ISystemPaymentQueries systemQueries,
    IOptions<PaymentProcessingOptions> paymentOptions,
    IOptions<WorkerOptions> workerOptions,
    TimeProvider clock,
    ILogger<PaymentReconciliationJob> logger) : IntervalJob(logger)
{
    protected override TimeSpan Interval => workerOptions.Value.ReconciliationInterval;

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var options = paymentOptions.Value;
        var candidates = await systemQueries.FindAttemptsNeedingReconciliationAsync(
            now - options.ReconcileUnknownAfter,
            now - options.ReconcileProviderPendingAfter,
            now - options.ReconcileProcessingAfter,
            limit: 50,
            cancellationToken);

        foreach (var candidate in candidates)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystem(candidate.TenantId);
                var handler = scope.ServiceProvider.GetRequiredService<ReconcilePaymentHandler>();
                var result = await handler.HandleAsync(candidate.PaymentId, candidate.AttemptId, cancellationToken);
                PayFlowMetrics.ReconciliationResults.Add(1, new KeyValuePair<string, object?>("result", result.ToString()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reconciliation of attempt {AttemptId} failed; will retry next cycle", candidate.AttemptId);
            }
        }
    }
}

/// <summary>Runs the simulated provider's asynchronous behaviour (pending resolution + signed webhooks).</summary>
internal sealed class MockProviderSimulationJob(
    MockWebhookSimulator simulator,
    IOptions<MockProviderOptions> providerOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<MockProviderSimulationJob> logger) : IntervalJob(logger)
{
    protected override TimeSpan Interval => workerOptions.Value.MockProviderInterval;

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!providerOptions.Value.EnableWebhookSimulator)
        {
            return;
        }

        var resolved = await simulator.ResolvePendingChargesAsync(cancellationToken);
        var delivered = await simulator.DeliverWebhooksAsync(cancellationToken);
        if (resolved + delivered > 0)
        {
            logger.LogInformation("Mock provider resolved {Resolved} pending charge(s), delivered {Delivered} webhook(s)", resolved, delivered);
        }
    }
}

/// <summary>
/// Deletes processed outbox/inbox rows and expired idempotency keys across all tenants. Runs on the system data
/// source because it is a cross-tenant maintenance task (the application role only sees one tenant under RLS).
/// </summary>
internal sealed class RetentionJob(SystemDataSource system, IOptions<WorkerOptions> workerOptions, TimeProvider clock, ILogger<RetentionJob> logger)
    : IntervalJob(logger)
{
    protected override TimeSpan Interval => workerOptions.Value.RetentionInterval;

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var outbox = await ExecuteAsync("DELETE FROM outbox_messages WHERE status = 'Published' AND published_at < @before", now - workerOptions.Value.OutboxRetention, cancellationToken);
        var inbox = await ExecuteAsync("DELETE FROM inbox_messages WHERE processed_at < @before", now - workerOptions.Value.InboxRetention, cancellationToken);
        var keys = await ExecuteAsync("DELETE FROM idempotency_records WHERE expires_at < @before", now, cancellationToken);

        if (outbox + inbox + keys > 0)
        {
            logger.LogInformation("Retention removed {Outbox} outbox, {Inbox} inbox and {Keys} idempotency rows", outbox, inbox, keys);
        }
    }

    private async Task<int> ExecuteAsync(string sql, DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var command = system.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("before", before);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
