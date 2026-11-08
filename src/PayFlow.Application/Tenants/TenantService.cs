using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Domain.Common;
using PayFlow.Domain.Tenants;

namespace PayFlow.Application.Tenants;

public sealed record CreateTenantRequest(string Name, string Slug, string DefaultCurrency, string TimeZone);

public sealed record UpdateTenantRequest(string Name, string DefaultCurrency, string TimeZone);

public sealed record TenantDto(Guid Id, string Name, string Slug, TenantStatus Status, string DefaultCurrency, string TimeZone, DateTimeOffset CreatedAt);

public sealed record MemberDto(Guid MembershipId, Guid UserId, string Email, string DisplayName, TenantRole Role, MembershipStatus Status, DateTimeOffset CreatedAt);

public sealed record InviteMemberRequest(string Email, TenantRole Role);

public sealed record InvitationDto(Guid Id, string Email, TenantRole Role, InvitationStatus Status, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

public sealed record PendingInvitationDto(Guid Id, Guid TenantId, string TenantName, TenantRole Role, DateTimeOffset ExpiresAt);

public sealed record UpdateMemberRequest(TenantRole? Role, MembershipStatus? Status);

public sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Slug).NotEmpty().Matches("^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$")
            .WithMessage("Slug must be 3-50 lowercase letters, digits or hyphens.");
        RuleFor(x => x.DefaultCurrency).Must(CurrencyCatalog.IsSupported).WithMessage("Unsupported currency.");
        RuleFor(x => x.TimeZone).NotEmpty().Must(TimeZoneValidation.IsValid).WithMessage("Unknown IANA time zone.");
    }
}

public sealed class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    public UpdateTenantRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.DefaultCurrency).Must(CurrencyCatalog.IsSupported).WithMessage("Unsupported currency.");
        RuleFor(x => x.TimeZone).NotEmpty().Must(TimeZoneValidation.IsValid).WithMessage("Unknown IANA time zone.");
    }
}

public sealed class InviteMemberRequestValidator : AbstractValidator<InviteMemberRequest>
{
    public InviteMemberRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Role).IsInEnum();
    }
}

public sealed class UpdateMemberRequestValidator : AbstractValidator<UpdateMemberRequest>
{
    public UpdateMemberRequestValidator()
    {
        RuleFor(x => x).Must(x => x.Role is not null || x.Status is not null).WithMessage("Role or status must be provided.");
        RuleFor(x => x.Role).IsInEnum().When(x => x.Role is not null);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status is not null);
    }
}

public static class TimeZoneValidation
{
    public static bool IsValid(string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return timeZone.Contains('/', StringComparison.Ordinal) || timeZone == "UTC";
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}

public sealed class TenantService(
    ITenantsDb db,
    IUnitOfWork unitOfWork,
    IIdentityService identity,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    IAuditTrail audit,
    TimeProvider clock)
{
    public async Task<TenantDto> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var slug = Tenant.NormalizeSlug(request.Slug);
        if (await db.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken))
        {
            throw new ConflictException("tenant_slug_taken", "This slug is already in use.");
        }

        var now = clock.GetUtcNow();
        var tenant = Tenant.Create(request.Name, slug, request.DefaultCurrency, request.TimeZone, now);
        var membership = TenantMembership.Create(tenant.Id, userId, TenantRole.Owner, now);
        db.Tenants.Add(tenant);
        db.TenantMemberships.Add(membership);
        audit.Record(AuditActions.TenantCreated, "tenant", tenant.Id.ToString(), new { tenant.Slug }, tenant.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(tenant);
    }

