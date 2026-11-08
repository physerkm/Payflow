using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PayFlow.Infrastructure.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Traces (HTTP → use case → DB → outbox → RabbitMQ → consumer → provider), metrics and logs via OpenTelemetry.
    /// OTLP export is enabled when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (or Observability:OtlpEndpoint) is set.
    /// </summary>
    public static IHostApplicationBuilder AddPayFlowObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? builder.Configuration["Observability:OtlpEndpoint"];
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Logging.Configure(options => options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString())
                .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(PayFlowTelemetry.SourceName)
                    .AddSource("RabbitMQ.Client.Publisher", "RabbitMQ.Client.Subscriber")
                    .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(PayFlowTelemetry.SourceName, "Npgsql")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                }
            });

        if (exportOtlp)
        {
            otel.WithLogging(logging => logging.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!)), options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
            });
        }

        return builder;
    }
}
