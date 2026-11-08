using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayFlow.Contracts;

/// <summary>
/// Transport envelope for every integration message. Contracts are versioned through the event type
/// suffix (e.g. <c>PaymentRequested.v1</c>) and must never carry card data, tokens or unnecessary PII.
/// </summary>
public sealed record MessageEnvelope(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? TenantId,
    string? CorrelationId,
    JsonElement Payload)
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MessageEnvelope Create<TPayload>(Guid messageId, DateTimeOffset occurredAt, Guid? tenantId, string? correlationId, TPayload payload)
        where TPayload : IIntegrationEvent =>
        new(messageId, TPayload.EventType, occurredAt, tenantId, correlationId, JsonSerializer.SerializeToElement(payload, SerializerOptions));

    public TPayload GetPayload<TPayload>()
        where TPayload : IIntegrationEvent =>
        Payload.Deserialize<TPayload>(SerializerOptions)
        ?? throw new JsonException($"Payload of message {MessageId} could not be deserialized as {typeof(TPayload).Name}.");

    public string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

    public static MessageEnvelope Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<MessageEnvelope>(utf8Json, SerializerOptions)
        ?? throw new JsonException("Message envelope is empty.");
}

public interface IIntegrationEvent
{
    static abstract string EventType { get; }
}
