namespace PayFlow.Contracts.Events;

/// <summary>A payment attempt is ready to be sent to the provider.</summary>
public sealed record PaymentRequestedV1(Guid PaymentId, Guid PaymentAttemptId) : IIntegrationEvent
{
    public static string EventType => "PaymentRequested.v1";
}

public sealed record PaymentSucceededV1(Guid PaymentId, Guid OrderId, long AmountMinor, string Currency) : IIntegrationEvent
{
    public static string EventType => "PaymentSucceeded.v1";
}

public sealed record PaymentFailedV1(Guid PaymentId, Guid OrderId, string FailureCode) : IIntegrationEvent
{
    public static string EventType => "PaymentFailed.v1";
}

/// <summary>A refund was requested and must be executed through the provider.</summary>
public sealed record RefundRequestedV1(Guid RefundId, Guid PaymentId) : IIntegrationEvent
{
    public static string EventType => "RefundRequested.v1";
}

public sealed record RefundSucceededV1(Guid RefundId, Guid PaymentId, long AmountMinor, string Currency) : IIntegrationEvent
{
    public static string EventType => "RefundSucceeded.v1";
}

public sealed record RefundFailedV1(Guid RefundId, Guid PaymentId, string FailureCode) : IIntegrationEvent
{
    public static string EventType => "RefundFailed.v1";
}

public static class EventTypes
{
    public static readonly string PaymentRequested = PaymentRequestedV1.EventType;
    public static readonly string PaymentSucceeded = PaymentSucceededV1.EventType;
    public static readonly string PaymentFailed = PaymentFailedV1.EventType;
    public static readonly string RefundRequested = RefundRequestedV1.EventType;
    public static readonly string RefundSucceeded = RefundSucceededV1.EventType;
    public static readonly string RefundFailed = RefundFailedV1.EventType;

    public static IReadOnlyList<string> All { get; } =
        [PaymentRequested, PaymentSucceeded, PaymentFailed, RefundRequested, RefundSucceeded, RefundFailed];
}