    public async Task<TenantDto> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var tenant = await LoadCurrentTenantAsync(cancellationToken);
        return ToDto(tenant);
    }

    public async Task<TenantDto> UpdateCurrentAsync(UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        var tenant = await LoadCurrentTenantAsync(cancellationToken);
        tenant.UpdateSettings(request.Name, request.DefaultCurrency, request.TimeZone, clock.GetUtcNow());
        audit.Record(AuditActions.TenantUpdated, "tenant", tenant.Id.ToString(), new { request.DefaultCurrency, request.TimeZone });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(tenant);
    }

    public async Task<IReadOnlyList<MemberDto>> ListMembersAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var memberships = await db.TenantMemberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        var users = await identity.GetUsersAsync(memberships.Select(m => m.UserId).ToList(), cancellationToken);
        return memberships
            .Select(m => users.TryGetValue(m.UserId, out var u)
                ? new MemberDto(m.Id, m.UserId, u.Email, u.DisplayName, m.Role, m.Status, m.CreatedAt)
                : new MemberDto(m.Id, m.UserId, "(deleted user)", "(deleted user)", m.Role, m.Status, m.CreatedAt))
            .ToList();
    }

    public async Task<InvitationDto> InviteAsync(InviteMemberRequest request, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        MembershipPolicy.EnsureCanAssign(tenantContext.RequireRole(), request.Role);

        var normalizedEmail = identity.NormalizeEmail(request.Email);
        var existingUser = await identity.FindByEmailAsync(request.Email, cancellationToken);
        if (existingUser is not null &&
            await db.TenantMemberships.AnyAsync(m => m.TenantId == tenantId && m.UserId == existingUser.Id, cancellationToken))
        {
            throw new ConflictException("member_exists", "This user is already a member of the tenant.");
        }

        var now = clock.GetUtcNow();
        if (await db.TenantInvitations.AnyAsync(
                i => i.TenantId == tenantId && i.EmailNormalized == normalizedEmail && i.Status == InvitationStatus.Pending && i.ExpiresAt > now,
                cancellationToken))
        {
            throw new ConflictException("invitation_exists", "A pending invitation already exists for this email.");
        }

        var invitation = TenantInvitation.Create(tenantId, normalizedEmail, request.Role, currentUser.RequireUserId(), now);
        db.TenantInvitations.Add(invitation);
        audit.Record(AuditActions.MemberInvited, "invitation", invitation.Id.ToString(), new { role = request.Role.ToString() });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(invitation, request.Email.Trim());
    }

    public async Task<IReadOnlyList<InvitationDto>> ListInvitationsAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var invitations = await db.TenantInvitations.AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        return invitations.Select(i => ToDto(i, i.EmailNormalized.ToLowerInvariant())).ToList();
    }

    public async Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var invitation = await db.TenantInvitations.SingleOrDefaultAsync(i => i.Id == invitationId && i.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException("Invitation");
        invitation.Revoke();
        audit.Record(AuditActions.InvitationRevoked, "invitation", invitation.Id.ToString());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<MemberDto> UpdateMemberAsync(Guid membershipId, UpdateMemberRequest request, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var actorRole = tenantContext.RequireRole();

        return await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                // Serialize owner-affecting changes per tenant so two concurrent demotions cannot remove the last owner.
                await db.LockTenantAsync(tenantId, ct);
                var target = await LoadMembershipAsync(tenantId, membershipId, ct);
                MembershipPolicy.EnsureCanModify(actorRole, target);
                if (request.Role is { } newRole)
                {
                    MembershipPolicy.EnsureCanAssign(actorRole, newRole);
                }

                var activeOwners = await CountActiveOwnersAsync(tenantId, ct);
                MembershipPolicy.EnsureOwnerRemains(target, activeOwners, request.Role, request.Status == MembershipStatus.Disabled);

                var now = clock.GetUtcNow();
                if (request.Role is { } role)
                {
                    target.ChangeRole(role, now);
                }

                if (request.Status == MembershipStatus.Disabled)
                {
                    target.Disable(now);
                }
                else if (request.Status == MembershipStatus.Active)
                {
                    target.Enable(now);
                }

                audit.Record(
                    AuditActions.MemberUpdated,
                    "membership",
                    target.Id.ToString(),
                    new { role = request.Role?.ToString(), status = request.Status?.ToString() });
                await unitOfWork.SaveChangesAsync(ct);

                var user = await identity.GetUserAsync(target.UserId, ct);
                return new MemberDto(target.Id, target.UserId, user?.Email ?? "", user?.DisplayName ?? "", target.Role, target.Status, target.CreatedAt);
            },
            cancellationToken);
    }

    public async Task RemoveMemberAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var actorRole = tenantContext.RequireRole();

        await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                await db.LockTenantAsync(tenantId, ct);
                var target = await LoadMembershipAsync(tenantId, membershipId, ct);
                MembershipPolicy.EnsureCanModify(actorRole, target);
                MembershipPolicy.EnsureOwnerRemains(target, await CountActiveOwnersAsync(tenantId, ct), null, disablingOrRemoving: true);

                db.TenantMemberships.Remove(target);
                audit.Record(AuditActions.MemberRemoved, "membership", target.Id.ToString(), new { userId = target.UserId });
                await unitOfWork.SaveChangesAsync(ct);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<PendingInvitationDto>> GetMyInvitationsAsync(CancellationToken cancellationToken)
    {
        var user = await identity.GetUserAsync(currentUser.RequireUserId(), cancellationToken)
            ?? throw new AuthenticationFailedException("user_inactive", "Your account is not active.");
        var now = clock.GetUtcNow();
        return await (
                from i in db.TenantInvitations.AsNoTracking()
                join t in db.Tenants.AsNoTracking() on i.TenantId equals t.Id
                where i.EmailNormalized == user.EmailNormalized && i.Status == InvitationStatus.Pending && i.ExpiresAt > now
                orderby i.CreatedAt descending
                select new PendingInvitationDto(i.Id, t.Id, t.Name, i.Role, i.ExpiresAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<PendingInvitationDto> AcceptInvitationAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        var user = await identity.GetUserAsync(currentUser.RequireUserId(), cancellationToken)
            ?? throw new AuthenticationFailedException("user_inactive", "Your account is not active.");

        // Invitations are looked up by id AND the caller's email, so foreign invitations look like "not found".
        var invitation = await db.TenantInvitations
            .SingleOrDefaultAsync(i => i.Id == invitationId && i.EmailNormalized == user.EmailNormalized, cancellationToken)
            ?? throw new NotFoundException("Invitation");

        var now = clock.GetUtcNow();
        invitation.Accept(user.Id, user.EmailNormalized, now);

        if (await db.TenantMemberships.AnyAsync(m => m.TenantId == invitation.TenantId && m.UserId == user.Id, cancellationToken))
        {
            throw new ConflictException("member_exists", "You are already a member of this tenant.");
        }

        db.TenantMemberships.Add(TenantMembership.Create(invitation.TenantId, user.Id, invitation.Role, now));
        audit.Record(AuditActions.InvitationAccepted, "invitation", invitation.Id.ToString(), tenantId: invitation.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var tenantName = await db.Tenants.Where(t => t.Id == invitation.TenantId).Select(t => t.Name).SingleAsync(cancellationToken);
        return new PendingInvitationDto(invitation.Id, invitation.TenantId, tenantName, invitation.Role, invitation.ExpiresAt);
    }

    private async Task<Tenant> LoadCurrentTenantAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        return await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant");
    }

    private async Task<TenantMembership> LoadMembershipAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        await db.TenantMemberships.SingleOrDefaultAsync(m => m.Id == membershipId && m.TenantId == tenantId, cancellationToken)
        ?? throw new NotFoundException("Membership");

    private Task<int> CountActiveOwnersAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.TenantMemberships.CountAsync(
            m => m.TenantId == tenantId && m.Role == TenantRole.Owner && m.Status == MembershipStatus.Active,
            cancellationToken);

    private static TenantDto ToDto(Tenant t) => new(t.Id, t.Name, t.Slug, t.Status, t.DefaultCurrency, t.TimeZone, t.CreatedAt);

    private static InvitationDto ToDto(TenantInvitation i, string email) => new(i.Id, email, i.Role, i.Status, i.ExpiresAt, i.CreatedAt);
}
