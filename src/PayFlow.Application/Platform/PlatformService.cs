using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Domain.Tenants;

namespace PayFlow.Application.Platform;

public enum PlatformRole
{
    PlatformAdmin,
    SupportAgent,
}

/// <summary>
/// Platform staff are configured by email (bootstrap without a separate admin UI). They manage tenants as a whole
/// but never get access to a tenant's business data (customers, orders, payments).
/// </summary>
public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    public string[] Admins { get; set; } = [];

    public string[] SupportAgents { get; set; } = [];
}

public interface IPlatformRoles
{
    PlatformRole? GetRole(string? email);
}

internal sealed class PlatformRoles(IOptions<PlatformOptions> options, IIdentityService identity) : IPlatformRoles
{
    public PlatformRole? GetRole(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalized = identity.NormalizeEmail(email);
        if (options.Value.Admins.Any(a => identity.NormalizeEmail(a) == normalized))
        {
            return PlatformRole.PlatformAdmin;
        }

        return options.Value.SupportAgents.Any(a => identity.NormalizeEmail(a) == normalized) ? PlatformRole.SupportAgent : null;
    }
}

public sealed record PlatformTenantDto(
    Guid Id,
    string Name,
    string Slug,
    TenantStatus Status,
    string DefaultCurrency,
    int ActiveMembers,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PlatformTenantQuery(string? Search, TenantStatus? Status, int Page = 1, int PageSize = 20);

public sealed record SetTenantStatusRequest(TenantStatus Status, string Reason);

public sealed class SetTenantStatusRequestValidator : AbstractValidator<SetTenantStatusRequest>
{
    public SetTenantStatusRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required; platform actions are audited.").MaximumLength(500);
    }
}

public sealed class PlatformService(
    ITenantsDb db,
    IUnitOfWork unitOfWork,
    IIdentityService identity,
    IPlatformRoles roles,
    ICurrentUser currentUser,
    IAuditTrail audit,
    TimeProvider clock)
{
    public async Task<PlatformRole?> GetRoleAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return null;
        }

        var user = await identity.GetUserAsync(userId, cancellationToken);
        return user is { IsActive: true } ? roles.GetRole(user.Email) : null;
    }

    public async Task<PagedResult<PlatformTenantDto>> ListTenantsAsync(PlatformTenantQuery query, CancellationToken cancellationToken)
    {
        await RequireRoleAsync(cancellationToken, PlatformRole.PlatformAdmin, PlatformRole.SupportAgent);

        var tenants = db.Tenants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            tenants = tenants.Where(t => t.Name.ToLower().Contains(term) || t.Slug.Contains(term));
        }

        if (query.Status is { } status)
        {
            tenants = tenants.Where(t => t.Status == status);
        }

        return await tenants.OrderByDescending(t => t.CreatedAt).ToPagedResultAsync(
            new PageRequest(query.Page, query.PageSize),
            q => q.Select(t => new PlatformTenantDto(
                t.Id,
                t.Name,
                t.Slug,
                t.Status,
                t.DefaultCurrency,
                db.TenantMemberships.Count(m => m.TenantId == t.Id && m.Status == MembershipStatus.Active),
                t.CreatedAt,
                t.UpdatedAt)),
            cancellationToken);
    }

    /// <summary>Suspending a tenant blocks all of its members on their next request (membership checks are live).</summary>
    public async Task<PlatformTenantDto> SetTenantStatusAsync(Guid tenantId, SetTenantStatusRequest request, CancellationToken cancellationToken)
    {
        await RequireRoleAsync(cancellationToken, PlatformRole.PlatformAdmin);

        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken) ?? throw new NotFoundException("Tenant");
        var previous = tenant.Status;
        if (request.Status == TenantStatus.Suspended)
        {
            tenant.Suspend(clock.GetUtcNow());
        }
        else
        {
            tenant.Activate(clock.GetUtcNow());
        }

        audit.Record(
            "platform.tenant_status_changed",
            "tenant",
            tenant.Id.ToString(),
            new { from = previous.ToString(), to = tenant.Status.ToString(), reason = request.Reason.Trim() },
            tenant.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var members = await db.TenantMemberships.CountAsync(m => m.TenantId == tenant.Id && m.Status == MembershipStatus.Active, cancellationToken);
        return new PlatformTenantDto(tenant.Id, tenant.Name, tenant.Slug, tenant.Status, tenant.DefaultCurrency, members, tenant.CreatedAt, tenant.UpdatedAt);
    }

    private async Task RequireRoleAsync(CancellationToken cancellationToken, params PlatformRole[] allowed)
    {
        var role = await GetRoleAsync(cancellationToken);
        if (role is null || !allowed.Contains(role.Value))
        {
            throw new ForbiddenException("platform_access_denied", "This operation requires a platform role.");
        }
    }
}
