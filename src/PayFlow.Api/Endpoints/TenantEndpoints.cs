using PayFlow.Api.Authorization;
using PayFlow.Api.Extensions;
using PayFlow.Application.Audit;
using PayFlow.Application.Authorization;
using PayFlow.Application.Notifications;
using PayFlow.Application.Tenants;

namespace PayFlow.Api.Endpoints;

internal static class TenantEndpoints
{
    public static RouteGroupBuilder MapTenantEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/tenants").WithTags("Tenants & members");

        group.MapPost("/", async (CreateTenantRequest request, TenantService tenants, CancellationToken ct) =>
            {
                var tenant = await tenants.CreateAsync(request, ct);
                return TypedResults.Created($"/api/v1/tenants/{tenant.Id}", tenant);
            })
            .Validate<CreateTenantRequest>()
            .RequireAuthorization()
            .WithSummary("Create a tenant; the caller becomes its Owner");

        group.MapGet("/current", async (TenantService tenants, CancellationToken ct) => TypedResults.Ok(await tenants.GetCurrentAsync(ct)))
            .RequireTenant()
            .WithSummary("Active tenant details");

        group.MapPatch("/current", async (UpdateTenantRequest request, TenantService tenants, CancellationToken ct) =>
                TypedResults.Ok(await tenants.UpdateCurrentAsync(request, ct)))
            .Validate<UpdateTenantRequest>()
            .RequirePermission(Permissions.TenantManage)
            .WithSummary("Update tenant settings");

        group.MapGet("/current/members", async (TenantService tenants, CancellationToken ct) => TypedResults.Ok(await tenants.ListMembersAsync(ct)))
            .RequirePermission(Permissions.MembersManage)
            .WithSummary("List members");

        group.MapGet("/current/invitations", async (TenantService tenants, CancellationToken ct) => TypedResults.Ok(await tenants.ListInvitationsAsync(ct)))
            .RequirePermission(Permissions.MembersManage)
            .WithSummary("List invitations");

        group.MapPost("/current/invitations", async (InviteMemberRequest request, TenantService tenants, CancellationToken ct) =>
            {
                var invitation = await tenants.InviteAsync(request, ct);
                return TypedResults.Created($"/api/v1/tenants/current/invitations/{invitation.Id}", invitation);
            })
            .Validate<InviteMemberRequest>()
            .RequirePermission(Permissions.MembersManage)
            .WithSummary("Invite a member by email");

        group.MapDelete("/current/invitations/{invitationId:guid}", async (Guid invitationId, TenantService tenants, CancellationToken ct) =>
            {
                await tenants.RevokeInvitationAsync(invitationId, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.MembersManage)
            .WithSummary("Revoke a pending invitation");

        group.MapPatch("/current/members/{membershipId:guid}", async (Guid membershipId, UpdateMemberRequest request, TenantService tenants, CancellationToken ct) =>
                TypedResults.Ok(await tenants.UpdateMemberAsync(membershipId, request, ct)))
            .Validate<UpdateMemberRequest>()
            .RequirePermission(Permissions.MembersManage)
            .WithSummary("Change a member's role or status");

        group.MapDelete("/current/members/{membershipId:guid}", async (Guid membershipId, TenantService tenants, CancellationToken ct) =>
            {
                await tenants.RemoveMemberAsync(membershipId, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.MembersManage)
            .WithSummary("Remove a member (the last owner is protected)");

        api.MapGet("/notifications", async (NotificationService notifications, int? limit, CancellationToken ct) =>
                TypedResults.Ok(await notifications.ListRecentAsync(limit ?? 20, ct)))
            .RequireTenant()
            .WithTags("Notifications")
            .WithSummary("Recent payment/refund notifications");

        api.MapGet("/audit-logs", async (AuditQueries audit, int? page, int? pageSize, string? action, CancellationToken ct) =>
                TypedResults.Ok(await audit.ListAsync(new Application.Abstractions.PageRequest(page ?? 1, pageSize ?? 20), action, ct)))
            .RequirePermission(Permissions.AuditRead)
            .WithTags("Audit")
            .WithSummary("Audit trail of the active tenant");

        return api;
    }
}
