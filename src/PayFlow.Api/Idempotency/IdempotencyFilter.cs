using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PayFlow.Application.Abstractions;

namespace PayFlow.Api.Idempotency;

/// <summary>
/// Implements the Idempotency-Key contract for command endpoints:
/// <list type="bullet">
/// <item>same key + same request → the stored response is replayed (no second business effect);</item>
/// <item>same key + different request → 409 Conflict;</item>
/// <item>concurrent duplicates → the second request waits on the unique index, then replays.</item>
/// </list>
/// The key reservation, the business change and the stored response commit in ONE transaction, so a crash can
/// never leave a "completed" key without its effect (or the reverse).
/// </summary>
internal sealed class IdempotencyFilter(bool required) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";
    private const int MaxKeyLength = 128;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            if (required)
            {
                throw new RequestValidationException(HeaderName, "The Idempotency-Key header is required for this operation.");
            }

            return await next(context);
        }

        if (key.Length > MaxKeyLength || key.Any(char.IsControl))
        {
            throw new RequestValidationException(HeaderName, $"Idempotency-Key must be 1-{MaxKeyLength} printable characters.");
        }

        var services = http.RequestServices;
        var tenantId = services.GetRequiredService<ITenantContext>().RequireTenantId();
        var store = services.GetRequiredService<IIdempotencyStore>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var jsonOptions = services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        var operation = $"{http.Request.Method} {(http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value}";
        var requestHash = await ComputeRequestHashAsync(http);

        return await unitOfWork.ExecuteInTransactionAsync<object?>(
            async ct =>
            {
                var begin = await store.BeginAsync(tenantId, operation, key, requestHash, ct);
                switch (begin)
                {
                    case IdempotencyBeginResult.Replay replay:
                        http.Response.Headers[ReplayedHeader] = "true";
                        if (replay.Location is not null)
                        {
                            http.Response.Headers.Location = replay.Location;
                        }

                        return Results.Content(replay.Body ?? string.Empty, "application/json", Encoding.UTF8, replay.StatusCode);

                    case IdempotencyBeginResult.Mismatch:
                        throw new ConflictException("idempotency_key_reused", "This Idempotency-Key was already used with a different request.");

                    case IdempotencyBeginResult.Started started:
                        var result = await next(context);
                        if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 } success)
                        {
                            var body = result is IValueHttpResult { Value: { } value } ? JsonSerializer.Serialize(value, value.GetType(), jsonOptions) : null;
                            var location = result.GetType().GetProperty("Location")?.GetValue(result) as string;
                            await store.CompleteAsync(started.RecordId, success.StatusCode!.Value, body, location, ct);
                        }

                        return result;

                    default:
                        throw new InvalidOperationException("Unexpected idempotency state.");
                }
            },
            http.RequestAborted);
    }

    private static async Task<string> ComputeRequestHashAsync(HttpContext http)
    {
        var request = http.Request;
        string body = string.Empty;
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
            using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
            body = await reader.ReadToEndAsync(http.RequestAborted);
            request.Body.Position = 0;
        }

        var canonical = $"{request.Method}\n{request.Path}\n{body}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public static class IdempotencyEndpointExtensions
{
    /// <summary>Honors the Idempotency-Key header; when <paramref name="required"/> is true the header is mandatory.</summary>
    public static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder, bool required = false) =>
        builder.AddEndpointFilter(new IdempotencyFilter(required))
            .WithMetadata(new IdempotencyMetadata(required));
}

public sealed record IdempotencyMetadata(bool Required);
