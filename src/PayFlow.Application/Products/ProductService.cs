using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;
using PayFlow.Domain.Products;

namespace PayFlow.Application.Products;

public sealed record CreateProductRequest(string Sku, string Name, string? Description, long PriceMinor, string Currency);

public sealed record UpdateProductRequest(string Name, string? Description, long PriceMinor, string Currency);

public sealed record SetProductStatusRequest(ProductStatus Status);

public sealed record ProductDto(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    long PriceMinor,
    string Currency,
    ProductStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductListQuery(string? Search, ProductStatus? Status, string? Currency, string? Sort, int Page = 1, int PageSize = 20);

public sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().Matches("^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$")
            .WithMessage("SKU must be 2-64 characters of letters, digits, '-', '_' or '.'.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.PriceMinor).GreaterThan(0).LessThanOrEqualTo(Product.MaxPriceMinor);
        RuleFor(x => x.Currency).Must(c => CurrencyCatalog.IsSupported(c?.ToUpperInvariant())).WithMessage("Unsupported currency.");
    }
}

public sealed class UpdateProductRequestValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.PriceMinor).GreaterThan(0).LessThanOrEqualTo(Product.MaxPriceMinor);
        RuleFor(x => x.Currency).Must(c => CurrencyCatalog.IsSupported(c?.ToUpperInvariant())).WithMessage("Unsupported currency.");
    }
}

public sealed class SetProductStatusRequestValidator : AbstractValidator<SetProductStatusRequest>
{
    public SetProductStatusRequestValidator() => RuleFor(x => x.Status).IsInEnum();
}

public sealed class ProductService(ICatalogDb db, IUnitOfWork unitOfWork, ITenantContext tenantContext, TimeProvider clock)
{
    private static readonly string[] SortFields = ["name", "sku", "price", "createdAt"];

    public async Task<ProductDto> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var sku = Product.NormalizeSku(request.Sku);
        if (await db.Products.AnyAsync(p => p.Sku == sku, cancellationToken))
        {
            throw new ConflictException("product_sku_taken", $"A product with SKU '{sku}' already exists.");
        }

        var product = Product.Create(tenantContext.RequireTenantId(), sku, request.Name, request.Description, request.PriceMinor, request.Currency, clock.GetUtcNow());
        db.Products.Add(product);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(product);
    }

    public async Task<ProductDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToDto(await LoadAsync(id, cancellationToken, tracking: false));

    public async Task<PagedResult<ProductDto>> ListAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        var products = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            products = products.Where(p => p.Name.ToLower().Contains(term) || p.Sku.ToLower().Contains(term));
        }

        if (query.Status is { } status)
        {
            products = products.Where(p => p.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Currency))
        {
            var currency = query.Currency.Trim().ToUpperInvariant();
            products = products.Where(p => p.Currency == currency);
        }

        var sort = SortSpec.Parse(query.Sort, SortFields, new SortSpec("createdAt", true));
        products = sort.Field switch
        {
            "name" => products.OrderBy(p => p.Name, sort.Descending),
            "sku" => products.OrderBy(p => p.Sku, sort.Descending),
            "price" => products.OrderBy(p => p.PriceMinor, sort.Descending),
            _ => products.OrderBy(p => p.CreatedAt, sort.Descending),
        };

        return await products.ToPagedResultAsync(
            new PageRequest(query.Page, query.PageSize),
            q => q.Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.Description, p.PriceMinor, p.Currency, p.Status, p.CreatedAt, p.UpdatedAt)),
            cancellationToken);
    }

    public async Task<ProductDto> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, cancellationToken);
        product.Update(request.Name, request.Description, request.PriceMinor, request.Currency, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(product);
    }

    public async Task<ProductDto> SetStatusAsync(Guid id, SetProductStatusRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, cancellationToken);
        product.SetStatus(request.Status, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(product);
    }

    private async Task<Product> LoadAsync(Guid id, CancellationToken cancellationToken, bool tracking = true)
    {
        var query = tracking ? db.Products : db.Products.AsNoTracking();
        return await query.SingleOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw new NotFoundException("Product");
    }

    private static ProductDto ToDto(Product p) =>
        new(p.Id, p.Sku, p.Name, p.Description, p.PriceMinor, p.Currency, p.Status, p.CreatedAt, p.UpdatedAt);
}

/// <summary>Public API of the Catalog module: current product data used to build order snapshots.</summary>
public interface IProductCatalog
{
    Task<IReadOnlyList<ProductSnapshot>> GetAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}

public sealed record ProductSnapshot(Guid Id, string Sku, string Name, long PriceMinor, string Currency, bool IsActive);

internal sealed class ProductCatalog(ICatalogDb db) : IProductCatalog
{
    public async Task<IReadOnlyList<ProductSnapshot>> GetAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        await db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new ProductSnapshot(p.Id, p.Sku, p.Name, p.PriceMinor, p.Currency, p.Status == ProductStatus.Active))
            .ToListAsync(cancellationToken);
}
