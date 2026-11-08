using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Audit;
using PayFlow.Domain.Notifications;
using PayFlow.Domain.Tenants;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(n => n.Type).HasMaxLength(64).IsRequired();
        builder.Property(n => n.Title).HasMaxLength(200).IsRequired();
        builder.Property(n => n.Message).HasMaxLength(1000).IsRequired();
        builder.Property(n => n.ResourceType).HasMaxLength(32);
        builder.HasIndex(n => new { n.TenantId, n.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_notifications_tenant_created");
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Action).HasMaxLength(64).IsRequired();
        builder.Property(a => a.ResourceType).HasMaxLength(32).IsRequired();
        builder.Property(a => a.ResourceId).HasMaxLength(64);
        builder.Property(a => a.CorrelationId).HasMaxLength(64);
        builder.Property(a => a.Metadata).HasColumnType("jsonb");
        builder.HasIndex(a => new { a.TenantId, a.OccurredAt }).IsDescending(false, true).HasDatabaseName("ix_audit_logs_tenant_occurred");
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.EventType).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(1000);
        builder.Property(m => m.CorrelationId).HasMaxLength(64);
        builder.Property(m => m.TraceParent).HasMaxLength(128);
        builder.HasIndex(m => m.MessageId).IsUnique().HasDatabaseName("ux_outbox_messages_message_id");
        builder.HasIndex(m => new { m.AvailableAt, m.CreatedAt })
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ix_outbox_pending");
        builder.HasIndex(m => m.PublishedAt)
            .HasFilter("status = 'Published'")
            .HasDatabaseName("ix_outbox_published_at");
    }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");
        builder.HasKey(m => new { m.ConsumerName, m.MessageId });
        builder.Property(m => m.ConsumerName).HasMaxLength(100);
        builder.HasIndex(m => m.ProcessedAt).HasDatabaseName("ix_inbox_messages_processed_at");
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Operation).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Key).HasMaxLength(128).IsRequired();
        builder.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.ResponseBody).HasColumnType("text");
        builder.Property(r => r.ResponseLocation).HasMaxLength(256);
        builder.HasIndex(r => new { r.TenantId, r.Operation, r.Key }).IsUnique().HasDatabaseName("ux_idempotency_records_scope_key");
        builder.HasIndex(r => r.ExpiresAt).HasDatabaseName("ix_idempotency_records_expires_at");
    }
}

internal sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("webhook_events");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Provider).HasMaxLength(32).IsRequired();
        builder.Property(w => w.ProviderEventId).HasMaxLength(100).IsRequired();
        builder.Property(w => w.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(w => w.Status).HasMaxLength(20).IsRequired();
        builder.Property(w => w.Error).HasMaxLength(200);
        builder.HasIndex(w => new { w.Provider, w.ProviderEventId }).IsUnique().HasDatabaseName("ux_webhook_events_provider_event");
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(t => t.RevokedReason).HasMaxLength(32);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_hash");
        builder.HasIndex(t => t.FamilyId).HasDatabaseName("ix_refresh_tokens_family");
        builder.HasIndex(t => t.UserId).HasDatabaseName("ix_refresh_tokens_user");
    }
}

internal sealed class TenantSequenceConfiguration : IEntityTypeConfiguration<TenantSequence>
{
    public void Configure(EntityTypeBuilder<TenantSequence> builder)
    {
        builder.ToTable("tenant_sequences");
        builder.HasKey(s => new { s.TenantId, s.Name });
        builder.Property(s => s.Name).HasMaxLength(32);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MockProviderTransactionConfiguration : IEntityTypeConfiguration<MockProviderTransaction>
{
    public void Configure(EntityTypeBuilder<MockProviderTransaction> builder)
    {
        builder.ToTable("mock_provider_transactions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Kind).HasMaxLength(16).IsRequired();
        builder.Property(t => t.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(t => t.ProviderReference).HasMaxLength(64).IsRequired();
        builder.Property(t => t.RelatedReference).HasMaxLength(64);
        builder.Property(t => t.PaymentMethodToken).HasMaxLength(128);
        builder.Property(t => t.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(t => t.Status).HasMaxLength(20).IsRequired();
        builder.Property(t => t.FailureCode).HasMaxLength(64);
        builder.Property(t => t.WebhookStatus).HasMaxLength(20);
        builder.HasIndex(t => t.IdempotencyKey).IsUnique().HasDatabaseName("ux_mock_provider_idempotency");
        builder.HasIndex(t => t.ProviderReference).IsUnique().HasDatabaseName("ux_mock_provider_reference");
        builder.HasIndex(t => new { t.Status, t.CreatedAt }).HasDatabaseName("ix_mock_provider_status");
    }
}
