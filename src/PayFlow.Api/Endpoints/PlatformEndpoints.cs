using PayFlow.Api.Extensions;
using PayFlow.Application.Platform;

namespace PayFlow.Api.Endpoints;

/// <summary>Platform staff endpoints. The platform role is resolved live on every call (see PlatformService).</summary>
internal static class PlatformEndpoints
{
    public static RouteGroupBuilder MapPlatformEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/platform").WithTags("Platform administration").RequireAuthorization();

        group.MapGet("/tenants", async ([AsParameters] PlatformTenantQuery query, PlatformService platform, CancellationToken ct) =>
                TypedResults.Ok(await platform.ListTenantsAsync(query, ct)))
            .WithSummary("All tenants with status and active member count (PlatformAdmin, SupportAgent)");

        group.MapPatch("/tenants/{tenantId:guid}/status", async (Guid tenantId, SetTenantStatusRequest request, PlatformService platform, CancellationToken ct) =>
                TypedResults.Ok(await platform.SetTenantStatusAsync(tenantId, request, ct)))
            .Validate<SetTenantStatusRequest>()
            .WithSummary("Suspend or reactivate a tenant with an audited reason (PlatformAdmin)");

        return api;
    }
}
