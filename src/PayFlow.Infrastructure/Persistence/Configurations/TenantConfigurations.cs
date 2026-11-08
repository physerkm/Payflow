using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Tenants;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(120).IsRequired();
        builder.Property(t => t.Slug).HasMaxLength(50).IsRequired();
        builder.Property(t => t.DefaultCurrency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(t => t.TimeZone).HasMaxLength(64).IsRequired();
        builder.Property(t => t.RowVersion).IsConcurrencyToken();
        builder.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("ux_tenants_slug");
        builder.Ignore(t => t.DomainEvents);
    }
}

internal sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.ToTable("tenant_memberships");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.RowVersion).IsConcurrencyToken();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique().HasDatabaseName("ux_tenant_memberships_tenant_user");
        builder.HasIndex(m => m.UserId).HasDatabaseName("ix_tenant_memberships_user");
        builder.Ignore(m => m.DomainEvents);
        builder.Ignore(m => m.IsActiveOwner);
    }
}

internal sealed class TenantInvitationConfiguration : IEntityTypeConfiguration<TenantInvitation>
{
    public void Configure(EntityTypeBuilder<TenantInvitation> builder)
    {
        builder.ToTable("tenant_invitations");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.EmailNormalized).HasMaxLength(256).IsRequired();
        builder.Property(i => i.RowVersion).IsConcurrencyToken();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(i => new { i.TenantId, i.EmailNormalized }).HasDatabaseName("ix_tenant_invitations_tenant_email");
        builder.HasIndex(i => i.EmailNormalized).HasDatabaseName("ix_tenant_invitations_email");
        builder.Ignore(i => i.DomainEvents);
    }
}
