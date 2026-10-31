using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>The persisted Round Robin cursor — one row per department.</summary>
public class DepartmentAssignmentRotationConfiguration : IEntityTypeConfiguration<DepartmentAssignmentRotation>
{
    public void Configure(EntityTypeBuilder<DepartmentAssignmentRotation> builder)
    {
        builder.ToTable("DepartmentAssignmentRotations");

        builder.HasKey(r => r.DepartmentId);
        builder.Property(r => r.DepartmentId).ValueGeneratedNever();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(r => r.DepartmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
