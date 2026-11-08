using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure;
using PayFlow.Infrastructure.Messaging;
using PayFlow.Worker.Consumers;
using PayFlow.Worker.Jobs;
using PayFlow.Worker.Publishers;

namespace PayFlow.Worker;

public sealed class WorkerHealth
{
    public DateTimeOffset? OutboxHeartbeat { get; set; }
}

public static class WorkerServiceCollectionExtensions
{
    /// <summary>Registers everything the Worker process runs: transport, consumers, outbox publisher and jobs.</summary>
    public static IServiceCollection AddPayFlowWorker(
        this IServiceCollection services,
        IConfiguration configuration,
        string? transportOverride = null,
        bool enableBackgroundJobs = true)
    {
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));
        services.TryAddScoped<ICurrentUser, SystemCurrentUser>();
        services.AddSingleton<WorkerHealth>();

        services.AddMessagingTransport(configuration, transportOverride);
        services.AddSingleton<IMessageDispatcher, MessageDispatcher>();
        services.AddScoped<IMessageConsumer, PaymentRequestedConsumer>();
        services.AddScoped<IMessageConsumer, RefundRequestedConsumer>();
        services.AddScoped<IMessageConsumer, NotificationConsumer>();

        services.AddHostedService<OutboxPublisherService>();
        if (enableBackgroundJobs)
        {
            services.AddHostedService<PaymentReconciliationJob>();
            services.AddHostedService<RetentionJob>();
            services.AddHostedService<MockProviderSimulationJob>();
        }

        services.AddHealthChecks().AddCheck<OutboxHeartbeatHealthCheck>("outbox-publisher", tags: ["ready"]);
        return services;
    }
}

/// <summary>Background processing has no end user; audit entries record a null actor (system).</summary>
internal sealed class SystemCurrentUser : ICurrentUser
{
    public Guid? UserId => null;

    public string? Email => null;

    public bool IsAuthenticated => false;
}

internal sealed class OutboxHeartbeatHealthCheck(WorkerHealth health) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var heartbeat = health.OutboxHeartbeat;
        if (heartbeat is null)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Outbox publisher has not completed a cycle yet."));
        }

        return Task.FromResult(DateTimeOffset.UtcNow - heartbeat < TimeSpan.FromMinutes(1)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Outbox publisher is stalled."));
    }
}
