using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure.Messaging;

namespace PayFlow.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Apply pending migrations at startup (development convenience; production uses the migrator job).</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Set app.tenant_id for every command so PostgreSQL RLS policies are enforced for the app role.</summary>
    public bool EnableRowLevelSecurity { get; set; }
}

public static class DatabaseMigrator
{
    /// <summary>Applies EF Core migrations using the given (privileged) connection string.</summary>
    public static async Task MigrateAsync(string connectionString, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext(connectionString);
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await context.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database schema is up to date");
    }

    internal static PayFlowDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<PayFlowDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PayFlowDbContext(options, new TenantContext(), new NullEventMapper(), TimeProvider.System);
    }

    private sealed class NullEventMapper : IIntegrationEventMapper
    {
        public OutboxMessage? Map(Domain.Common.IDomainEvent domainEvent, DateTimeOffset now) => null;
    }
}

/// <summary>Used by <c>dotnet ef</c> to create migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PayFlowDbContext>
{
    public PayFlowDbContext CreateDbContext(string[] args) =>
        DatabaseMigrator.CreateContext(
            Environment.GetEnvironmentVariable("PAYFLOW_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=payflow;Username=payflow;Password=payflow");
}

internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.", ex);
        }
    }
}

internal sealed class RabbitMqHealthCheck(RabbitMqConnectionProvider connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await connections.GetConnectionAsync(cancellationToken);
            return connection.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("RabbitMQ connection is recovering.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degraded, not unhealthy: payments are still accepted and safely queued in the outbox.
            return HealthCheckResult.Degraded("RabbitMQ is not reachable; outbox messages are buffered in PostgreSQL.", ex);
        }
    }
}
