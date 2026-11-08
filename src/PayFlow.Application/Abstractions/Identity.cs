namespace PayFlow.Application.Abstractions;

public sealed record UserInfo(Guid Id, string Email, string EmailNormalized, string DisplayName, bool IsActive);

public sealed record RegisterUserResult(bool Succeeded, UserInfo? User, IReadOnlyDictionary<string, string[]> Errors);

public enum CredentialCheckStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
    Disabled,
}

public sealed record CredentialCheckResult(CredentialCheckStatus Status, UserInfo? User);

/// <summary>User store and password checks (ASP.NET Core Identity in Infrastructure).</summary>
public interface IIdentityService
{
    string NormalizeEmail(string email);

    Task<RegisterUserResult> RegisterAsync(string email, string password, string displayName, CancellationToken cancellationToken);

    /// <summary>Verifies the password and applies lockout on repeated failures.</summary>
    Task<CredentialCheckResult> CheckCredentialsAsync(string email, string password, CancellationToken cancellationToken);

    Task<UserInfo?> GetUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserInfo?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, UserInfo>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Issues short-lived JWT access tokens. The active tenant is embedded but re-validated on every request.</summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(UserInfo user, Guid? tenantId);
}

public sealed record IssuedRefreshToken(string RawToken, DateTimeOffset ExpiresAt);

public enum RefreshRotationStatus
{
    Rotated,
    Invalid,
    Expired,

    /// <summary>A revoked token was presented again: the whole token family is revoked.</summary>
    ReuseDetected,
}

public sealed record RefreshRotationResult(RefreshRotationStatus Status, Guid? UserId = null, IssuedRefreshToken? NewToken = null);

/// <summary>Refresh tokens are stored only as SHA-256 hashes and rotated on every use.</summary>
public interface IRefreshTokenStore
{
    Task<IssuedRefreshToken> IssueAsync(Guid userId, CancellationToken cancellationToken);

    Task<RefreshRotationResult> RotateAsync(string rawToken, CancellationToken cancellationToken);

    Task RevokeFamilyAsync(string rawToken, string reason, CancellationToken cancellationToken);

    Task RevokeAllForUserAsync(Guid userId, string reason, CancellationToken cancellationToken);
}
