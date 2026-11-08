using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PayFlow.Api.Authorization;
using PayFlow.Api.Endpoints;
using PayFlow.Api.Middleware;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure.Identity;

namespace PayFlow.Api.Extensions;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Requests per minute per client IP for login/register/refresh.</summary>
    public int AuthPermitPerMinute { get; set; } = 10;

    /// <summary>Requests per minute per user (or IP when anonymous) for the rest of the API.</summary>
    public int GlobalPermitPerMinute { get; set; } = 600;

    /// <summary>AI assistant questions per minute per user.</summary>
    public int AssistantPermitPerMinute { get; set; } = 10;
}

internal static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddPayFlowProblemDetails();

        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((options, auth) =>
            {
                var jwt = auth.Value.Jwt;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = JwtAccessTokenIssuer.CreateSigningKey(jwt.SigningKey),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Name,
                };
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await ProblemAuthorizationResultHandler.WriteAsync(
                            context.HttpContext,
                            StatusCodes.Status401Unauthorized,
                            "authentication_required",
                            "A valid access token is required.");
                    },
                };
            });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddAuthorization();

        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await ProblemAuthorizationResultHandler.WriteAsync(context.HttpContext, 429, "rate_limited", "Too many requests. Please slow down.");
            };

            options.AddPolicy(AuthEndpoints.AuthRateLimitPolicy, http =>
            {
                var settings = http.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                if (!settings.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    $"auth:{http.Connection.RemoteIpAddress}",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.AuthPermitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });

            options.AddPolicy(AssistantEndpoints.AssistantRateLimitPolicy, http =>
            {
                // Model calls cost money and time: a small per-user budget.
                var settings = http.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                if (!settings.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                var user = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter($"assistant:{user}", _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.AssistantPermitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                var settings = http.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                if (!settings.Enabled || http.Request.Path.StartsWithSegments("/health"))
                {
                    return RateLimitPartition.GetNoLimiter("unlimited");
                }

                var key = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = settings.GlobalPermitPerMinute,
                    TokensPerPeriod = settings.GlobalPermitPerMinute,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });

        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }
        }));

        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "PayFlow API";
            document.Info.Version = "v1";
            document.Info.Description =
                "Multi-tenant payment & order management API. Authenticate with POST /api/v1/auth/login, then select a tenant " +
                "with POST /api/v1/auth/activate-tenant and send the returned token as 'Authorization: Bearer <token>'. " +
                "Payment and refund commands require an Idempotency-Key header. All amounts are integers in minor units.";
            return Task.CompletedTask;
        }));

        services.AddHealthChecks();
        return services;
    }
}
