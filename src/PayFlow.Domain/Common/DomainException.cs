namespace PayFlow.Domain.Common;

public enum DomainErrorKind
{
    /// <summary>The input violates a rule (maps to HTTP 400).</summary>
    Validation,

    /// <summary>The operation conflicts with the current state of the resource (maps to HTTP 409).</summary>
    Conflict,

    /// <summary>A domain-level authorization rule forbids the operation (maps to HTTP 403).</summary>
    Forbidden,
}

/// <summary>
/// Raised when a domain invariant or state-transition rule is violated.
/// The <see cref="Code"/> is a stable, machine-readable identifier exposed to API clients.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string code, string message, DomainErrorKind kind = DomainErrorKind.Validation)
        : base(message)
    {
        Code = code;
        Kind = kind;
    }

    public DomainException()
        : this("domain_error", "A domain rule was violated.")
    {
    }

    public DomainException(string message)
        : this("domain_error", message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "domain_error";
    }

    public string Code { get; } = "domain_error";

    public DomainErrorKind Kind { get; }

    public static DomainException InvalidTransition(string resource, string from, string to) =>
        new($"{resource}_invalid_transition", $"{resource} cannot move from '{from}' to '{to}'.", DomainErrorKind.Conflict);
}

internal static class Guard
{
    public static string NotEmpty(string? value, string code, string message, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException(code, message);
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException(code, $"{message} (max {maxLength} characters).");
        }

        return trimmed;
    }

    public static string? Optional(string? value, string code, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException(code, $"Value exceeds {maxLength} characters.");
        }

        return trimmed;
    }

    public static Guid NotEmpty(Guid value, string code)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException(code, "Identifier must not be empty.");
        }

        return value;
    }
}
