using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Authorization;
using PayFlow.Domain.Tenants;

namespace PayFlow.Application.Tenants;

public sealed record TenantAccess(
    Guid TenantId,
    Guid MembershipId,
    TenantRole Role,
    string TenantName,
    string TenantSlug,
    string DefaultCurrency,
    string TimeZone);

public sealed record ActiveTenantDto(
    Guid TenantId,
    string Name,
    string Slug,
    TenantRole Role,
    IReadOnlyCollection<string> Permissions,
    string DefaultCurrency,
    string TimeZone)
{
    public static ActiveTenantDto From(TenantAccess access) => new(
        access.TenantId,
        access.TenantName,
        access.TenantSlug,
        access.Role,
        RolePermissions.For(access.Role).Order(StringComparer.Ordinal).ToList(),
        access.DefaultCurrency,
        access.TimeZone);
}

/// <summary>
/// Resolves whether a user may act in a tenant right now. Called on every tenant-scoped request so that a
/// revoked or changed membership takes effect immediately, without waiting for the access token to expire.
/// </summary>
public sealed class TenantAccessResolver(ITenantsDb db)
{
    public async Task<TenantAccess?> ResolveAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        var row = await (
                from m in db.TenantMemberships.AsNoTracking()
                join t in db.Tenants.AsNoTracking() on m.TenantId equals t.Id
                where m.UserId == userId && m.TenantId == tenantId
                select new
                {
                    m.Id,
                    m.Role,
                    m.Status,
                    TenantStatus = t.Status,
                    t.Name,
                    t.Slug,
                    t.DefaultCurrency,
                    t.TimeZone,
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null || row.Status != MembershipStatus.Active || row.TenantStatus != TenantStatus.Active)
        {
            return null;
        }

        return new TenantAccess(tenantId, row.Id, row.Role, row.Name, row.Slug, row.DefaultCurrency, row.TimeZone);
    }
}
