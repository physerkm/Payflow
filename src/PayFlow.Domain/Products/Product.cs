using System.Text.RegularExpressions;
using PayFlow.Domain.Common;

namespace PayFlow.Domain.Products;

public enum ProductStatus
{
    Active,
    Inactive,
}

/// <summary>
/// A product or service sold by a tenant. Changing the price never affects existing orders,
/// because order items keep their own price snapshot.
/// </summary>
public sealed partial class Product : AggregateRoot, ITenantScoped
{
    public const long MaxPriceMinor = 1_000_000_000_00; // 1 billion major units

    private Product()
    {
    }

    public Guid TenantId { get; private set; }

    public string Sku { get; private set; } = default!;

    public string Name { get; private set; } = default!;

    public string? Description { get; private set; }

    public long PriceMinor { get; private set; }

    public string Currency { get; private set; } = default!;

    public ProductStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Money Price => new(PriceMinor, Currency);

    public static Product Create(Guid tenantId, string sku, string name, string? description, long priceMinor, string currency, DateTimeOffset now)
    {
        var product = new Product
        {
            Id = Guid.CreateVersion7(now),
            TenantId = Guard.NotEmpty(tenantId, "tenant_required"),
            Sku = NormalizeSku(sku),
            Status = ProductStatus.Active,
            CreatedAt = now,
        };
        product.Update(name, description, priceMinor, currency, now);
        return product;
    }

    public void Update(string name, string? description, long priceMinor, string currency, DateTimeOffset now)
    {
        if (priceMinor <= 0 || priceMinor > MaxPriceMinor)
        {
            throw new DomainException("product_price_invalid", "Price must be a positive amount in minor units.");
        }

        Name = Guard.NotEmpty(name, "product_name_required", "Product name is required", 200);
        Description = Guard.Optional(description, "product_description_invalid", 2000);
        PriceMinor = priceMinor;
        Currency = CurrencyCatalog.Normalize(currency);
        UpdatedAt = now;
    }

    public void SetStatus(ProductStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }

    public static string NormalizeSku(string? sku)
    {
        var normalized = sku?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!SkuPattern().IsMatch(normalized))
        {
            throw new DomainException("product_sku_invalid", "SKU must be 2-64 characters of letters, digits, '-', '_' or '.'.");
        }

        return normalized;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkuPattern();
}
