using PayFlow.Api.Authorization;
using PayFlow.Api.Extensions;
using PayFlow.Api.Idempotency;
using PayFlow.Application.Authorization;
using PayFlow.Application.Customers;
using PayFlow.Application.Products;

namespace PayFlow.Api.Endpoints;

internal static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCustomerEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/customers").WithTags("Customers");

        group.MapPost("/", async (CreateCustomerRequest request, CustomerService customers, CancellationToken ct) =>
            {
                var customer = await customers.CreateAsync(request, ct);
                return TypedResults.Created($"/api/v1/customers/{customer.Id}", customer);
            })
            .Validate<CreateCustomerRequest>()
            .WithIdempotency()
            .RequirePermission(Permissions.CustomersWrite)
            .WithSummary("Create a customer");

        group.MapGet("/", async ([AsParameters] CustomerListQuery query, CustomerService customers, CancellationToken ct) =>
                TypedResults.Ok(await customers.ListAsync(query, ct)))
            .RequirePermission(Permissions.CustomersRead)
            .WithSummary("Paged, filtered customer list (sort: name, createdAt, email)");

        group.MapGet("/{id:guid}", async (Guid id, CustomerService customers, CancellationToken ct) => TypedResults.Ok(await customers.GetAsync(id, ct)))
            .RequirePermission(Permissions.CustomersRead)
            .WithSummary("Customer details");

        group.MapPut("/{id:guid}", async (Guid id, UpdateCustomerRequest request, CustomerService customers, CancellationToken ct) =>
                TypedResults.Ok(await customers.UpdateAsync(id, request, ct)))
            .Validate<UpdateCustomerRequest>()
            .RequirePermission(Permissions.CustomersWrite)
            .WithSummary("Update a customer");

        group.MapDelete("/{id:guid}", async (Guid id, CustomerService customers, CancellationToken ct) =>
            {
                await customers.DeactivateAsync(id, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.CustomersWrite)
            .WithSummary("Deactivate a customer (financial history is preserved)");

        return api;
    }

    public static RouteGroupBuilder MapProductEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/products").WithTags("Products");

        group.MapPost("/", async (CreateProductRequest request, ProductService products, CancellationToken ct) =>
            {
                var product = await products.CreateAsync(request, ct);
                return TypedResults.Created($"/api/v1/products/{product.Id}", product);
            })
            .Validate<CreateProductRequest>()
            .WithIdempotency()
            .RequirePermission(Permissions.ProductsWrite)
            .WithSummary("Create a product (SKU unique per tenant, price in minor units)");

        group.MapGet("/", async ([AsParameters] ProductListQuery query, ProductService products, CancellationToken ct) =>
                TypedResults.Ok(await products.ListAsync(query, ct)))
            .RequirePermission(Permissions.ProductsRead)
            .WithSummary("Paged, filtered product list (sort: name, sku, price, createdAt)");

        group.MapGet("/{id:guid}", async (Guid id, ProductService products, CancellationToken ct) => TypedResults.Ok(await products.GetAsync(id, ct)))
            .RequirePermission(Permissions.ProductsRead)
            .WithSummary("Product details");

        group.MapPut("/{id:guid}", async (Guid id, UpdateProductRequest request, ProductService products, CancellationToken ct) =>
                TypedResults.Ok(await products.UpdateAsync(id, request, ct)))
            .Validate<UpdateProductRequest>()
            .RequirePermission(Permissions.ProductsWrite)
            .WithSummary("Update a product (existing orders keep their price snapshot)");

        group.MapPatch("/{id:guid}/status", async (Guid id, SetProductStatusRequest request, ProductService products, CancellationToken ct) =>
                TypedResults.Ok(await products.SetStatusAsync(id, request, ct)))
            .Validate<SetProductStatusRequest>()
            .RequirePermission(Permissions.ProductsWrite)
            .WithSummary("Activate or deactivate a product");

        return api;
    }
}
