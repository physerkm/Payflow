using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PayFlow.Application;
using PayFlow.Infrastructure;
using PayFlow.Infrastructure.Observability;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Worker;
using InfrastructureSetup = PayFlow.Infrastructure.DependencyInjection;

if (args.Contains(HealthProbe.Argument))
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.AddPayFlowObservability("payflow-worker");
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddPayFlowWorker(builder.Configuration);

// Graceful shutdown: give in-flight messages time to finish before the host stops.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
{
    var connectionString = app.Configuration.GetConnectionString(InfrastructureSetup.MigrationsConnectionStringName)
        ?? InfrastructureSetup.RequireConnectionString(app.Configuration, InfrastructureSetup.ConnectionStringName);
    await DatabaseMigrator.MigrateAsync(connectionString, app.Logger);
}

// The worker exposes only health endpoints (for container liveness/readiness probes).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Logger.LogInformation("PayFlow Worker started ({Environment})", app.Environment.EnvironmentName);
await app.RunAsync();
return 0;
