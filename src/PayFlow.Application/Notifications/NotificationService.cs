using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Orders;
using PayFlow.Domain.Common;
using PayFlow.Domain.Notifications;

namespace PayFlow.Application.Notifications;

public sealed record NotificationDto(
    Guid Id,
    string Type,
    NotificationSeverity Severity,
    string Title,
    string Message,
    string? ResourceType,
    Guid? ResourceId,
    DateTimeOffset CreatedAt);

public sealed class NotificationService(INotificationsDb db)
{
    public async Task<IReadOnlyList<NotificationDto>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        await db.Notifications.AsNoTracking()
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(n => new NotificationDto(n.Id, n.Type, n.Severity, n.Title, n.Message, n.ResourceType, n.ResourceId, n.CreatedAt))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Turns payment/refund integration events into in-app notifications (MVP channel). Email/SMS channels can be
/// added later behind the same consumer without touching the payment flow.
/// </summary>
public sealed class NotificationHandler(
    INotificationsDb db,
    IUnitOfWork unitOfWork,
    IInbox inbox,
    IOrderLookup orderLookup,
    ITenantContext tenantContext,
    TimeProvider clock,
    ILogger<NotificationHandler> logger)
{
    public Task PaymentSucceededAsync(MessageContext message, Guid paymentId, Guid orderId, long amountMinor, string currency, CancellationToken cancellationToken) =>
        CreateAsync(
            message,
            orderId,
            orderNumber => ("payment.succeeded", NotificationSeverity.Success, "Payment succeeded", $"{FormatAmount(amountMinor, currency)} collected for order {orderNumber}."),
            "payment",
            paymentId,
            cancellationToken);

    public Task PaymentFailedAsync(MessageContext message, Guid paymentId, Guid orderId, string failureCode, CancellationToken cancellationToken) =>
        CreateAsync(
            message,
            orderId,
            orderNumber => ("payment.failed", NotificationSeverity.Error, "Payment failed", $"Payment for order {orderNumber} failed ({failureCode})."),
            "payment",
            paymentId,
            cancellationToken);

    public Task RefundSucceededAsync(MessageContext message, Guid refundId, Guid paymentId, long amountMinor, string currency, CancellationToken cancellationToken) =>
        CreateAsync(
            message,
            null,
            _ => ("refund.succeeded", NotificationSeverity.Info, "Refund completed", $"{FormatAmount(amountMinor, currency)} refunded."),
            "payment",
            paymentId,
            cancellationToken);

    public Task RefundFailedAsync(MessageContext message, Guid refundId, Guid paymentId, string failureCode, CancellationToken cancellationToken) =>
        CreateAsync(
            message,
            null,
            _ => ("refund.failed", NotificationSeverity.Warning, "Refund failed", $"A refund could not be completed ({failureCode})."),
            "payment",
            paymentId,
            cancellationToken);

    public static string FormatAmount(long amountMinor, string currency)
    {
        var units = CurrencyCatalog.IsSupported(currency) ? CurrencyCatalog.GetMinorUnits(currency) : 2;
        var major = amountMinor / (decimal)Math.Pow(10, units);
        return string.Create(CultureInfo.InvariantCulture, $"{major.ToString("N" + units, CultureInfo.InvariantCulture)} {currency}");
    }

    private async Task CreateAsync(
        MessageContext message,
        Guid? orderId,
        Func<string, (string Type, NotificationSeverity Severity, string Title, string Text)> build,
        string resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        var orderNumber = orderId is { } id
            ? (await orderLookup.GetOrderNumbersAsync([id], cancellationToken)).GetValueOrDefault(id) ?? id.ToString()[..8]
            : string.Empty;
        var (type, severity, title, text) = build(orderNumber);

        await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                if (!await inbox.TryRegisterAsync(message, ct))
                {
                    return;
                }

                db.Notifications.Add(Notification.Create(
                    tenantContext.RequireTenantId(), type, severity, title, text, resourceType, resourceId, message.MessageId, clock.GetUtcNow()));
                await unitOfWork.SaveChangesAsync(ct);
                logger.LogInformation("Notification {Type} created for {ResourceType} {ResourceId}", type, resourceType, resourceId);
            },
            cancellationToken);
    }
}
