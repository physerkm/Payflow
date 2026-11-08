using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Contracts;
using PayFlow.Infrastructure.Observability;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PayFlow.Infrastructure.Messaging;

/// <summary>Owns the single long-lived AMQP connection (automatic recovery enabled).</summary>
public sealed class RabbitMqConnectionProvider(IOptions<MessagingOptions> options, ILogger<RabbitMqConnectionProvider> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public bool IsConnected => _connection?.IsOpen == true;

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection; // automatic recovery reconnects an existing connection after outages
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var o = options.Value.RabbitMq;
            var factory = new ConnectionFactory
            {
                HostName = o.Host,
                Port = o.Port,
                UserName = o.UserName,
                Password = o.Password,
                VirtualHost = o.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                ClientProvidedName = $"payflow-{Environment.MachineName}",
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}", o.Host, o.Port);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _lock.Dispose();
    }
}

public static class RabbitMqTopology
{
    /// <summary>Idempotently declares exchanges, consumer queues, per-level retry queues and dead-letter queues.</summary>
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Topology.EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(Topology.RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(Topology.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        foreach (var (queue, routingKeys) in Topology.Bindings)
        {
            await channel.QueueDeclareAsync(
                queue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
                    ["x-dead-letter-routing-key"] = queue,
                },
                cancellationToken: cancellationToken);

            foreach (var routingKey in routingKeys)
            {
                await channel.QueueBindAsync(queue, Topology.EventsExchange, routingKey, cancellationToken: cancellationToken);
            }

            var deadLetterQueue = Topology.DeadLetterQueue(queue);
            await channel.QueueDeclareAsync(deadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(deadLetterQueue, Topology.DeadLetterExchange, queue, cancellationToken: cancellationToken);

            for (var level = 1; level <= Topology.RetryDelays.Length; level++)
            {
                // Messages wait here for the TTL, then the broker dead-letters them back to the main queue.
                var retryQueue = Topology.RetryQueue(queue, level);
                await channel.QueueDeclareAsync(
                    retryQueue,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: new Dictionary<string, object?>
                    {
                        ["x-message-ttl"] = (int)Topology.RetryDelays[level - 1].TotalMilliseconds,
                        ["x-dead-letter-exchange"] = string.Empty,
                        ["x-dead-letter-routing-key"] = queue,
                    },
                    cancellationToken: cancellationToken);
                await channel.QueueBindAsync(retryQueue, Topology.RetryExchange, retryQueue, cancellationToken: cancellationToken);
            }
        }
    }
}

/// <summary>Publishes persistent messages with publisher confirms and mandatory routing.</summary>
public sealed class RabbitMqMessageBus(RabbitMqConnectionProvider connections, ILogger<RabbitMqMessageBus> logger) : IMessageBus, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);
            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = message.MessageId.ToString(),
                Type = message.EventType,
                ContentType = "application/json",
                CorrelationId = message.CorrelationId,
                Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Headers = new Dictionary<string, object?>(),
            };
            if (message.TraceParent is not null)
            {
                properties.Headers["traceparent"] = message.TraceParent;
            }

            // With confirmation tracking enabled this completes only after the broker ack, and throws on
            // nack or when a mandatory message could not be routed to any queue.
            await channel.BasicPublishAsync(
                Topology.EventsExchange,
                message.EventType,
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(message.Body),
                cancellationToken: cancellationToken);
        }
        catch
        {
            await ResetChannelAsync();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetChannelAsync();
        _lock.Dispose();
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        var connection = await connections.GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await RabbitMqTopology.DeclareAsync(channel, cancellationToken);
        _channel = channel;
        logger.LogInformation("RabbitMQ publisher channel opened and topology declared");
        return channel;
    }

    private async Task ResetChannelAsync()
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            await _channel.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "Ignoring error while disposing the publisher channel");
        }

        _channel = null;
    }
}

