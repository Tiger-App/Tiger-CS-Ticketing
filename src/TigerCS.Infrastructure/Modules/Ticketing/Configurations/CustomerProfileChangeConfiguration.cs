using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>The append-only customer change history - see <see cref="CustomerProfileChange"/>. The migration also installs a trigger that rejects UPDATE and DELETE.</summary>
public class CustomerProfileChangeConfiguration : IEntityTypeConfiguration<CustomerProfileChange>
{
    public void Configure(EntityTypeBuilder<CustomerProfileChange> builder)
    {
        builder.ToTable("CustomerProfileChanges");

        builder.HasKey(c => c.CustomerProfileChangeId);
        builder.Property(c => c.CustomerProfileChangeId).ValueGeneratedOnAdd();

        builder.Property(c => c.CustomerProfileId).IsRequired();
        builder.Property(c => c.CustomerKey).IsRequired().HasMaxLength(CustomerProfileChange.CustomerKeyMaxLength);
        builder.Property(c => c.FieldName).IsRequired().HasMaxLength(CustomerProfileChange.FieldNameMaxLength);
        builder.Property(c => c.OldValue).HasMaxLength(CustomerProfileChange.ValueMaxLength);
        builder.Property(c => c.NewValue).HasMaxLength(CustomerProfileChange.ValueMaxLength);
        builder.Property(c => c.ModifiedByEmployeeId).IsRequired();
        builder.Property(c => c.ModifiedByName).IsRequired().HasMaxLength(CustomerProfileChange.ModifiedByNameMaxLength);
        builder.Property(c => c.ModifiedAtUtc).IsRequired();
        builder.Property(c => c.Source).IsRequired().HasMaxLength(CustomerProfileChange.SourceMaxLength);
        builder.Property(c => c.ChangeReason).HasMaxLength(CustomerProfileChange.ReasonMaxLength);
        builder.Property(c => c.CorrelationId).IsRequired();
        builder.Property(c => c.ChangeKey).IsRequired().HasMaxLength(CustomerProfileChange.ChangeKeyLength).IsFixedLength();

        // Retries of one operation can never record the same change twice.
        builder.HasIndex(c => c.ChangeKey).IsUnique();
        // The history of one customer, newest first (the Change History tab).
        builder.HasIndex(c => new { c.CustomerProfileId, c.ModifiedAtUtc });
        builder.HasIndex(c => c.CustomerKey);
        builder.HasIndex(c => c.CorrelationId);
    }
}
