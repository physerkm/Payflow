using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PayFlow.Infrastructure.Observability;

public static class PayFlowTelemetry
{
    public const string SourceName = "PayFlow";

    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
}

/// <summary>Business and reliability metrics (exported through OpenTelemetry → Prometheus).</summary>
public static class PayFlowMetrics
{
    public static readonly Meter Meter = new(PayFlowTelemetry.SourceName, "1.0.0");

    public static readonly Counter<long> PaymentOutcomes =
        Meter.CreateCounter<long>("payflow.payments.provider_outcomes", description: "Provider call outcomes by result");

    public static readonly Counter<long> ReconciliationResults =
        Meter.CreateCounter<long>("payflow.payments.reconciliations", description: "Reconciliation results");

    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("payflow.outbox.published", description: "Outbox messages published to the broker");

    public static readonly Counter<long> OutboxPublishFailures =
        Meter.CreateCounter<long>("payflow.outbox.publish_failures", description: "Failed outbox publish attempts");

    public static readonly Counter<long> MessageRetries =
        Meter.CreateCounter<long>("payflow.messages.retries", description: "Transport-level redeliveries scheduled");

    public static readonly Counter<long> MessagesDeadLettered =
        Meter.CreateCounter<long>("payflow.messages.dead_lettered", description: "Messages moved to a dead-letter queue");

    public static readonly Histogram<double> MessageProcessingDuration =
        Meter.CreateHistogram<double>("payflow.messages.processing_duration", unit: "ms", description: "Consumer processing time");

    public static readonly Counter<long> WebhooksReceived =
        Meter.CreateCounter<long>("payflow.webhooks.received", description: "Provider webhooks by result");

    private static long _outboxPending;
    private static double _outboxOldestPendingSeconds;
    private static IReadOnlyDictionary<string, long> _unresolvedAttempts = new Dictionary<string, long>();

    static PayFlowMetrics()
    {
        Meter.CreateObservableGauge(
            "payflow.payments.unresolved_attempts",
            () => Volatile.Read(ref _unresolvedAttempts)
                .Select(p => new Measurement<long>(p.Value, new KeyValuePair<string, object?>("status", p.Key))),
            description: "Provider attempts whose outcome is still open (reconciliation backlog)");
        Meter.CreateObservableGauge("payflow.outbox.pending", () => Volatile.Read(ref _outboxPending), description: "Pending outbox messages");
        Meter.CreateObservableGauge(
            "payflow.outbox.oldest_pending_age",
            () => Volatile.Read(ref _outboxOldestPendingSeconds),
            unit: "s",
            description: "Age of the oldest pending outbox message");
    }

    public static void SetOutboxBacklog(long pending, double oldestAgeSeconds)
    {
        Volatile.Write(ref _outboxPending, pending);
        Volatile.Write(ref _outboxOldestPendingSeconds, oldestAgeSeconds);
    }

    public static void SetUnresolvedAttempts(IReadOnlyDictionary<string, long> countsByStatus) =>
        Volatile.Write(ref _unresolvedAttempts, countsByStatus);

    public static void RecordMessageProcessed(string eventType, string result, TimeSpan duration) =>
        MessageProcessingDuration.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("result", result));
}
