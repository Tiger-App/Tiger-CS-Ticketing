using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Application.Modules.Collections.TowerAssignments;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Collections.TowerAssignments;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// The monthly tower -> collector mapping. The tower is identified by the local <c>CollectionsTowers.TowerId</c> (a table maintained outside EF, so no foreign key to it); the tower code and
/// name are copied so a row and its history stay readable if the tower is later renamed. The unique index is the guarantee that a tower has one primary collector per month, whatever the
/// application does concurrently.
/// </summary>
public class CollectionsTowerAssignmentConfiguration : IEntityTypeConfiguration<CollectionsTowerAssignment>
{
    public void Configure(EntityTypeBuilder<CollectionsTowerAssignment> builder)
    {
        builder.ToTable("CollectionsTowerAssignments");
        builder.HasKey(a => a.CollectionsTowerAssignmentId);
        builder.Property(a => a.CollectionsTowerAssignmentId).ValueGeneratedOnAdd();
        builder.Property(a => a.TowerCode).HasMaxLength(20).IsRequired();
        builder.Property(a => a.TowerName).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Status).HasConversion<byte>().IsRequired();

        builder.HasIndex(a => new { a.TowerId, a.Year, a.Month })
            .IsUnique()
            .HasFilter("[IsPrimary] = 1")
            .HasDatabaseName("UX_CollectionsTowerAssignments_Tower_Month_Primary");
        builder.HasIndex(a => new { a.Year, a.Month, a.EmployeeId })
            .HasDatabaseName("IX_CollectionsTowerAssignments_Month_Employee");

        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CollectionsTowerAssignmentHistoryConfiguration : IEntityTypeConfiguration<CollectionsTowerAssignmentHistory>
{
    public void Configure(EntityTypeBuilder<CollectionsTowerAssignmentHistory> builder)
    {
        builder.ToTable("CollectionsTowerAssignmentHistory");
        builder.HasKey(h => h.CollectionsTowerAssignmentHistoryId);
        builder.Property(h => h.CollectionsTowerAssignmentHistoryId).ValueGeneratedOnAdd();
        builder.Property(h => h.TowerCode).HasMaxLength(20).IsRequired();
        builder.Property(h => h.TowerName).HasMaxLength(200).IsRequired();
        builder.Property(h => h.Action).HasConversion<byte>().IsRequired();
        builder.HasIndex(h => new { h.Year, h.Month, h.TowerId }).HasDatabaseName("IX_CollectionsTowerAssignmentHistory_Month_Tower");

        builder.HasOne<Employee>().WithMany().HasForeignKey(h => h.PreviousEmployeeId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Employee>().WithMany().HasForeignKey(h => h.NewEmployeeId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Employee>().WithMany().HasForeignKey(h => h.ChangedByEmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Append-only assignment event log of a ticket (Collections). Complements, and does not replace, the "current assignee" rows of <c>TicketAssignments</c>.</summary>
public class TicketAssignmentHistoryEntryConfiguration : IEntityTypeConfiguration<TicketAssignmentHistoryEntry>
{
    public void Configure(EntityTypeBuilder<TicketAssignmentHistoryEntry> builder)
    {
        builder.ToTable("TicketAssignmentHistory");
        builder.HasKey(e => e.TicketAssignmentHistoryEntryId);
        builder.Property(e => e.TicketAssignmentHistoryEntryId).ValueGeneratedOnAdd();
        builder.Property(e => e.Kind).HasConversion<byte>().IsRequired();
        builder.Property(e => e.TowerCode).HasMaxLength(20);
        builder.Property(e => e.TowerName).HasMaxLength(200);
        builder.Property(e => e.UnitNumber).HasMaxLength(100);
        builder.Property(e => e.Reason).HasMaxLength(500);
        builder.HasIndex(e => new { e.TicketId, e.ChangedAtUtc }).HasDatabaseName("IX_TicketAssignmentHistory_Ticket_Changed");

        builder.HasOne<Ticket>().WithMany().HasForeignKey(e => e.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Department>().WithMany().HasForeignKey(e => e.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Employee>().WithMany().HasForeignKey(e => e.NewEmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Employee>().WithMany().HasForeignKey(e => e.PreviousEmployeeId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Employee>().WithMany().HasForeignKey(e => e.ChangedByEmployeeId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CollectionsTowerAssignmentRepository(TigerCsDbContext dbContext) : ICollectionsTowerAssignmentRepository
{
    public async Task<IReadOnlyList<CollectionsTowerAssignment>> ListByMonthAsync(int year, int month, CancellationToken cancellationToken = default) =>
        await dbContext.CollectionsTowerAssignments.Where(a => a.Year == year && a.Month == month).ToListAsync(cancellationToken);

    public Task<CollectionsTowerAssignment?> GetActivePrimaryAsync(int towerId, int year, int month, CancellationToken cancellationToken = default) =>
        dbContext.CollectionsTowerAssignments.AsNoTracking().FirstOrDefaultAsync(
            a => a.TowerId == towerId && a.Year == year && a.Month == month && a.IsPrimary && a.Status == TowerAssignmentStatus.Active, cancellationToken);

    public async Task AddAsync(CollectionsTowerAssignment assignment, CancellationToken cancellationToken = default) =>
        await dbContext.CollectionsTowerAssignments.AddAsync(assignment, cancellationToken);

    public void Remove(CollectionsTowerAssignment assignment) => dbContext.CollectionsTowerAssignments.Remove(assignment);

    public async Task AddHistoryAsync(CollectionsTowerAssignmentHistory entry, CancellationToken cancellationToken = default) =>
        await dbContext.CollectionsTowerAssignmentHistory.AddAsync(entry, cancellationToken);

    public async Task<IReadOnlyList<CollectionsTowerAssignmentHistory>> ListHistoryAsync(int? year, int? month, int? towerId, int take, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CollectionsTowerAssignmentHistory.AsNoTracking().AsQueryable();
        if (year is { } y) query = query.Where(h => h.Year == y);
        if (month is { } m) query = query.Where(h => h.Month == m);
        if (towerId is { } t) query = query.Where(h => h.TowerId == t);
        return await query.OrderByDescending(h => h.ChangedAtUtc).ThenByDescending(h => h.CollectionsTowerAssignmentHistoryId)
            .Take(Math.Clamp(take, 1, 1000)).ToListAsync(cancellationToken);
    }
}

public sealed class TicketAssignmentHistoryRepository(TigerCsDbContext dbContext) : ITicketAssignmentHistoryRepository
{
    public async Task AddAsync(TicketAssignmentHistoryEntry entry, CancellationToken cancellationToken = default) =>
        await dbContext.TicketAssignmentHistory.AddAsync(entry, cancellationToken);

    public async Task<IReadOnlyList<TicketAssignmentHistoryEntry>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketAssignmentHistory.AsNoTracking().Where(e => e.TicketId == ticketId)
            .OrderBy(e => e.ChangedAtUtc).ThenBy(e => e.TicketAssignmentHistoryEntryId).ToListAsync(cancellationToken);

    public Task<TicketAssignmentHistoryEntry?> GetLatestAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketAssignmentHistory.AsNoTracking().Where(e => e.TicketId == ticketId)
            .OrderByDescending(e => e.ChangedAtUtc).ThenByDescending(e => e.TicketAssignmentHistoryEntryId).FirstOrDefaultAsync(cancellationToken);
}
