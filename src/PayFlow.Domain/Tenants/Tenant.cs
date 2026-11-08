using System.Text.RegularExpressions;
using PayFlow.Domain.Common;

namespace PayFlow.Domain.Tenants;

public enum TenantStatus
{
    Active,
    Suspended,
}

/// <summary>A company using the platform; the primary data-isolation boundary.</summary>
public sealed partial class Tenant : AggregateRoot
{
    private Tenant()
    {
    }

    public string Name { get; private set; } = default!;

    public string Slug { get; private set; } = default!;

    public TenantStatus Status { get; private set; }

    /// <summary>Default currency for new products. Orders keep their own currency snapshot.</summary>
    public string DefaultCurrency { get; private set; } = default!;

    /// <summary>IANA time zone used for report bucketing and display (data is stored in UTC).</summary>
    public string TimeZone { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Tenant Create(string name, string slug, string defaultCurrency, string timeZone, DateTimeOffset now)
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(now),
            Name = Guard.NotEmpty(name, "tenant_name_required", "Tenant name is required", 120),
            Slug = NormalizeSlug(slug),
            Status = TenantStatus.Active,
            DefaultCurrency = CurrencyCatalog.Normalize(defaultCurrency),
            TimeZone = Guard.NotEmpty(timeZone, "tenant_timezone_required", "Time zone is required", 64),
            CreatedAt = now,
            UpdatedAt = now,
        };
        return tenant;
    }

    public void UpdateSettings(string name, string defaultCurrency, string timeZone, DateTimeOffset now)
    {
        Name = Guard.NotEmpty(name, "tenant_name_required", "Tenant name is required", 120);
        DefaultCurrency = CurrencyCatalog.Normalize(defaultCurrency);
        TimeZone = Guard.NotEmpty(timeZone, "tenant_timezone_required", "Time zone is required", 64);
        UpdatedAt = now;
    }

    public void Suspend(DateTimeOffset now)
    {
        Status = TenantStatus.Suspended;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        Status = TenantStatus.Active;
        UpdatedAt = now;
    }

    public static string NormalizeSlug(string? slug)
    {
        var normalized = slug?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!SlugPattern().IsMatch(normalized))
        {
            throw new DomainException(
                "tenant_slug_invalid",
                "Slug must be 3-50 characters of lowercase letters, digits or hyphens and must start and end with a letter or digit.");
        }

        return normalized;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
