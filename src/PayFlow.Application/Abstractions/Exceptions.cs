namespace PayFlow.Application.Abstractions;

/// <summary>
/// The resource does not exist <em>in the current tenant</em>. Resources of other tenants also produce
/// this exception, so their existence is never disclosed (HTTP 404).
/// </summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string resource)
        : base($"{resource} was not found.")
    {
        Resource = resource;
    }

    public NotFoundException()
        : this("Resource")
    {
    }

    public NotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        Resource = "Resource";
    }

    public string Resource { get; } = "Resource";
}

/// <summary>Authenticated but not allowed (HTTP 403).</summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public ForbiddenException()
        : this("forbidden", "The operation is not allowed.")
    {
    }

    public ForbiddenException(string message)
        : this("forbidden", message)
    {
    }

    public ForbiddenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string Code { get; } = "forbidden";
}

/// <summary>The request conflicts with the current state (HTTP 409).</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public ConflictException()
        : this("conflict", "The request conflicts with the current state of the resource.")
    {
    }

    public ConflictException(string message)
        : this("conflict", message)
    {
    }

    public ConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string Code { get; } = "conflict";
}

/// <summary>Request validation failed (HTTP 400 with field errors).</summary>
public sealed class RequestValidationException : Exception
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public RequestValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] })
    {
    }

    public RequestValidationException()
        : this(new Dictionary<string, string[]>())
    {
    }

    public RequestValidationException(string message)
        : base(message)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public RequestValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>A unique constraint was violated while saving (translated from the database error by Infrastructure).</summary>
public sealed class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException(string constraintName, Exception innerException)
        : base($"Unique constraint '{constraintName}' was violated.", innerException)
    {
        ConstraintName = constraintName;
    }

    public UniqueConstraintViolationException()
        : this("unknown", new InvalidOperationException())
    {
    }

    public UniqueConstraintViolationException(string message)
        : base(message)
    {
        ConstraintName = "unknown";
    }

    public string ConstraintName { get; } = "unknown";
}

/// <summary>Optimistic concurrency conflict (row_version changed since the entity was read) - HTTP 409.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ConcurrencyConflictException()
        : base("The resource was modified by another request. Reload and try again.")
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>Authentication failed (HTTP 401). Messages are deliberately generic.</summary>
public sealed class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public AuthenticationFailedException()
        : this("authentication_failed", "Authentication failed.")
    {
    }

    public AuthenticationFailedException(string message)
        : this("authentication_failed", message)
    {
    }

    public AuthenticationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string Code { get; } = "authentication_failed";
}
