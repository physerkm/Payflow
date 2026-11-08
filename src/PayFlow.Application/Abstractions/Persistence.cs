using Microsoft.EntityFrameworkCore;
using PayFlow.Domain.Audit;
using PayFlow.Domain.Customers;
using PayFlow.Domain.Notifications;
using PayFlow.Domain.Orders;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Products;
using PayFlow.Domain.Refunds;
using PayFlow.Domain.Tenants;

namespace PayFlow.Application.Abstractions;

/// <summary>
/// Unit of work over the single PostgreSQL database. Domain events of saved aggregates are converted
/// into outbox messages inside the same transaction.
/// </summary>
public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="action"/> in a transaction, or joins the transaction that is already open.</summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);

    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);

    /// <summary>Detaches all tracked entities so the next queries read fresh state from the database.</summary>
    void ResetTracking();
}

/// <summary>Records provider webhook events for deduplication (unique provider + provider_event_id).</summary>
public interface IWebhookEventStore
{
    /// <returns><c>false</c> if the event was already received (duplicate delivery).</returns>
    Task<bool> TryRecordAsync(string provider, string providerEventId, string payload, CancellationToken cancellationToken);

    Task MarkAsync(string provider, string providerEventId, string status, string? error, CancellationToken cancellationToken);
}

// Each module sees only its own tables. The single DbContext implements all of these interfaces,
// so module boundaries are enforced at compile time while transactions stay local and simple.

public interface ITenantsDb
{
    DbSet<Tenant> Tenants { get; }

    DbSet<TenantMembership> TenantMemberships { get; }

    DbSet<TenantInvitation> TenantInvitations { get; }

    /// <summary>Takes a row lock on the tenant (SELECT ... FOR UPDATE) to serialize membership changes.</summary>
    Task LockTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}

public interface ICustomersDb
{
    DbSet<Customer> Customers { get; }
}

public interface ICatalogDb
{
    DbSet<Product> Products { get; }
}

public interface IOrdersDb
{
    DbSet<Order> Orders { get; }

    DbSet<OrderItem> OrderItems { get; }

    /// <summary>Atomically allocates the next per-tenant order sequence number.</summary>
    Task<long> NextOrderSequenceAsync(Guid tenantId, CancellationToken cancellationToken);
}

public interface IPaymentsDb
{
    DbSet<Payment> Payments { get; }

    DbSet<PaymentAttempt> PaymentAttempts { get; }

    DbSet<Refund> Refunds { get; }

    /// <summary>Takes a row lock on the payment (SELECT ... FOR UPDATE) for refund-limit checks.</summary>
    Task LockPaymentAsync(Guid paymentId, CancellationToken cancellationToken);
}

public interface INotificationsDb
{
    DbSet<Notification> Notifications { get; }
}

public interface IAuditDb
{
    DbSet<AuditLog> AuditLogs { get; }
}
