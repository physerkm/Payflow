using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PayFlow.Api.Middleware;
using PayFlow.Application.Abstractions;

namespace PayFlow.Api.Authorization;

/// <summary>Requires an active tenant membership and (optionally) a permission granted by the member's role.</summary>
internal sealed record PermissionRequirement(string? Permission) : IAuthorizationRequirement;

internal sealed class PermissionAuthorizationHandler(ITenantContext tenantContext) : AuthorizationHandler<PermissionRequirement>
{
    public const string TenantRequired = "tenant_required";
    public const string PermissionDenied = "permission_denied";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!tenantContext.HasTenant)
        {
            context.Fail(new AuthorizationFailureReason(this, TenantRequired));
        }
        else if (requirement.Permission is null || tenantContext.HasPermission(requirement.Permission))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, PermissionDenied));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Builds policies on demand: "perm:orders.write" or "tenant" (any active membership).</summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    public const string PermissionPrefix = "perm:";
    public const string TenantPolicy = "tenant";

    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName == TenantPolicy)
        {
            return Task.FromResult<AuthorizationPolicy?>(Build(null));
        }

        return policyName.StartsWith(PermissionPrefix, StringComparison.Ordinal)
            ? Task.FromResult<AuthorizationPolicy?>(Build(policyName[PermissionPrefix.Length..]))
            : _fallback.GetPolicyAsync(policyName);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    private static AuthorizationPolicy Build(string? permission) =>
        new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
}

/// <summary>Writes problem details for 401/403 with a machine-readable reason.</summary>
internal sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized, "authentication_required", "A valid access token is required.");
            return;
        }

        if (authorizeResult.Forbidden)
        {
            var reasons = authorizeResult.AuthorizationFailure?.FailureReasons.Select(r => r.Message).ToList() ?? [];
            var (code, detail) = context.Items.ContainsKey(TenantResolutionMiddleware.AccessRevokedItem)
                ? ("tenant_access_revoked", "Your membership in this tenant is no longer active.")
                : reasons.Contains(PermissionAuthorizationHandler.TenantRequired)
                    ? ("tenant_required", "Select an active tenant first (POST /api/v1/auth/activate-tenant).")
                    : ("permission_denied", "Your role does not allow this operation.");
            await WriteAsync(context, StatusCodes.Status403Forbidden, code, detail);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    internal static async Task WriteAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = status == 401 ? "Unauthorized" : "Forbidden",
            Detail = detail,
            Type = $"https://payflow.dev/problems/{code.Replace('_', '-')}",
        };
        problem.Extensions["code"] = code;
        var writer = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await writer.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}

public static class AuthorizationEndpointExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.PermissionPrefix + permission);

    public static TBuilder RequireTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.TenantPolicy);
}
