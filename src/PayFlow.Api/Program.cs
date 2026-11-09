using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using PayFlow.Api.Endpoints;
using PayFlow.Api.Extensions;
using PayFlow.Api.Middleware;
using PayFlow.Application;
using InfrastructureSetup = PayFlow.Infrastructure.DependencyInjection;
using PayFlow.Infrastructure;
using PayFlow.Infrastructure.Observability;
using PayFlow.Infrastructure.Persistence;
using Scalar.AspNetCore;

if (args.Contains(HealthProbe.Argument))
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// `dotnet PayFlow.Api.dll --migrate` applies migrations with the privileged connection string and exits.
// Used by the one-shot "migrator" container so the API itself never needs DDL rights.
if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
    var connectionString = builder.Configuration.GetConnectionString(InfrastructureSetup.MigrationsConnectionStringName)
        ?? InfrastructureSetup.RequireConnectionString(builder.Configuration, InfrastructureSetup.ConnectionStringName);
    await DatabaseMigrator.MigrateAsync(connectionString, loggerFactory.CreateLogger("PayFlow.Migrator"));
    return 0;
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 1024 * 1024; // resource-exhaustion guard
});

builder.AddPayFlowObservability("payflow-api");
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddApi(builder.Configuration);

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
{
    var connectionString = app.Configuration.GetConnectionString(InfrastructureSetup.MigrationsConnectionStringName)
        ?? InfrastructureSetup.RequireConnectionString(app.Configuration, InfrastructureSetup.ConnectionStringName);
    await DatabaseMigrator.MigrateAsync(connectionString, app.Logger);
}

if (app.Configuration.GetValue<bool>("ReverseProxy:Enabled"))
{
    // Behind the bundled nginx: take client IP/scheme from X-Forwarded-* (used by rate limiting and logs).
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<IdempotencyBufferingMiddleware>();
app.UseCors();
app.UseRequestTimeouts();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("PayFlow API"));
}

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints()
    .MapTenantEndpoints()
    .MapCustomerEndpoints()
    .MapProductEndpoints()
    .MapOrderEndpoints()
    .MapPaymentEndpoints()
    .MapReportEndpoints()
    .MapWebhookEndpoints()
    .MapAssistantEndpoints()
    .MapPlatformEndpoints();

app.Logger.LogInformation("PayFlow API started ({Environment})", app.Environment.EnvironmentName);
await app.RunAsync();
return 0;

namespace PayFlow.Api
{
    /// <summary>Marker type for WebApplicationFactory in integration tests.</summary>
    public sealed class ApiAssemblyMarker;
}
