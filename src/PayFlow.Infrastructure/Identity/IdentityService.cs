using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Infrastructure.Identity;

internal sealed class IdentityService(UserManager<AppUser> users, ILookupNormalizer normalizer, TimeProvider clock) : IIdentityService
{
    public string NormalizeEmail(string email) => normalizer.NormalizeEmail(email.Trim());

    public async Task<RegisterUserResult> RegisterAsync(string email, string password, string displayName, CancellationToken cancellationToken)
    {
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(clock.GetUtcNow()),
            UserName = email.Trim(),
            Email = email.Trim(),
            DisplayName = displayName.Trim(),
            CreatedAt = clock.GetUtcNow(),
            LockoutEnabled = true,
        };

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            var errors = result.Errors
                .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email")
                .ToDictionary(g => g.Key, g => g.Select(e => e.Code is "DuplicateUserName" or "DuplicateEmail" ? "This email cannot be used." : e.Description).Distinct().ToArray());
            return new RegisterUserResult(false, null, errors);
        }

        return new RegisterUserResult(true, ToInfo(user), new Dictionary<string, string[]>());
    }

    public async Task<CredentialCheckResult> CheckCredentialsAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            // Spend comparable time to avoid user enumeration through response timing.
            users.PasswordHasher.HashPassword(new AppUser { DisplayName = string.Empty }, password);
            return new CredentialCheckResult(CredentialCheckStatus.InvalidCredentials, null);
        }

        if (!user.IsActive)
        {
            return new CredentialCheckResult(CredentialCheckStatus.Disabled, ToInfo(user));
        }

        if (await users.IsLockedOutAsync(user))
        {
            return new CredentialCheckResult(CredentialCheckStatus.LockedOut, ToInfo(user));
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            var locked = await users.IsLockedOutAsync(user);
            return new CredentialCheckResult(locked ? CredentialCheckStatus.LockedOut : CredentialCheckStatus.InvalidCredentials, ToInfo(user));
        }

        await users.ResetAccessFailedCountAsync(user);
        return new CredentialCheckResult(CredentialCheckStatus.Success, ToInfo(user));
    }

    public async Task<UserInfo?> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : ToInfo(user);
    }

    public async Task<UserInfo?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email);
        var user = await users.Users.AsNoTracking().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        return user is null ? null : ToInfo(user);
    }

    public async Task<IReadOnlyDictionary<Guid, UserInfo>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, UserInfo>();
        }

        var list = await users.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToListAsync(cancellationToken);
        return list.ToDictionary(u => u.Id, ToInfo);
    }

    private static UserInfo ToInfo(AppUser user) =>
        new(user.Id, user.Email ?? string.Empty, user.NormalizedEmail ?? string.Empty, user.DisplayName, user.IsActive);
}

public static class PayFlowClaims
{
    public const string TenantId = "tenant_id";
}

public sealed class JwtAccessTokenIssuer(IOptions<AuthOptions> options, TimeProvider clock) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(UserInfo user, Guid? tenantId)
    {
        var jwt = options.Value.Jwt;
        var now = clock.GetUtcNow();
        var expires = now.Add(jwt.AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (tenantId is { } tid)
        {
            claims.Add(new Claim(PayFlowClaims.TenantId, tid.ToString()));
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(CreateSigningKey(jwt.SigningKey), SecurityAlgorithms.HmacSha256),
        });

        return new AccessToken(token, expires);
    }

    public static SymmetricSecurityKey CreateSigningKey(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Auth:Jwt:SigningKey must be at least 32 bytes long.");
        }

        return new SymmetricSecurityKey(bytes);
    }
}

internal sealed class RefreshTokenStore(PayFlowDbContext db, IOptions<AuthOptions> options, TimeProvider clock) : IRefreshTokenStore
{
    public async Task<IssuedRefreshToken> IssueAsync(Guid userId, CancellationToken cancellationToken)
    {
        var (entity, raw) = Create(userId, Guid.CreateVersion7(clock.GetUtcNow()));
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new IssuedRefreshToken(raw, entity.ExpiresAt);
    }

    public Task<RefreshRotationResult> RotateAsync(string rawToken, CancellationToken cancellationToken) =>
        db.ExecuteInTransactionAsync(
            async ct =>
            {
                var hash = Hash(rawToken);
                var current = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
                if (current is null)
                {
                    return new RefreshRotationResult(RefreshRotationStatus.Invalid);
                }

                var now = clock.GetUtcNow();
                if (current.RevokedAt is not null)
                {
                    // A revoked token was replayed: assume theft and revoke the whole family.
                    await RevokeFamilyByIdAsync(current.FamilyId, "reuse_detected", ct);
                    return new RefreshRotationResult(RefreshRotationStatus.ReuseDetected, current.UserId);
                }

                if (current.ExpiresAt <= now)
                {
                    return new RefreshRotationResult(RefreshRotationStatus.Expired, current.UserId);
                }

                var (next, raw) = Create(current.UserId, current.FamilyId);
                db.RefreshTokens.Add(next);
                await db.SaveChangesAsync(ct);

                var updated = await db.RefreshTokens
                    .Where(t => t.Id == current.Id && t.RevokedAt == null)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.RevokedAt, now)
                            .SetProperty(t => t.RevokedReason, "rotated")
                            .SetProperty(t => t.ReplacedById, next.Id),
                        ct);
                if (updated == 0)
                {
                    // Lost a race with a concurrent refresh using the same token.
                    throw new ConflictException("refresh_concurrent", "The session was refreshed concurrently.");
                }

                return new RefreshRotationResult(RefreshRotationStatus.Rotated, current.UserId, new IssuedRefreshToken(raw, next.ExpiresAt));
            },
            cancellationToken);

    public async Task RevokeFamilyAsync(string rawToken, string reason, CancellationToken cancellationToken)
    {
        var hash = Hash(rawToken);
        var familyId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).SingleOrDefaultAsync(cancellationToken);
        if (familyId is { } id)
        {
            await RevokeFamilyByIdAsync(id, reason, cancellationToken);
        }
    }

    public Task RevokeAllForUserAsync(Guid userId, string reason, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.GetUtcNow()).SetProperty(t => t.RevokedReason, reason), cancellationToken);

    internal static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private Task RevokeFamilyByIdAsync(Guid familyId, string reason, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.GetUtcNow()).SetProperty(t => t.RevokedReason, reason), cancellationToken);

    private (RefreshToken Entity, string Raw) Create(Guid userId, Guid familyId)
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        var now = clock.GetUtcNow();
        return (new RefreshToken
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            TokenHash = Hash(raw),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = now.Add(options.Value.RefreshToken.Lifetime),
        }, raw);
    }
}
