using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.12 / MVP-ERD.md §2.12 — append-only assignment history.</summary>
public class TicketAssignmentConfiguration : IEntityTypeConfiguration<TicketAssignment>
{
    public void Configure(EntityTypeBuilder<TicketAssignment> builder)
    {
        builder.ToTable("TicketAssignments");

        builder.HasKey(a => a.TicketAssignmentId);
        builder.Property(a => a.TicketAssignmentId).ValueGeneratedOnAdd();

        builder.Property(a => a.AssignedDepartmentId).IsRequired();
        builder.Property(a => a.AssignedAtUtc).IsRequired();
        builder.Property(a => a.IsCurrent).IsRequired();
        builder.Property(a => a.SupersededAtUtc).IsRequired(false);

        // MVP-Data-Dictionary.md §2.12: "Exactly one true row per TicketId"
        // (app-enforced; filtered unique index recommended).
        builder.HasIndex(a => a.TicketId).IsUnique().HasFilter("[IsCurrent] = 1");

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(a => a.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(a => a.AssignedEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(a => a.AssignedDepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(a => a.AssigningActorEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
