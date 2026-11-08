using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Orders;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Refunds;
using PayFlow.Domain.Tenants;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", t =>
        {
            t.HasCheckConstraint("ck_payments_amount_positive", "amount_minor > 0");
            t.HasCheckConstraint("ck_payments_refunds_non_negative", "refunded_amount_minor >= 0 AND pending_refund_amount_minor >= 0");
            t.HasCheckConstraint("ck_payments_refund_limit", "refunded_amount_minor + pending_refund_amount_minor <= amount_minor");
        });
        builder.HasKey(p => p.Id);
        builder.HasAlternateKey(p => new { p.TenantId, p.Id }).HasName("uq_payments_tenant_id_id");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_payments_order_tenant");

        builder.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(p => p.Provider).HasMaxLength(32).IsRequired();
        builder.Property(p => p.PaymentMethodToken).HasMaxLength(128).IsRequired();
        builder.Property(p => p.FailureCode).HasMaxLength(64);
        builder.Property(p => p.RowVersion).IsConcurrencyToken();

        builder.HasMany(p => p.Attempts)
            .WithOne()
            .HasForeignKey(a => new { a.TenantId, a.PaymentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_payment_attempts_payment_tenant");
        builder.Navigation(p => p.Attempts).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasIndex(p => new { p.TenantId, p.Status }).HasDatabaseName("ix_payments_tenant_status");
        builder.HasIndex(p => new { p.TenantId, p.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_payments_tenant_created");

        // At most one live (non-failed, non-cancelled) payment per order: the database itself prevents a
        // double charge caused by concurrent requests with different idempotency keys.
        builder.HasIndex(p => new { p.TenantId, p.OrderId })
            .IsUnique()
            .HasFilter("status IN ('Created', 'Pending', 'Unknown', 'Succeeded', 'PartiallyRefunded', 'Refunded')")
            .HasDatabaseName("ux_payments_order_active");

        builder.Ignore(p => p.Amount);
        builder.Ignore(p => p.CurrentAttempt);
        builder.Ignore(p => p.IsSettledSuccess);
        builder.Ignore(p => p.RefundableAmountMinor);
        builder.Ignore(p => p.DomainEvents);
    }
}

internal sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("payment_attempts", t => t.HasCheckConstraint("ck_payment_attempts_number_positive", "attempt_number > 0"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Provider).HasMaxLength(32).IsRequired();
        builder.Property(a => a.ProviderReference).HasMaxLength(64);
        builder.Property(a => a.FailureCode).HasMaxLength(64);
        builder.HasIndex(a => new { a.PaymentId, a.AttemptNumber }).IsUnique().HasDatabaseName("ux_payment_attempts_payment_number");
        builder.HasIndex(a => new { a.Provider, a.ProviderReference })
            .IsUnique()
            .HasFilter("provider_reference IS NOT NULL")
            .HasDatabaseName("ux_payment_attempts_provider_reference");
        builder.HasIndex(a => new { a.Status, a.UpdatedAt })
            .HasFilter("status IN ('Unknown', 'ProviderPending', 'Processing')")
            .HasDatabaseName("ix_payment_attempts_reconciliation");
        builder.Ignore(a => a.MerchantReference);
        builder.Ignore(a => a.IsResolved);
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("refunds", t => t.HasCheckConstraint("ck_refunds_amount_positive", "amount_minor > 0"));
        builder.HasKey(r => r.Id);
        builder.HasOne<Payment>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.PaymentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_refunds_payment_tenant");
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(500);
        builder.Property(r => r.ProviderReference).HasMaxLength(64);
        builder.Property(r => r.FailureCode).HasMaxLength(64);
        builder.Property(r => r.RowVersion).IsConcurrencyToken();
        builder.HasIndex(r => new { r.TenantId, r.PaymentId }).HasDatabaseName("ix_refunds_tenant_payment");
        builder.HasIndex(r => new { r.TenantId, r.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_refunds_tenant_created");
        builder.Ignore(r => r.DomainEvents);
    }
}
