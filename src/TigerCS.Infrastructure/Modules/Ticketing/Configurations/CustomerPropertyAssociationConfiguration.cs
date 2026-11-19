using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>Properties / units manually associated with a customer - see <see cref="CustomerPropertyAssociation"/>. Never CRM or PACT ownership.</summary>
public class CustomerPropertyAssociationConfiguration : IEntityTypeConfiguration<CustomerPropertyAssociation>
{
    public void Configure(EntityTypeBuilder<CustomerPropertyAssociation> builder)
    {
        builder.ToTable("CustomerPropertyAssociations");

        builder.HasKey(a => a.CustomerPropertyAssociationId);
        builder.Property(a => a.CustomerPropertyAssociationId).ValueGeneratedOnAdd();

        builder.Property(a => a.CustomerKey).IsRequired().HasMaxLength(CustomerPropertyAssociation.CustomerKeyMaxLength);
        builder.Property(a => a.CustomerProfileId).IsRequired();
        builder.Property(a => a.ProjectName).IsRequired().HasMaxLength(CustomerPropertyAssociation.ProjectNameMaxLength);
        builder.Property(a => a.UnitNumber).IsRequired().HasMaxLength(CustomerPropertyAssociation.UnitNumberMaxLength);
        builder.Property(a => a.Relationship).IsRequired().HasConversion<byte>();
        builder.Property(a => a.CreatedAtUtc).IsRequired();
        builder.Property(a => a.CreatedByEmployeeId).IsRequired();
        builder.Property(a => a.UpdatedAtUtc).IsRequired();
        builder.Property(a => a.UpdatedByEmployeeId).IsRequired();
        builder.Property(a => a.RowVersion).IsRowVersion();

        // One association per customer, project and unit; the service compares case-insensitively and the database (case-insensitive
        // collation) backs it.
        builder.HasIndex(a => new { a.CustomerProfileId, a.ProjectName, a.UnitNumber }).IsUnique();
        builder.HasIndex(a => a.CustomerKey);
    }
}
