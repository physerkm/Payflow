using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Customers;
using PayFlow.Domain.Orders;
using PayFlow.Domain.Products;
using PayFlow.Domain.Tenants;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(c => c.Id);

        // (tenant_id, id) is the target of tenant-safe composite foreign keys.
        builder.HasAlternateKey(c => new { c.TenantId, c.Id }).HasName("uq_customers_tenant_id_id");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(256);
        builder.Property(c => c.Phone).HasMaxLength(32);
        builder.Property(c => c.RowVersion).IsConcurrencyToken();
        builder.HasIndex(c => new { c.TenantId, c.Email }).HasDatabaseName("ix_customers_tenant_email");
        builder.HasIndex(c => new { c.TenantId, c.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_customers_tenant_created");
        builder.Ignore(c => c.DomainEvents);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", t => t.HasCheckConstraint("ck_products_price_positive", "price_minor > 0"));
        builder.HasKey(p => p.Id);
        builder.HasAlternateKey(p => new { p.TenantId, p.Id }).HasName("uq_products_tenant_id_id");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(p => p.Sku).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(p => p.RowVersion).IsConcurrencyToken();
        builder.HasIndex(p => new { p.TenantId, p.Sku }).IsUnique().HasDatabaseName("ux_products_tenant_sku");
        builder.Ignore(p => p.Price);
        builder.Ignore(p => p.DomainEvents);
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", t => t.HasCheckConstraint("ck_orders_total_non_negative", "total_minor >= 0"));
        builder.HasKey(o => o.Id);
        builder.HasAlternateKey(o => new { o.TenantId, o.Id }).HasName("uq_orders_tenant_id_id");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId).OnDelete(DeleteBehavior.Restrict);

        // Tenant-safe FK: an order can only reference a customer of the SAME tenant.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(o => new { o.TenantId, o.CustomerId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_orders_customer_tenant");

        builder.Property(o => o.OrderNumber).HasMaxLength(50).IsRequired();
        builder.Property(o => o.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(o => o.Notes).HasMaxLength(1000);
        builder.Property(o => o.RowVersion).IsConcurrencyToken();

        builder.HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(i => new { i.TenantId, i.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_order_items_order_tenant");
        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasIndex(o => new { o.TenantId, o.OrderNumber }).IsUnique().HasDatabaseName("uq_orders_tenant_number");
        builder.HasIndex(o => new { o.TenantId, o.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_orders_tenant_created");
        builder.HasIndex(o => new { o.TenantId, o.Status }).HasDatabaseName("ix_orders_tenant_status");
        builder.HasIndex(o => new { o.TenantId, o.CustomerId }).HasDatabaseName("ix_orders_tenant_customer");
        builder.Ignore(o => o.Total);
        builder.Ignore(o => o.CanAcceptPayment);
        builder.Ignore(o => o.DomainEvents);
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items", t =>
        {
            t.HasCheckConstraint("ck_order_items_quantity_positive", "quantity > 0");
            t.HasCheckConstraint("ck_order_items_line_total", "line_total_minor = unit_price_minor * quantity");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.SkuSnapshot).HasMaxLength(64).IsRequired();
        builder.Property(i => i.NameSnapshot).HasMaxLength(200).IsRequired();
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(i => new { i.TenantId, i.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_items_product_tenant");
        builder.Ignore(i => i.LineTotal);
    }
}
