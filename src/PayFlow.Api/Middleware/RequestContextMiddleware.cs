using System.Diagnostics;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.JsonWebTokens;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Tenants;
using PayFlow.Infrastructure.Identity;

namespace PayFlow.Api.Middleware;

/// <summary>Accepts a safe inbound X-Correlation-Id or generates one; echoes it and adds it to the log scope.</summary>
internal sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, CorrelationContext correlation)
    {
        var inbound = context.Request.Headers[HeaderName].ToString();
        correlation.CorrelationId = !string.IsNullOrEmpty(inbound) && SafeId().IsMatch(inbound)
            ? inbound
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        context.Response.Headers[HeaderName] = correlation.CorrelationId;
        Activity.Current?.SetTag("payflow.correlation_id", correlation.CorrelationId);
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlation.CorrelationId }))
        {
            await next(context);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex SafeId();
}

/// <summary>
/// Resolves the active tenant from the token's tenant claim AND a live check of the membership in the database.
/// A disabled/removed membership or suspended tenant loses access on the very next request.
/// </summary>
internal sealed class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public const string AccessRevokedItem = "payflow.tenant_access_revoked";

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext, TenantAccessResolver resolver)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true
            && Guid.TryParse(user.FindFirstValue(PayFlowClaims.TenantId), out var tenantId)
            && Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
        {
            var access = await resolver.ResolveAsync(userId, tenantId, context.RequestAborted);
            if (access is not null)
            {
                tenantContext.SetMember(tenantId, access.MembershipId, access.Role);
                Activity.Current?.SetTag("payflow.tenant_id", tenantId.ToString());
            }
            else
            {
                context.Items[AccessRevokedItem] = true;
            }
        }

        using (logger.BeginScope(new Dictionary<string, object?> { ["TenantId"] = tenantContext.TenantId }))
        {
            await next(context);
        }
    }
}

internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        if (!context.Request.Path.StartsWithSegments("/scalar"))
        {
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        }

        return next(context);
    }
}

/// <summary>Request bodies of idempotent commands must be re-readable to compute the request hash.</summary>
internal sealed class IdempotencyBufferingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey(Idempotency.IdempotencyFilter.HeaderName))
        {
            context.Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: 1024 * 1024);
        }

        return next(context);
    }
}

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : null;

    public string? Email => accessor.HttpContext?.User.FindFirstValue(JwtRegisteredClaimNames.Email);

    public bool IsAuthenticated => accessor.HttpContext?.User.Identity?.IsAuthenticated == true;
}
