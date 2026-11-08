using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Audit;
using PayFlow.Domain.Common;
using PayFlow.Domain.Customers;
using PayFlow.Domain.Notifications;
using PayFlow.Domain.Orders;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Products;
using PayFlow.Domain.Refunds;
using PayFlow.Domain.Tenants;
using PayFlow.Infrastructure.Messaging;

namespace PayFlow.Infrastructure.Persistence;

public sealed class PayFlowDbContext(
    DbContextOptions<PayFlowDbContext> options,
    ITenantContext tenantContext,
    IIntegrationEventMapper eventMapper,
    TimeProvider clock)
    : IdentityUserContext<AppUser, Guid>(options),
        IUnitOfWork, ITenantsDb, ICustomersDb, ICatalogDb, IOrdersDb, IPaymentsDb, INotificationsDb, IAuditDb
{
    /// <summary>Entity types protected by the global tenant query filter and the cross-tenant write guard.</summary>
    internal static readonly Type[] TenantFilteredTypes =
    [
        typeof(Customer), typeof(Product), typeof(Order), typeof(OrderItem), typeof(Payment), typeof(PaymentAttempt),
        typeof(Refund), typeof(Notification),
    ];

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();

    public DbSet<TenantInvitation> TenantInvitations => Set<TenantInvitation>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<TenantSequence> TenantSequences => Set<TenantSequence>();

    public DbSet<MockProviderTransaction> MockProviderTransactions => Set<MockProviderTransaction>();

    /// <summary>Evaluated per query: the filter value always reflects the current tenant context.</summary>
    internal Guid? CurrentTenantId => tenantContext.TenantId;

    public bool HasActiveTransaction => Database.CurrentTransaction is not null;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(b =>
        {
            b.ToTable("users");
            b.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
            b.Property(u => u.Email).HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).HasMaxLength(256);
        });
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        builder.ApplyConfigurationsFromAssembly(typeof(PayFlowDbContext).Assembly);

        // Ids are generated in code (UUIDv7). Without this, EF would treat a NEW child entity that already has a
        // key (e.g. an extra PaymentAttempt added to a tracked Payment) as an existing row and issue an UPDATE.
        foreach (var entityType in builder.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
        {
            builder.Entity(entityType.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();
        }

        // Defense layer 1: every query on tenant-owned tables gets "tenant_id = <current tenant>".
        // With no tenant in context the filter matches nothing (fail closed).
        builder.Entity<Customer>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        builder.Entity<Product>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        builder.Entity<Order>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        builder.Entity<OrderItem>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        builder.Entity<Payment>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        builder.Entity<PaymentAttempt>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        builder.Entity<Refund>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        builder.Entity<Notification>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as readable strings (status VARCHAR), never as magic integers.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardTenantOwnership();
        BumpRowVersions();
        WriteDomainEventsToOutbox();

        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The resource was modified by another request. Reload and try again.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new UniqueConstraintViolationException(pg.ConstraintName ?? "unknown", ex);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync; synchronous saves are not supported.");

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction is not null)
        {
            return await action(cancellationToken);
        }

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            ChangeTracker.Clear(); // tracked state no longer matches the database after a rollback
            throw;
        }
    }

    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async ct =>
            {
                await action(ct);
                return true;
            },
            cancellationToken);

    public void ResetTracking() => ChangeTracker.Clear();

    public async Task LockTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        await Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM tenants WHERE id = {tenantId} FOR UPDATE", cancellationToken);
    }

    public async Task LockPaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("A tenant context is required to lock a payment.");
        await Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM payments WHERE id = {paymentId} AND tenant_id = {tenantId} FOR UPDATE",
            cancellationToken);
    }

    public async Task<long> NextOrderSequenceAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO tenant_sequences (tenant_id, name, value) VALUES (@tenant, 'order', 1)
            ON CONFLICT (tenant_id, name) DO UPDATE SET value = tenant_sequences.value + 1
            RETURNING value
            """;
        var result = await ExecuteScalarAsync(sql, cancellationToken, new NpgsqlParameter("tenant", tenantId));
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Runs a scalar command on the context's connection, inside the current transaction if any.</summary>
    internal async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        var connection = Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.AddRange(parameters);
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await Database.CloseConnectionAsync();
            }
        }
    }

    private void EnsureTransaction()
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Row locks require an open transaction (use ExecuteInTransactionAsync).");
        }
    }

    /// <summary>Defense layer 2: never let a tenant-scoped write target another tenant's rows.</summary>
    private void GuardTenantOwnership()
    {
        var currentTenant = tenantContext.TenantId;
        foreach (var entry in ChangeTracker.Entries<ITenantScoped>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)
                || !TenantFilteredTypes.Contains(entry.Entity.GetType()))
            {
                continue;
            }

            if (currentTenant is null || entry.Entity.TenantId != currentTenant)
            {
                throw new InvalidOperationException(
                    $"Blocked a write to {entry.Entity.GetType().Name} outside the current tenant context.");
            }
        }
    }

    private void BumpRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries<AggregateRoot>())
        {
            if (entry.State == EntityState.Modified)
            {
                var property = entry.Property(nameof(AggregateRoot.RowVersion));
                property.CurrentValue = (long)property.OriginalValue! + 1;
            }
        }
    }

    private void WriteDomainEventsToOutbox()
    {
        var aggregates = ChangeTracker.Entries<AggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        var now = clock.GetUtcNow();
        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                var message = eventMapper.Map(domainEvent, now);
                if (message is not null)
                {
                    OutboxMessages.Add(message);
                }
            }

            aggregate.ClearDomainEvents();
        }
    }
}
