using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayFlow.Contracts;
using PayFlow.Contracts.Events;
using PayFlow.Infrastructure.Messaging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace PayFlow.IntegrationTests;

/// <summary>
/// Messaging integration against a real RabbitMQ (Testcontainers). Skipped automatically when Docker is not
/// available; runs in CI on GitHub-hosted Linux runners.
/// </summary>
public sealed class RabbitMqTransportTests : IAsyncLifetime
{
    private RabbitMqContainer? _container;
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            _unavailable = $"Docker is not available: {ex.GetType().Name}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Published_message_is_confirmed_routed_and_consumed()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? string.Empty);
        var (bus, consumer, dispatcher, connections) = Create(_ => Task.CompletedTask);
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        var envelope = MessageEnvelope.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "c-1", new PaymentRequestedV1(Guid.NewGuid(), Guid.NewGuid()));
        await bus.PublishAsync(new OutgoingMessage(envelope.MessageId, envelope.EventType, envelope.Serialize(), envelope.TenantId, "c-1", null), TestContext.Current.CancellationToken);

        await WaitAsync(() => dispatcher.Received.Any(e => e.MessageId == envelope.MessageId));
        await consumer.StopAsync(CancellationToken.None);
        await connections.DisposeAsync();
    }

    [Fact]
    public async Task Non_retryable_failures_are_dead_lettered()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? string.Empty);
        var (bus, consumer, _, connections) = Create(_ => throw new NonRetryableMessageException("poison"));
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        var envelope = MessageEnvelope.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), null, new RefundRequestedV1(Guid.NewGuid(), Guid.NewGuid()));
        await bus.PublishAsync(new OutgoingMessage(envelope.MessageId, envelope.EventType, envelope.Serialize(), envelope.TenantId, null, null), TestContext.Current.CancellationToken);

        var connection = await connections.GetConnectionAsync(TestContext.Current.CancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        await WaitAsync(async () => await channel.MessageCountAsync(Topology.DeadLetterQueue(Topology.RefundsQueue)) == 1);

        await consumer.StopAsync(CancellationToken.None);
        await connections.DisposeAsync();
    }

    [Fact]
    public async Task Unroutable_messages_fail_the_publish_instead_of_being_lost()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? string.Empty);
        var (bus, _, _, connections) = Create(_ => Task.CompletedTask);

        await Should.ThrowAsync<Exception>(() => bus.PublishAsync(new OutgoingMessage(Guid.NewGuid(), "Unknown.v1", "{}", null, null, null), TestContext.Current.CancellationToken));
        await connections.DisposeAsync();
    }

    private (RabbitMqMessageBus Bus, RabbitMqConsumerService Consumer, RecordingDispatcher Dispatcher, RabbitMqConnectionProvider Connections) Create(Func<MessageEnvelope, Task> behavior)
    {
        var uri = new Uri(_container!.GetConnectionString());
        var userInfo = uri.UserInfo.Split(':');
        var options = Options.Create(new MessagingOptions
        {
            Transport = "RabbitMq",
            RabbitMq = new RabbitMqOptions { Host = uri.Host, Port = uri.Port, UserName = userInfo[0], Password = userInfo[1] },
        });
        var connections = new RabbitMqConnectionProvider(options, NullLogger<RabbitMqConnectionProvider>.Instance);
        var dispatcher = new RecordingDispatcher(behavior);
        return (
            new RabbitMqMessageBus(connections, NullLogger<RabbitMqMessageBus>.Instance),
            new RabbitMqConsumerService(connections, dispatcher, options, NullLogger<RabbitMqConsumerService>.Instance),
            dispatcher,
            connections);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("RabbitMQ condition not met in time.");
    }

    private static Task WaitAsync(Func<bool> condition) => WaitAsync(() => Task.FromResult(condition()));

    private sealed class RecordingDispatcher(Func<MessageEnvelope, Task> behavior) : IMessageDispatcher
    {
        public ConcurrentBag<MessageEnvelope> Received { get; } = [];

        public async Task DispatchAsync(MessageEnvelope envelope, string? traceParent, CancellationToken cancellationToken)
        {
            Received.Add(envelope);
            await behavior(envelope);
        }
    }
}
