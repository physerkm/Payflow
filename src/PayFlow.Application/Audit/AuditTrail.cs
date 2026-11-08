using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Audit;

namespace PayFlow.Application.Audit;

public static class AuditActions
{
    public const string UserRegistered = "auth.registered";
    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string Logout = "auth.logout";
    public const string RefreshReuseDetected = "auth.refresh_reuse_detected";
    public const string TenantActivated = "auth.tenant_activated";
    public const string TenantCreated = "tenant.created";
    public const string TenantUpdated = "tenant.updated";
    public const string MemberInvited = "member.invited";
    public const string InvitationRevoked = "member.invitation_revoked";
    public const string InvitationAccepted = "member.invitation_accepted";
    public const string MemberUpdated = "member.updated";
    public const string MemberRemoved = "member.removed";
    public const string OrderCancelled = "order.cancelled";
    public const string PaymentInitiated = "payment.initiated";
    public const string PaymentRetried = "payment.retried";
    public const string PaymentStatusChanged = "payment.status_changed";
    public const string RefundRequested = "refund.requested";
    public const string RefundCompleted = "refund.completed";
}

/// <summary>
/// Adds audit entries to the current unit of work, so they commit atomically with the business change.
/// Metadata must contain identifiers and outcomes only - never secrets, tokens or card data.
/// </summary>
public interface IAuditTrail
{
    void Record(string action, string resourceType, string? resourceId, object? metadata = null, Guid? tenantId = null);
}

internal sealed class AuditTrail(
    IAuditDb db,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    ICorrelationContext correlation,
    TimeProvider clock) : IAuditTrail
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Record(string action, string resourceType, string? resourceId, object? metadata = null, Guid? tenantId = null)
    {
        var entry = AuditLog.Create(
            tenantId ?? tenantContext.TenantId,
            currentUser.UserId,
            action,
            resourceType,
            resourceId,
            correlation.CorrelationId,
            metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions),
            clock.GetUtcNow());
        db.AuditLogs.Add(entry);
    }
}

public sealed record AuditLogDto(
    Guid Id,
    Guid? ActorUserId,
    string? ActorEmail,
    string Action,
    string ResourceType,
    string? ResourceId,
    string? CorrelationId,
    string? Metadata,
    DateTimeOffset OccurredAt);

public sealed class AuditQueries(IAuditDb db, ITenantContext tenantContext, IIdentityService identity)
{
    public async Task<PagedResult<AuditLogDto>> ListAsync(PageRequest page, string? action, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var query = db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action == action);
        }

        var result = await query.OrderByDescending(a => a.OccurredAt).ToPagedResultAsync(page, q => q, cancellationToken);
        var users = await identity.GetUsersAsync(
            result.Items.Where(a => a.ActorUserId is not null).Select(a => a.ActorUserId!.Value).Distinct().ToList(),
            cancellationToken);

        var items = result.Items.Select(a => new AuditLogDto(
            a.Id,
            a.ActorUserId,
            a.ActorUserId is { } uid && users.TryGetValue(uid, out var u) ? u.Email : null,
            a.Action,
            a.ResourceType,
            a.ResourceId,
            a.CorrelationId,
            a.Metadata,
            a.OccurredAt)).ToList();

        return new PagedResult<AuditLogDto>(items, result.Page, result.PageSize, result.TotalCount);
    }
}
