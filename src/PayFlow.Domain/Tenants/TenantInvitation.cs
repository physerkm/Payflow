using PayFlow.Domain.Common;

namespace PayFlow.Domain.Tenants;

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
}

/// <summary>An invitation for an email address to join a tenant with a given role.</summary>
public sealed class TenantInvitation : AggregateRoot, ITenantScoped
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    private TenantInvitation()
    {
    }

    public Guid TenantId { get; private set; }

    public string EmailNormalized { get; private set; } = default!;

    public TenantRole Role { get; private set; }

    public InvitationStatus Status { get; private set; }

    public Guid InvitedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public Guid? AcceptedByUserId { get; private set; }

    public static TenantInvitation Create(Guid tenantId, string email, TenantRole role, Guid invitedBy, DateTimeOffset now)
    {
        var normalized = Guard.NotEmpty(email, "invitation_email_required", "Email is required", 256).ToUpperInvariant();
        return new TenantInvitation
        {
            Id = Guid.CreateVersion7(now),
            TenantId = Guard.NotEmpty(tenantId, "tenant_required"),
            EmailNormalized = normalized,
            Role = role,
            Status = InvitationStatus.Pending,
            InvitedByUserId = invitedBy,
            CreatedAt = now,
            ExpiresAt = now.Add(DefaultLifetime),
        };
    }

    public bool IsOpen(DateTimeOffset now) => Status == InvitationStatus.Pending && ExpiresAt > now;

    public void Accept(Guid userId, string userEmailNormalized, DateTimeOffset now)
    {
        if (!IsOpen(now))
        {
            throw new DomainException("invitation_not_open", "The invitation is no longer valid.", DomainErrorKind.Conflict);
        }

        if (!string.Equals(EmailNormalized, userEmailNormalized, StringComparison.Ordinal))
        {
            throw new DomainException("invitation_email_mismatch", "The invitation was issued for a different email address.", DomainErrorKind.Conflict);
        }

        Status = InvitationStatus.Accepted;
        AcceptedAt = now;
        AcceptedByUserId = userId;
    }

    public void Revoke()
    {
        if (Status != InvitationStatus.Pending)
        {
            throw new DomainException("invitation_not_open", "Only pending invitations can be revoked.", DomainErrorKind.Conflict);
        }

        Status = InvitationStatus.Revoked;
    }
}
