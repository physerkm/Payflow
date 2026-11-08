using PayFlow.Domain.Common;

namespace PayFlow.Domain.Tenants;

public enum TenantRole
{
    Owner,
    Admin,
    Finance,
    Operator,
    ReadOnly,
}

public enum MembershipStatus
{
    Active,
    Disabled,
}

/// <summary>Relationship between a user and a tenant, carrying the user's role in that tenant.</summary>
public sealed class TenantMembership : AggregateRoot, ITenantScoped
{
    private TenantMembership()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public TenantRole Role { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActiveOwner => Status == MembershipStatus.Active && Role == TenantRole.Owner;

    public static TenantMembership Create(Guid tenantId, Guid userId, TenantRole role, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = Guard.NotEmpty(tenantId, "tenant_required"),
        UserId = Guard.NotEmpty(userId, "user_required"),
        Role = role,
        Status = MembershipStatus.Active,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void ChangeRole(TenantRole newRole, DateTimeOffset now)
    {
        Role = newRole;
        UpdatedAt = now;
    }

    public void Disable(DateTimeOffset now)
    {
        Status = MembershipStatus.Disabled;
        UpdatedAt = now;
    }

    public void Enable(DateTimeOffset now)
    {
        Status = MembershipStatus.Active;
        UpdatedAt = now;
    }
}

/// <summary>
/// Rules that span several memberships of the same tenant (privilege escalation and last-owner protection).
/// The caller must load the active owner count under a tenant-level lock to make the check race-free.
/// </summary>
public static class MembershipPolicy
{
    public static void EnsureCanAssign(TenantRole actorRole, TenantRole roleToAssign)
    {
        if (actorRole is not (TenantRole.Owner or TenantRole.Admin))
        {
            throw new DomainException("membership_forbidden", "Only owners and admins can manage members.", DomainErrorKind.Forbidden);
        }

        if (roleToAssign == TenantRole.Owner && actorRole != TenantRole.Owner)
        {
            throw new DomainException("membership_owner_only", "Only an owner can grant the Owner role.", DomainErrorKind.Forbidden);
        }
    }

    public static void EnsureCanModify(TenantRole actorRole, TenantMembership target)
    {
        if (actorRole is not (TenantRole.Owner or TenantRole.Admin))
        {
            throw new DomainException("membership_forbidden", "Only owners and admins can manage members.", DomainErrorKind.Forbidden);
        }

        if (target.Role == TenantRole.Owner && actorRole != TenantRole.Owner)
        {
            throw new DomainException("membership_owner_only", "Only an owner can modify another owner.", DomainErrorKind.Forbidden);
        }
    }

    /// <summary>Throws when the change would leave the tenant without an active owner.</summary>
    public static void EnsureOwnerRemains(TenantMembership target, int activeOwnerCount, TenantRole? newRole, bool disablingOrRemoving)
    {
        var losesOwnership = target.IsActiveOwner && (disablingOrRemoving || (newRole is not null && newRole != TenantRole.Owner));
        if (losesOwnership && activeOwnerCount <= 1)
        {
            throw new DomainException(
                "membership_last_owner",
                "The last owner of a tenant cannot be removed, disabled or demoted.",
                DomainErrorKind.Conflict);
        }
    }
}
