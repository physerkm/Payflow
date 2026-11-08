using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Contracts;
using PayFlow.Infrastructure.Observability;

namespace PayFlow.Infrastructure.Messaging;

/// <summary>
/// Single-process transport for local development without Docker and for integration tests. The outbox and
/// consumers are exactly the same as with RabbitMQ; only the hop between them is an in-memory channel.
/// </summary>
public sealed class InMemoryMessageBus : IMessageBus
{
    private readonly Channel<OutgoingMessage> _channel = Channel.CreateUnbounded<OutgoingMessage>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<OutgoingMessage> Reader => _channel.Reader;

    /// <summary>Messages that exhausted their retries (inspected by tests).</summary>
    public ConcurrentQueue<(OutgoingMessage Message, Exception Error)> DeadLetters { get; } = new();

    /// <summary>When set, publishing throws - used to simulate a broker outage.</summary>
    public bool SimulateOutage { get; set; }

    public Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        if (SimulateOutage)
        {
            throw new InvalidOperationException("Simulated broker outage.");
        }

        return _channel.Writer.WriteAsync(message, cancellationToken).AsTask();
    }
}

public sealed class InMemoryConsumerService(
    InMemoryMessageBus bus,
    IMessageDispatcher dispatcher,
    IOptions<MessagingOptions> options,
    ILogger<InMemoryConsumerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in bus.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task ProcessAsync(OutgoingMessage message, CancellationToken stoppingToken)
    {
        var envelope = MessageEnvelope.Deserialize(System.Text.Encoding.UTF8.GetBytes(message.Body));
        var maxRetries = options.Value.MaxDeliveryRetries;
        for (var attempt = 0; ; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await dispatcher.DispatchAsync(envelope, message.TraceParent, stoppingToken);
                PayFlowMetrics.RecordMessageProcessed(envelope.EventType, "success", stopwatch.Elapsed);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (ex is NonRetryableMessageException || attempt >= maxRetries)
                {
                    logger.LogError(ex, "Message {MessageId} ({EventType}) dead-lettered", envelope.MessageId, envelope.EventType);
                    bus.DeadLetters.Enqueue((message, ex));
                    PayFlowMetrics.MessagesDeadLettered.Add(1);
                    return;
                }

                logger.LogWarning(ex, "Message {MessageId} failed (attempt {Attempt}); retrying", envelope.MessageId, attempt + 1);
                PayFlowMetrics.MessageRetries.Add(1);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), stoppingToken);
            }
        }
    }
}
