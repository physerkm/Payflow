using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Application.Tenants;
using PayFlow.Domain.Tenants;

namespace PayFlow.Application.Identity;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(Guid? TenantId);

public sealed record ActivateTenantRequest(Guid TenantId);

public sealed record UserDto(Guid Id, string Email, string DisplayName);

public sealed record MembershipDto(
    Guid MembershipId,
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    TenantRole Role,
    MembershipStatus Status,
    TenantStatus TenantStatus);

public sealed record MeDto(UserDto User, ActiveTenantDto? ActiveTenant, Platform.PlatformRole? PlatformRole);

/// <summary>Outcome of a successful authentication step. <see cref="RefreshToken"/> is set when a new one was issued.</summary>
public sealed record AuthResult(AccessToken AccessToken, IssuedRefreshToken? RefreshToken, UserDto User, ActiveTenantDto? Tenant);

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(10).MaximumLength(128);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class AuthService(
    IIdentityService identity,
    IAccessTokenIssuer accessTokens,
    IRefreshTokenStore refreshTokens,
    ITenantsDb tenantsDb,
    TenantAccessResolver accessResolver,
    IUnitOfWork unitOfWork,
    IAuditTrail audit,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    Platform.IPlatformRoles platformRoles,
    ILogger<AuthService> logger)
{
    private const string InvalidCredentialsMessage = "Invalid email or password.";

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await identity.RegisterAsync(request.Email, request.Password, request.DisplayName, cancellationToken);
        if (!result.Succeeded || result.User is null)
        {
            throw new RequestValidationException(result.Errors);
        }

        audit.Record(AuditActions.UserRegistered, "user", result.User.Id.ToString());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await IssueSessionAsync(result.User, cancellationToken);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var check = await identity.CheckCredentialsAsync(request.Email, request.Password, cancellationToken);
        if (check.Status != CredentialCheckStatus.Success || check.User is null)
        {
            // Same response for unknown user, wrong password, lockout and disabled accounts (no account enumeration).
            audit.Record(AuditActions.LoginFailed, "user", check.User?.Id.ToString(), new { reason = check.Status.ToString() });
            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Login failed with status {Status}", check.Status);
            throw new AuthenticationFailedException("invalid_credentials", InvalidCredentialsMessage);
        }

        audit.Record(AuditActions.LoginSucceeded, "user", check.User.Id.ToString());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await IssueSessionAsync(check.User, cancellationToken);
    }

    public async Task<AuthResult> RefreshAsync(string? rawRefreshToken, Guid? tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            throw new AuthenticationFailedException("refresh_token_missing", "Session expired. Please sign in again.");
        }

        var rotation = await refreshTokens.RotateAsync(rawRefreshToken, cancellationToken);
        switch (rotation.Status)
        {
            case RefreshRotationStatus.Rotated:
                break;
            case RefreshRotationStatus.ReuseDetected:
                audit.Record(AuditActions.RefreshReuseDetected, "user", rotation.UserId?.ToString());
                await unitOfWork.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Refresh token reuse detected for user {UserId}; token family revoked", rotation.UserId);
                throw new AuthenticationFailedException("refresh_token_reused", "Session expired. Please sign in again.");
            default:
                throw new AuthenticationFailedException("refresh_token_invalid", "Session expired. Please sign in again.");
        }

        var user = await identity.GetUserAsync(rotation.UserId!.Value, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw new AuthenticationFailedException("user_inactive", "Session expired. Please sign in again.");
        }

        TenantAccess? access = null;
        if (tenantId is { } requestedTenant)
        {
            access = await accessResolver.ResolveAsync(user.Id, requestedTenant, cancellationToken);
        }

        var token = accessTokens.Issue(user, access?.TenantId);
        return new AuthResult(token, rotation.NewToken, ToDto(user), access is null ? null : ActiveTenantDto.From(access));
    }

    public async Task LogoutAsync(string? rawRefreshToken, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            await refreshTokens.RevokeFamilyAsync(rawRefreshToken, "logout", cancellationToken);
        }

        if (currentUser.UserId is { } userId)
        {
            audit.Record(AuditActions.Logout, "user", userId.ToString());
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<MeDto> GetMeAsync(CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(cancellationToken);
        ActiveTenantDto? active = null;
        if (tenantContext.TenantId is { } tenantId)
        {
            var access = await accessResolver.ResolveAsync(user.Id, tenantId, cancellationToken);
            active = access is null ? null : ActiveTenantDto.From(access);
        }

        return new MeDto(ToDto(user), active, platformRoles.GetRole(user.Email));
    }

    public async Task<IReadOnlyList<MembershipDto>> GetMembershipsAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        return await (
                from m in tenantsDb.TenantMemberships.AsNoTracking()
                join t in tenantsDb.Tenants.AsNoTracking() on m.TenantId equals t.Id
                where m.UserId == userId
                orderby t.Name
                select new MembershipDto(m.Id, t.Id, t.Name, t.Slug, m.Role, m.Status, t.Status))
            .ToListAsync(cancellationToken);
    }

    public async Task<AuthResult> ActivateTenantAsync(ActivateTenantRequest request, CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(cancellationToken);
        var access = await accessResolver.ResolveAsync(user.Id, request.TenantId, cancellationToken)
            ?? throw new ForbiddenException("tenant_access_denied", "You do not have an active membership in this tenant.");

        audit.Record(AuditActions.TenantActivated, "tenant", access.TenantId.ToString(), tenantId: access.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var token = accessTokens.Issue(user, access.TenantId);
        return new AuthResult(token, null, ToDto(user), ActiveTenantDto.From(access));
    }

    private async Task<AuthResult> IssueSessionAsync(UserInfo user, CancellationToken cancellationToken)
    {
        var refresh = await refreshTokens.IssueAsync(user.Id, cancellationToken);
        var access = accessTokens.Issue(user, tenantId: null);
        return new AuthResult(access, refresh, ToDto(user), null);
    }

    private async Task<UserInfo> RequireUserAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var user = await identity.GetUserAsync(userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw new AuthenticationFailedException("user_inactive", "Your account is not active.");
        }

        return user;
    }

    private static UserDto ToDto(UserInfo user) => new(user.Id, user.Email, user.DisplayName);
}
