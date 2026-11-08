namespace PayFlow.Application.Abstractions;

/// <summary>Result of reserving an idempotency key inside the current transaction.</summary>
public abstract record IdempotencyBeginResult
{
    /// <summary>First time this key is seen: execute the operation, then call <see cref="IIdempotencyStore.CompleteAsync"/>.</summary>
    public sealed record Started(Guid RecordId) : IdempotencyBeginResult;

    /// <summary>The same request was already completed: return the stored response.</summary>
    public sealed record Replay(int StatusCode, string? Body, string? Location) : IdempotencyBeginResult;

    /// <summary>The key was used with a different request body (HTTP 409).</summary>
    public sealed record Mismatch : IdempotencyBeginResult;
}

/// <summary>
/// Stores idempotency records. <see cref="BeginAsync"/> must be called inside the same transaction as the
/// business operation: a concurrent request with the same key blocks on the unique index until the first
/// transaction commits, then replays its stored response.
/// </summary>
public interface IIdempotencyStore
{
    Task<IdempotencyBeginResult> BeginAsync(Guid tenantId, string operation, string key, string requestHash, CancellationToken cancellationToken);

    Task CompleteAsync(Guid recordId, int statusCode, string? responseBody, string? location, CancellationToken cancellationToken);
}
