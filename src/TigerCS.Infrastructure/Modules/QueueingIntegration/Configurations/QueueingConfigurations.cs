using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.QueueingIntegration.Configurations;

/// <summary>
/// The inbox. <c>UX_QueueingEvents_Source_EventId</c> is the database-level idempotency guarantee for EventId: however many instances receive the
/// same event at once, exactly one row exists and every other writer gets a unique-constraint violation, answered with the stored outcome.
/// </summary>
public class QueueingEventConfiguration : IEntityTypeConfiguration<QueueingEvent>
{
    public void Configure(EntityTypeBuilder<QueueingEvent> builder)
    {
        builder.ToTable("QueueingEvents");
        builder.HasKey(e => e.QueueingEventId);
        builder.Property(e => e.QueueingEventId).ValueGeneratedOnAdd();

        builder.Property(e => e.Source).HasMaxLength(50).IsRequired();
        builder.Property(e => e.EventId).HasMaxLength(QueueingEvent.EventIdMaxLength).IsRequired();
        builder.Property(e => e.EventType).HasConversion<byte>().IsRequired();
        builder.Property(e => e.ExternalTicketId).HasMaxLength(QueueingTicketLink.ExternalIdMaxLength).IsRequired();
        builder.Property(e => e.EventSequence).IsRequired();
        builder.Property(e => e.OccurredAtUtc).IsRequired();
        // Nullable: erased after the retention period for a successfully processed event. The hash stays, so idempotency never depends on the payload.
        builder.Property(e => e.Payload);
        builder.Property(e => e.PayloadHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(e => e.PayloadPurgedAtUtc);
        builder.Property(e => e.Status).HasConversion<byte>().IsRequired();
        builder.Property(e => e.OutcomeCode).HasMaxLength(64);
        builder.Property(e => e.OutcomeDetail).HasMaxLength(QueueingEvent.DetailMaxLength);
        builder.Property(e => e.ReceivedAtUtc).IsRequired();

        builder.HasIndex(e => new { e.Source, e.EventId }).IsUnique().HasDatabaseName("UX_QueueingEvents_Source_EventId");
        // Serves the per-ticket drain (events waiting for their ticket / predecessor) and the retry sweep.
        builder.HasIndex(e => new { e.Source, e.ExternalTicketId, e.EventSequence }).HasDatabaseName("IX_QueueingEvents_Ticket_Sequence");
        builder.HasIndex(e => new { e.Status, e.NextRetryAtUtc }).HasDatabaseName("IX_QueueingEvents_Status_NextRetry");
        // Serves the retention sweep: processed events that still hold a payload, oldest first.
        builder.HasIndex(e => e.ProcessedAtUtc).HasFilter("[Payload] IS NOT NULL AND [Status] = 2").HasDatabaseName("IX_QueueingEvents_PurgeablePayloads");
    }
}

/// <summary>One state row per queue-originated ticket; <c>TicketId</c> is unique and <c>RowVersion</c> arbitrates concurrent applications.</summary>
public class QueueingTicketLinkConfiguration : IEntityTypeConfiguration<QueueingTicketLink>
{
    public void Configure(EntityTypeBuilder<QueueingTicketLink> builder)
    {
        builder.ToTable("QueueingTicketLinks");
        builder.HasKey(l => l.QueueingTicketLinkId);
        builder.Property(l => l.QueueingTicketLinkId).ValueGeneratedOnAdd();

        builder.Property(l => l.TicketId).IsRequired();
        builder.Property(l => l.ExternalTicketId).HasMaxLength(QueueingTicketLink.ExternalIdMaxLength).IsRequired();
        builder.Property(l => l.QueueNumber).HasMaxLength(Ticket.ExternalReferenceMaxLength);
        builder.Property(l => l.CompanyId).HasMaxLength(QueueingTicketLink.ExternalIdMaxLength);
        builder.Property(l => l.ExternalTicketTypeId).HasMaxLength(QueueingTicketLink.ExternalIdMaxLength);
        builder.Property(l => l.CounterId).HasMaxLength(QueueingTicketLink.ExternalIdMaxLength);
        builder.Property(l => l.AssignedExternalUserId).HasMaxLength(QueueingTicketLink.ExternalIdMaxLength);
        builder.Property(l => l.State).HasConversion<byte>().IsRequired();
        builder.Property(l => l.LastAppliedSequence).IsRequired();
        builder.Property(l => l.LastAppliedOccurredAtUtc).IsRequired();
        builder.Property(l => l.CreatedAtUtc).IsRequired();
        builder.Property(l => l.RowVersion).IsRowVersion();

        builder.HasIndex(l => l.TicketId).IsUnique().HasDatabaseName("UX_QueueingTicketLinks_TicketId");
        builder.HasIndex(l => l.ExternalTicketId).IsUnique().HasDatabaseName("UX_QueueingTicketLinks_ExternalTicketId");

        builder.HasOne<Ticket>().WithMany().HasForeignKey(l => l.TicketId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class QueueingCompanyMappingConfiguration : IEntityTypeConfiguration<QueueingCompanyMapping>
{
    public void Configure(EntityTypeBuilder<QueueingCompanyMapping> builder)
    {
        builder.ToTable("QueueingCompanyMappings");
        builder.HasKey(m => m.QueueingCompanyMappingId);
        builder.Property(m => m.CompanyId).HasMaxLength(QueueingCompanyMapping.ExternalIdMaxLength).IsRequired();
        builder.Property(m => m.DepartmentId).IsRequired();
        builder.Property(m => m.IsActive).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.HasIndex(m => m.CompanyId).IsUnique().HasDatabaseName("UX_QueueingCompanyMappings_CompanyId");
        builder.HasOne<Department>().WithMany().HasForeignKey(m => m.DepartmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class QueueingTicketTypeMappingConfiguration : IEntityTypeConfiguration<QueueingTicketTypeMapping>
{
    public void Configure(EntityTypeBuilder<QueueingTicketTypeMapping> builder)
    {
        builder.ToTable("QueueingTicketTypeMappings");
        builder.HasKey(m => m.QueueingTicketTypeMappingId);
        builder.Property(m => m.CompanyId).HasMaxLength(QueueingCompanyMapping.ExternalIdMaxLength).IsRequired();
        builder.Property(m => m.TicketTypeId).HasMaxLength(QueueingCompanyMapping.ExternalIdMaxLength).IsRequired();
        builder.Property(m => m.RequestTypeId).IsRequired();
        builder.Property(m => m.IsActive).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.HasIndex(m => new { m.CompanyId, m.TicketTypeId }).IsUnique().HasDatabaseName("UX_QueueingTicketTypeMappings_Company_Type");
    }
}

public class QueueingUserMappingConfiguration : IEntityTypeConfiguration<QueueingUserMapping>
{
    public void Configure(EntityTypeBuilder<QueueingUserMapping> builder)
    {
        builder.ToTable("QueueingUserMappings");
        builder.HasKey(m => m.QueueingUserMappingId);
        builder.Property(m => m.ExternalUserId).HasMaxLength(QueueingCompanyMapping.ExternalIdMaxLength).IsRequired();
        builder.Property(m => m.EmployeeId).IsRequired();
        builder.Property(m => m.IsActive).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.HasIndex(m => m.ExternalUserId).IsUnique().HasDatabaseName("UX_QueueingUserMappings_ExternalUserId");
        // One TigerCS employee is one queue employee: two queue ids pointing at one person would make "who is serving" ambiguous.
        builder.HasIndex(m => m.EmployeeId).IsUnique().HasDatabaseName("UX_QueueingUserMappings_EmployeeId");
    }
}
