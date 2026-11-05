using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>Agent-entered customer name/e-mail, one row per canonical phone number — see <see cref="CustomerContactDetail"/>.</summary>
public class CustomerContactDetailConfiguration : IEntityTypeConfiguration<CustomerContactDetail>
{
    public void Configure(EntityTypeBuilder<CustomerContactDetail> builder)
    {
        builder.ToTable("CustomerContactDetails");

        builder.HasKey(c => c.CustomerContactDetailId);
        builder.Property(c => c.CustomerContactDetailId).ValueGeneratedOnAdd();

        builder.Property(c => c.PhoneKey).IsRequired().HasMaxLength(CustomerContactDetail.PhoneKeyMaxLength);
        builder.Property(c => c.Name).IsRequired().HasMaxLength(CustomerContactDetail.NameMaxLength);
        builder.Property(c => c.Email).HasMaxLength(CustomerContactDetail.EmailMaxLength);
        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedByEmployeeId).IsRequired();
        builder.Property(c => c.RowVersion).IsRowVersion();

        // The duplicate guard: one record per real phone number, enforced by the database as well as the service.
        builder.HasIndex(c => c.PhoneKey).IsUnique();
    }
}
