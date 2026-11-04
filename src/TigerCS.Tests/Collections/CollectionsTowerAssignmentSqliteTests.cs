using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.Collections.TowerAssignments;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.Collections;

/// <summary>The one-primary-collector-per-tower-per-month rule proven as a real unique index (SQLite, the EF-mapped schema), so it holds however the application behaves under concurrency.</summary>
public sealed class CollectionsTowerAssignmentSqliteTests : IDisposable
{
    private sealed class SqliteContext(DbContextOptions<TigerCsDbContext> options) : TigerCsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Ticket>().Property(t => t.RowVersion).HasDefaultValueSql("X'0000000000000000'");
            builder.Entity<TicketAgentHandoff>().Property(h => h.RowVersion).HasDefaultValueSql("X'0000000000000000'");
        }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    private SqliteContext NewContext() => new(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlite(_connection).Options);

    public CollectionsTowerAssignmentSqliteTests()
    {
        _connection.Open();
        // Employees hang off Identity users, which this index test does not need: only the unique index is under test.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Employees.Add(new Employee(_alice, "Alice", false, DateTime.UtcNow));
        db.Employees.Add(new Employee(_bob, "Bob", false, DateTime.UtcNow));
        db.SaveChanges();
    }

    private CollectionsTowerAssignment Row(Guid employee, int tower, int month, TowerAssignmentStatus status = TowerAssignmentStatus.Active) =>
        new(tower, tower.ToString(), $"Tower {tower}", 4, 2026, month, employee, status, DateTime.UtcNow, employee);

    [Fact]
    public async Task ASecondCollectorForTheSameTowerAndMonth_IsRejectedByTheDatabase_DraftOrActive()
    {
        await using var db = NewContext();
        db.CollectionsTowerAssignments.Add(Row(_alice, 140, 11));
        await db.SaveChangesAsync();

        db.CollectionsTowerAssignments.Add(Row(_bob, 140, 11, TowerAssignmentStatus.Draft));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TheSameTower_InAnotherMonth_IsFree_AndTheActiveLookupIgnoresDrafts()
    {
        await using var db = NewContext();
        db.CollectionsTowerAssignments.Add(Row(_alice, 140, 11));
        db.CollectionsTowerAssignments.Add(Row(_bob, 140, 12, TowerAssignmentStatus.Draft));
        await db.SaveChangesAsync();
        var repository = new CollectionsTowerAssignmentRepository(db);

        Assert.Equal(_alice, (await repository.GetActivePrimaryAsync(140, 2026, 11))!.EmployeeId);
        Assert.Null(await repository.GetActivePrimaryAsync(140, 2026, 12));      // a draft is not in force
        Assert.Null(await repository.GetActivePrimaryAsync(141, 2026, 11));
    }

    [Fact]
    public async Task TheHistoryTable_KeepsEverythingItIsGiven()
    {
        await using var db = NewContext();
        var row = Row(_alice, 140, 11);
        db.CollectionsTowerAssignments.Add(row);
        var repository = new CollectionsTowerAssignmentRepository(db);
        await repository.AddHistoryAsync(new(row, null, _alice, TowerAssignmentAction.Added, DateTime.UtcNow, _alice));
        await repository.AddHistoryAsync(new(row, _alice, _bob, TowerAssignmentAction.Reassigned, DateTime.UtcNow.AddMinutes(1), _alice));
        await db.SaveChangesAsync();

        var history = await repository.ListHistoryAsync(2026, 11, 140, 50);

        Assert.Equal([TowerAssignmentAction.Reassigned, TowerAssignmentAction.Added], history.Select(h => h.Action));
    }

    public void Dispose() => _connection.Dispose();
}
