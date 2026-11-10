using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Configurations;

/// <summary>The departments an employee is explicitly authorised to transfer tickets to - separate from membership (view access) and from the reassignment permission.</summary>
public class UserTransferDepartmentConfiguration : IEntityTypeConfiguration<UserTransferDepartment>
{
    public void Configure(EntityTypeBuilder<UserTransferDepartment> builder)
    {
        builder.ToTable("UserTransferDepartments");

        builder.HasKey(t => t.UserTransferDepartmentId);
        builder.Property(t => t.UserTransferDepartmentId).ValueGeneratedOnAdd();
        builder.Property(t => t.EmployeeId).IsRequired();
        builder.Property(t => t.DepartmentId).IsRequired();
        builder.Property(t => t.GrantedAtUtc).IsRequired();
        builder.Property(t => t.GrantedByEmployeeId);

        builder.HasOne<Employee>().WithMany().HasForeignKey(t => t.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Department>().WithMany().HasForeignKey(t => t.DepartmentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => new { t.EmployeeId, t.DepartmentId }).IsUnique();
    }
}
