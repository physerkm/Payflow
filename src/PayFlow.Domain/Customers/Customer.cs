using PayFlow.Domain.Common;

namespace PayFlow.Domain.Customers;

public enum CustomerStatus
{
    Active,
    Inactive,
}

/// <summary>A customer of a tenant. Customers are deactivated, never hard-deleted, because orders reference them.</summary>
public sealed class Customer : AggregateRoot, ITenantScoped
{
    private Customer()
    {
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = default!;

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public CustomerStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Customer Create(Guid tenantId, string name, string? email, string? phone, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = Guard.NotEmpty(tenantId, "tenant_required"),
        Name = Guard.NotEmpty(name, "customer_name_required", "Customer name is required", 200),
        Email = Guard.Optional(email, "customer_email_invalid", 256)?.ToLowerInvariant(),
        Phone = Guard.Optional(phone, "customer_phone_invalid", 32),
        Status = CustomerStatus.Active,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Update(string name, string? email, string? phone, DateTimeOffset now)
    {
        Name = Guard.NotEmpty(name, "customer_name_required", "Customer name is required", 200);
        Email = Guard.Optional(email, "customer_email_invalid", 256)?.ToLowerInvariant();
        Phone = Guard.Optional(phone, "customer_phone_invalid", 32);
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        Status = CustomerStatus.Inactive;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        Status = CustomerStatus.Active;
        UpdatedAt = now;
    }
}