/// <summary>
/// Consumes every PayFlow queue with manual acks. Failures are re-published to a delayed retry queue
/// (increasing delays); after the retry budget, or for non-retryable errors, the message is dead-lettered.
/// </summary>
public sealed class RabbitMqConsumerService(
    RabbitMqConnectionProvider connections,
    IMessageDispatcher dispatcher,
    IOptions<MessagingOptions> options,
    ILogger<RabbitMqConsumerService> logger) : BackgroundService
{
    private const string RetryHeader = "x-payflow-retry";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channels = new List<IChannel>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await connections.GetConnectionAsync(stoppingToken);
                foreach (var queue in Topology.Bindings.Keys)
                {
                    var channel = await connection.CreateChannelAsync(
                        new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                        stoppingToken);
                    await RabbitMqTopology.DeclareAsync(channel, stoppingToken);
                    await channel.BasicQosAsync(0, options.Value.RabbitMq.PrefetchCount, global: false, stoppingToken);

                    var consumer = new AsyncEventingBasicConsumer(channel);
                    var queueName = queue;
                    consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, queueName, delivery, stoppingToken);
                    await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
                    channels.Add(channel);
                    logger.LogInformation("Consuming queue {Queue}", queue);
                }

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RabbitMQ consumer setup failed; retrying in 5 seconds");
                foreach (var channel in channels)
                {
                    await channel.DisposeAsync();
                }

                channels.Clear();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        foreach (var channel in channels)
        {
            await channel.DisposeAsync();
        }
    }

    private async Task HandleDeliveryAsync(IChannel channel, string queue, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var retryCount = ReadIntHeader(delivery.BasicProperties.Headers, RetryHeader);
        var traceParent = ReadStringHeader(delivery.BasicProperties.Headers, "traceparent");
        var stopwatch = Stopwatch.StartNew();

        MessageEnvelope envelope;
        try
        {
            envelope = MessageEnvelope.Deserialize(delivery.Body.Span);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            logger.LogError(ex, "Malformed message on {Queue}; dead-lettering", queue);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
            PayFlowMetrics.MessagesDeadLettered.Add(1, new KeyValuePair<string, object?>("queue", queue));
            return;
        }

        try
        {
            await dispatcher.DispatchAsync(envelope, traceParent, stoppingToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            PayFlowMetrics.RecordMessageProcessed(envelope.EventType, "success", stopwatch.Elapsed);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            var maxRetries = Math.Min(options.Value.MaxDeliveryRetries, Topology.RetryDelays.Length);
            if (ex is NonRetryableMessageException || retryCount >= maxRetries)
            {
                logger.LogError(ex, "Message {MessageId} ({EventType}) dead-lettered after {RetryCount} retries", envelope.MessageId, envelope.EventType, retryCount);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                PayFlowMetrics.MessagesDeadLettered.Add(1, new KeyValuePair<string, object?>("queue", queue));
                PayFlowMetrics.RecordMessageProcessed(envelope.EventType, "dead_lettered", stopwatch.Elapsed);
                return;
            }

            var nextLevel = retryCount + 1;
            logger.LogWarning(ex, "Message {MessageId} ({EventType}) failed; scheduling retry {Retry}", envelope.MessageId, envelope.EventType, nextLevel);
            var properties = new BasicProperties(delivery.BasicProperties)
            {
                Headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
                {
                    [RetryHeader] = nextLevel,
                },
            };
            await channel.BasicPublishAsync(Topology.RetryExchange, Topology.RetryQueue(queue, nextLevel), mandatory: true, properties, delivery.Body, stoppingToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            PayFlowMetrics.MessageRetries.Add(1, new KeyValuePair<string, object?>("queue", queue));
            PayFlowMetrics.RecordMessageProcessed(envelope.EventType, "retry", stopwatch.Elapsed);
        }
    }

    private static int ReadIntHeader(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var value) && value is not null
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

    private static string? ReadStringHeader(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string s => s,
                _ => null,
            }
            : null;
}
