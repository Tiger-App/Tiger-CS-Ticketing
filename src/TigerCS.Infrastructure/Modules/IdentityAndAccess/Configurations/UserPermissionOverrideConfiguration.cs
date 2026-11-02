using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Configurations;

/// <summary>Employee-specific permission exceptions. Only overrides are stored; the role baseline lives in code.</summary>
public class UserPermissionOverrideConfiguration : IEntityTypeConfiguration<UserPermissionOverride>
{
    public void Configure(EntityTypeBuilder<UserPermissionOverride> builder)
    {
        builder.ToTable("UserPermissionOverrides");

        builder.HasKey(o => o.UserPermissionOverrideId);
        builder.Property(o => o.UserPermissionOverrideId).ValueGeneratedOnAdd();

        builder.Property(o => o.EmployeeId).IsRequired();
        builder.Property(o => o.PermissionKey).HasMaxLength(100).IsRequired();
        builder.Property(o => o.Effect).HasConversion<byte>().IsRequired();
        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.CreatedByEmployeeId);
        builder.Property(o => o.UpdatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedByEmployeeId);

        // Overrides follow the employee; employees are never deleted, but the
        // FK keeps the table honest and removes rows if one ever is.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(o => o.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per (employee, permission): the current state, edited in place.
        builder.HasIndex(o => new { o.EmployeeId, o.PermissionKey }).IsUnique();
    }
}
