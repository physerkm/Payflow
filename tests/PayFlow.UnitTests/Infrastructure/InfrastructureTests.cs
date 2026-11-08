using System.Text.Json;
using PayFlow.Application.Abstractions;
using PayFlow.Contracts;
using PayFlow.Contracts.Events;
using PayFlow.Domain.Payments;
using PayFlow.Infrastructure.Messaging;
using PayFlow.Infrastructure.PaymentProviders;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.UnitTests.Infrastructure;

public sealed class WebhookSignatureTests
{
    private const string Secret = "whsec_test";
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_signature_is_accepted()
    {
        var header = WebhookSignature.Create(Secret, Now, """{"id":"evt_1"}""");
        WebhookSignature.Verify(header, """{"id":"evt_1"}""", Secret, Now.AddSeconds(10)).ShouldBeTrue();
    }

    [Fact]
    public void Tampered_body_or_wrong_secret_is_rejected()
    {
        var header = WebhookSignature.Create(Secret, Now, """{"amountMinor":100}""");
        WebhookSignature.Verify(header, """{"amountMinor":999}""", Secret, Now).ShouldBeFalse();
        WebhookSignature.Verify(header, """{"amountMinor":100}""", "other", Now).ShouldBeFalse();
    }

    [Fact]
    public void Old_signatures_are_rejected_as_replays()
    {
        var header = WebhookSignature.Create(Secret, Now, "{}");
        WebhookSignature.Verify(header, "{}", Secret, Now.AddMinutes(6)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abc")]
    [InlineData("t=notanumber,v1=abc")]
    public void Malformed_headers_are_rejected(string? header) =>
        WebhookSignature.Verify(header, "{}", Secret, Now).ShouldBeFalse();
}

public sealed class IntegrationEventMapperTests
{
    private sealed class FixedCorrelation : ICorrelationContext
    {
        public string CorrelationId => "corr-123";
    }

    [Fact]
    public void Payment_requested_maps_to_versioned_envelope_without_sensitive_data()
    {
        var mapper = new IntegrationEventMapper(new FixedCorrelation());
        var now = DateTimeOffset.UtcNow;
        var tenant = Guid.NewGuid();
        var later = now.AddSeconds(5);

        var message = mapper.Map(new PaymentRequested(Guid.NewGuid(), Guid.NewGuid(), tenant, later, now), now)!;

        message.EventType.ShouldBe("PaymentRequested.v1");
        message.TenantId.ShouldBe(tenant);
        message.Status.ShouldBe(OutboxStatus.Pending);
        message.AvailableAt.ShouldBe(later); // delayed retry
        message.CorrelationId.ShouldBe("corr-123");

        var envelope = MessageEnvelope.Deserialize(System.Text.Encoding.UTF8.GetBytes(message.Payload));
        envelope.MessageId.ShouldBe(message.MessageId);
        envelope.TenantId.ShouldBe(tenant);
        message.Payload.ShouldNotContain("pm_test");
        envelope.GetPayload<PaymentRequestedV1>().PaymentId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Internal_domain_events_are_not_published()
    {
        var mapper = new IntegrationEventMapper(new FixedCorrelation());
        mapper.Map(new PayFlow.Domain.Orders.OrderCreated(Guid.NewGuid(), Guid.NewGuid(), 1, "TRY", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow).ShouldBeNull();
    }
}

public sealed class MessageEnvelopeTests
{
    [Fact]
    public void Envelope_round_trips_through_json()
    {
        var envelope = MessageEnvelope.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "c-1", new RefundRequestedV1(Guid.NewGuid(), Guid.NewGuid()));
        var json = envelope.Serialize();

        var copy = MessageEnvelope.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        copy.EventType.ShouldBe("RefundRequested.v1");
        copy.GetPayload<RefundRequestedV1>().ShouldBe(envelope.GetPayload<RefundRequestedV1>());
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("messageId", out _).ShouldBeTrue();
        doc.RootElement.TryGetProperty("occurredAt", out _).ShouldBeTrue();
    }

    [Fact]
    public void Every_event_type_is_bound_to_exactly_one_queue()
    {
        var bound = Topology.Bindings.SelectMany(b => b.Value).ToList();
        bound.ShouldBe(EventTypes.All, ignoreOrder: true);
        bound.Distinct().Count().ShouldBe(bound.Count);
    }
}
