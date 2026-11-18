using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>The phone / project / unit entered by hand for a Customers-directory customer, one row per directory identity - see <see cref="CustomerProfileOverride"/>.</summary>
public class CustomerProfileOverrideConfiguration : IEntityTypeConfiguration<CustomerProfileOverride>
{
    public void Configure(EntityTypeBuilder<CustomerProfileOverride> builder)
    {
        builder.ToTable("CustomerProfileOverrides");

        builder.HasKey(o => o.CustomerProfileOverrideId);
        builder.Property(o => o.CustomerProfileOverrideId).ValueGeneratedOnAdd();

        builder.Property(o => o.CustomerKey).IsRequired().HasMaxLength(CustomerProfileOverride.CustomerKeyMaxLength);
        builder.Property(o => o.IdentityKind).IsRequired();
        builder.Property(o => o.CrmBuyerCustomerId);
        builder.Property(o => o.ExternalSource).HasMaxLength(CustomerProfileOverride.IdentityPartMaxLength);
        builder.Property(o => o.ExternalCustomerId).HasMaxLength(CustomerProfileOverride.IdentityPartMaxLength);
        builder.Property(o => o.IdentityPhone).HasMaxLength(CustomerProfileOverride.PhoneNumberMaxLength);

        builder.Property(o => o.PhoneNumber).HasMaxLength(CustomerProfileOverride.PhoneNumberMaxLength);
        builder.Property(o => o.PhoneLocation).HasConversion<byte?>();
        builder.Property(o => o.ProjectName).HasMaxLength(CustomerProfileOverride.ProjectNameMaxLength);
        builder.Property(o => o.UnitNumber).HasMaxLength(CustomerProfileOverride.UnitNumberMaxLength);

        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedByEmployeeId).IsRequired();
        builder.Property(o => o.RowVersion).IsRowVersion();

        // One row per customer, enforced by the database as well as the service.
        builder.HasIndex(o => o.CustomerKey).IsUnique();
    }
}
