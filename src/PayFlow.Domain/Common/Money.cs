using System.Collections.Frozen;

namespace PayFlow.Domain.Common;

/// <summary>
/// ISO 4217 currency metadata. Amounts are always stored in minor units (BIGINT), so the
/// exponent is needed to convert between display values and stored values.
/// </summary>
public static class CurrencyCatalog
{
    private static readonly FrozenDictionary<string, int> MinorUnits = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["TRY"] = 2,
        ["USD"] = 2,
        ["EUR"] = 2,
        ["GBP"] = 2,
        ["CHF"] = 2,
        ["SEK"] = 2,
        ["NOK"] = 2,
        ["DKK"] = 2,
        ["CAD"] = 2,
        ["AUD"] = 2,
        ["JPY"] = 0,
        ["KRW"] = 0,
        ["KWD"] = 3,
        ["BHD"] = 3,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> SupportedCodes => MinorUnits.Keys;

    public static bool IsSupported(string? code) => code is not null && MinorUnits.ContainsKey(code);

    public static int GetMinorUnits(string code) =>
        MinorUnits.TryGetValue(code, out var units)
            ? units
            : throw new DomainException("currency_unsupported", $"Currency '{code}' is not supported.");

    /// <summary>Normalizes and validates a currency code (e.g. "try" becomes "TRY").</summary>
    public static string Normalize(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        if (!IsSupported(normalized))
        {
            throw new DomainException("currency_unsupported", $"Currency '{code}' is not supported.");
        }

        return normalized!;
    }
}

/// <summary>
/// Money in minor units. Never uses floating point. Arithmetic is overflow-checked and refuses to
/// combine different currencies (no implicit FX conversion).
/// </summary>
public readonly record struct Money
{
    public Money(long amountMinor, string currency)
    {
        AmountMinor = amountMinor;
        Currency = CurrencyCatalog.Normalize(currency);
    }

    public long AmountMinor { get; }

    public string Currency { get; }

    public static Money Zero(string currency) => new(0, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(AmountMinor + other.AmountMinor), Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(AmountMinor - other.AmountMinor), Currency);
    }

    public Money Multiply(int quantity) => new(checked(AmountMinor * quantity), Currency);

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new DomainException("currency_mismatch", $"Cannot combine {Currency} and {other.Currency} amounts.");
        }
    }

    public override string ToString() => $"{AmountMinor} {Currency} (minor units)";
}
