using Microsoft.Extensions.Options;
using PayFlow.Api.Extensions;
using PayFlow.Application.Identity;
using PayFlow.Application.Tenants;
using PayFlow.Infrastructure.Identity;

namespace PayFlow.Api.Endpoints;

/// <summary>
/// Access token in the response body (the SPA keeps it in memory only); refresh token in an HttpOnly, Secure,
/// SameSite=Strict cookie scoped to /api/v1/auth, so JavaScript can never read it (ADR-010).
/// </summary>
public sealed record SessionResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, UserDto User, ActiveTenantDto? Tenant);

internal static class AuthEndpoints
{
    public const string AuthRateLimitPolicy = "auth";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Authentication");

        group.MapPost("/register", async (RegisterRequest request, AuthService auth, HttpContext http, IOptions<AuthOptions> options, CancellationToken ct) =>
            {
                var result = await auth.RegisterAsync(request, ct);
                WriteRefreshCookie(http, options.Value, result);
                return TypedResults.Created("/api/v1/auth/me", ToResponse(result));
            })
            .Validate<RegisterRequest>()
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithSummary("Register a new user");

        group.MapPost("/login", async (LoginRequest request, AuthService auth, HttpContext http, IOptions<AuthOptions> options, CancellationToken ct) =>
            {
                var result = await auth.LoginAsync(request, ct);
                WriteRefreshCookie(http, options.Value, result);
                return TypedResults.Ok(ToResponse(result));
            })
            .Validate<LoginRequest>()
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .WithSummary("Sign in with email and password");

        group.MapPost("/refresh", async (RefreshRequest? request, AuthService auth, HttpContext http, IOptions<AuthOptions> options, CancellationToken ct) =>
            {
                var raw = http.Request.Cookies[options.Value.RefreshToken.CookieName];
                try
                {
                    var result = await auth.RefreshAsync(raw, request?.TenantId, ct);
                    WriteRefreshCookie(http, options.Value, result);
                    return TypedResults.Ok(ToResponse(result));
                }
                catch (Application.Abstractions.AuthenticationFailedException)
                {
                    DeleteRefreshCookie(http, options.Value);
                    throw;
                }
            })
            .AllowAnonymous()
            // Not under the strict login limit: the SPA refreshes on every page load and the token (48 random bytes)
            // cannot be brute-forced. The global per-client limiter still applies.
            .WithSummary("Rotate the refresh token (cookie) and get a new access token");

        group.MapPost("/logout", async (AuthService auth, HttpContext http, IOptions<AuthOptions> options, CancellationToken ct) =>
            {
                await auth.LogoutAsync(http.Request.Cookies[options.Value.RefreshToken.CookieName], ct);
                DeleteRefreshCookie(http, options.Value);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .WithSummary("Revoke the session's refresh token family");

        group.MapGet("/me", async (AuthService auth, CancellationToken ct) => TypedResults.Ok(await auth.GetMeAsync(ct)))
            .RequireAuthorization()
            .WithSummary("Current user and active tenant");

        group.MapGet("/memberships", async (AuthService auth, CancellationToken ct) => TypedResults.Ok(await auth.GetMembershipsAsync(ct)))
            .RequireAuthorization()
            .WithSummary("Tenants the current user belongs to");

        group.MapPost("/activate-tenant", async (ActivateTenantRequest request, AuthService auth, CancellationToken ct) =>
                TypedResults.Ok(ToResponse(await auth.ActivateTenantAsync(request, ct))))
            .RequireAuthorization()
            .WithSummary("Select a tenant; returns an access token bound to the verified membership");

        group.MapGet("/invitations", async (TenantService tenants, CancellationToken ct) => TypedResults.Ok(await tenants.GetMyInvitationsAsync(ct)))
            .RequireAuthorization()
            .WithSummary("Pending invitations for the current user's email");

        group.MapPost("/invitations/{invitationId:guid}/accept", async (Guid invitationId, TenantService tenants, CancellationToken ct) =>
                TypedResults.Ok(await tenants.AcceptInvitationAsync(invitationId, ct)))
            .RequireAuthorization()
            .WithSummary("Accept an invitation and join the tenant");

        return api;
    }

    private static SessionResponse ToResponse(AuthResult result) =>
        new(result.AccessToken.Token, "Bearer", result.AccessToken.ExpiresAt, result.User, result.Tenant);

    private static void WriteRefreshCookie(HttpContext http, AuthOptions options, AuthResult result)
    {
        if (result.RefreshToken is null)
        {
            return;
        }

        http.Response.Cookies.Append(options.RefreshToken.CookieName, result.RefreshToken.RawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RefreshToken.SecureCookie,
            SameSite = SameSiteMode.Strict,
            Path = options.RefreshToken.CookiePath,
            Expires = result.RefreshToken.ExpiresAt,
            IsEssential = true,
        });
    }

    private static void DeleteRefreshCookie(HttpContext http, AuthOptions options) =>
        http.Response.Cookies.Delete(options.RefreshToken.CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RefreshToken.SecureCookie,
            SameSite = SameSiteMode.Strict,
            Path = options.RefreshToken.CookiePath,
        });
}
