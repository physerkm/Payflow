namespace PayFlow.Infrastructure.Identity;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public JwtOptions Jwt { get; set; } = new();

    public RefreshTokenOptions RefreshToken { get; set; } = new();
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "payflow";

    public string Audience { get; set; } = "payflow-api";

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Must come from a secret store / environment in real deployments.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class RefreshTokenOptions
{
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(14);

    public string CookieName { get; set; } = "payflow_refresh";

    /// <summary>Path-scoped so the cookie is only sent to authentication endpoints.</summary>
    public string CookiePath { get; set; } = "/api/v1/auth";

    public bool SecureCookie { get; set; } = true;
}
