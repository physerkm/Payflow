using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Customers;

namespace PayFlow.Application.Customers;

public sealed record CreateCustomerRequest(string Name, string? Email, string? Phone);

public sealed record UpdateCustomerRequest(string Name, string? Email, string? Phone);

public sealed record CustomerDto(Guid Id, string Name, string? Email, string? Phone, CustomerStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CustomerListQuery(string? Search, CustomerStatus? Status, string? Sort, int Page = 1, int PageSize = 20);

public sealed class CreateCustomerRequestValidator : AbstractValidator<CreateCustomerRequest>
{
    public CreateCustomerRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).MaximumLength(32).Matches(@"^\+?[0-9 ()-]{5,32}$").When(x => !string.IsNullOrWhiteSpace(x.Phone));
    }
}

public sealed class UpdateCustomerRequestValidator : AbstractValidator<UpdateCustomerRequest>
{
    public UpdateCustomerRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).MaximumLength(32).Matches(@"^\+?[0-9 ()-]{5,32}$").When(x => !string.IsNullOrWhiteSpace(x.Phone));
    }
}

public sealed class CustomerService(ICustomersDb db, IUnitOfWork unitOfWork, ITenantContext tenantContext, TimeProvider clock)
{
    private static readonly string[] SortFields = ["name", "createdAt", "email"];

    public async Task<CustomerDto> CreateAsync(CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = Customer.Create(tenantContext.RequireTenantId(), request.Name, request.Email, request.Phone, clock.GetUtcNow());
        db.Customers.Add(customer);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(customer);
    }

    public async Task<CustomerDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToDto(await LoadAsync(id, cancellationToken, tracking: false));

    public async Task<PagedResult<CustomerDto>> ListAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        // The global tenant query filter is applied by the DbContext; no tenantId is ever taken from the client.
        var customers = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            customers = customers.Where(c => c.Name.ToLower().Contains(term) || (c.Email != null && c.Email.Contains(term)));
        }

        if (query.Status is { } status)
        {
            customers = customers.Where(c => c.Status == status);
        }

        var sort = SortSpec.Parse(query.Sort, SortFields, new SortSpec("createdAt", true));
        customers = sort.Field switch
        {
            "name" => customers.OrderBy(c => c.Name, sort.Descending),
            "email" => customers.OrderBy(c => c.Email, sort.Descending),
            _ => customers.OrderBy(c => c.CreatedAt, sort.Descending),
        };

        return await customers.ToPagedResultAsync(
            new PageRequest(query.Page, query.PageSize),
            q => q.Select(c => new CustomerDto(c.Id, c.Name, c.Email, c.Phone, c.Status, c.CreatedAt, c.UpdatedAt)),
            cancellationToken);
    }

    public async Task<CustomerDto> UpdateAsync(Guid id, UpdateCustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = await LoadAsync(id, cancellationToken);
        customer.Update(request.Name, request.Email, request.Phone, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(customer);
    }

    /// <summary>Deletion policy: customers are deactivated (soft delete) because financial records reference them.</summary>
    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var customer = await LoadAsync(id, cancellationToken);
        customer.Deactivate(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<Customer> LoadAsync(Guid id, CancellationToken cancellationToken, bool tracking = true)
    {
        var query = tracking ? db.Customers : db.Customers.AsNoTracking();
        return await query.SingleOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new NotFoundException("Customer");
    }

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.Name, c.Email, c.Phone, c.Status, c.CreatedAt, c.UpdatedAt);
}

/// <summary>Public API of the Customers module for other modules.</summary>
public interface ICustomerDirectory
{
    Task<CustomerSnapshot?> FindAsync(Guid customerId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
}

public sealed record CustomerSnapshot(Guid Id, string Name, bool IsActive);

internal sealed class CustomerDirectory(ICustomersDb db) : ICustomerDirectory
{
    public Task<CustomerSnapshot?> FindAsync(Guid customerId, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking()
            .Where(c => c.Id == customerId)
            .Select(c => new CustomerSnapshot(c.Id, c.Name, c.Status == CustomerStatus.Active))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await db.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
    }
}
