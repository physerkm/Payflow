using PayFlow.Application.Authorization;
using PayFlow.Domain.Tenants;

namespace PayFlow.Application.Abstractions;

/// <summary>The authenticated caller (null user for system processes such as the worker).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    string? Email { get; }

    bool IsAuthenticated { get; }

    Guid RequireUserId() => UserId ?? throw new ForbiddenException("authentication_required", "An authenticated user is required.");
}

/// <summary>
/// The active tenant for the current request or message. It is resolved on the server from a verified,
/// active membership (API) or from a validated message (worker) - never from a client header alone.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    Guid? MembershipId { get; }

    TenantRole? Role { get; }

    /// <summary>True for background processing on behalf of a tenant (no user involved).</summary>
    bool IsSystem { get; }

    bool HasTenant => TenantId is not null;

    Guid RequireTenantId() =>
        TenantId ?? throw new ForbiddenException("tenant_required", "An active tenant must be selected for this operation.");

    TenantRole RequireRole() =>
        Role ?? throw new ForbiddenException("tenant_required", "An active tenant membership is required for this operation.");

    bool HasPermission(string permission) => Role is { } role && RolePermissions.For(role).Contains(permission);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public Guid? MembershipId { get; private set; }

    public TenantRole? Role { get; private set; }

    public bool IsSystem { get; private set; }

    public void SetMember(Guid tenantId, Guid membershipId, TenantRole role)
    {
        TenantId = tenantId;
        MembershipId = membershipId;
        Role = role;
        IsSystem = false;
    }

    public void SetSystem(Guid tenantId)
    {
        TenantId = tenantId;
        MembershipId = null;
        Role = null;
        IsSystem = true;
    }

    public void Clear()
    {
        TenantId = null;
        MembershipId = null;
        Role = null;
        IsSystem = false;
    }
}

public interface ICorrelationContext
{
    string CorrelationId { get; }
}

public sealed class CorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
}
