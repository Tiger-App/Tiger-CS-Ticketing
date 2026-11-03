using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.Ticketing.Seed;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Infrastructure.Persistence.Migrations;

namespace TigerCS.Tests.GenesysIntegration.Persistence;

/// <summary>
/// The schema half of the Genesys agent identity mapping: what the EF model
/// declares, what the <c>AddGenesysAgentMappingAndInteractionOwnership</c>
/// migration will do to a database, and — on a real relational engine — that
/// the filtered unique index and the restrict foreign key are actually
/// enforced, which the InMemory provider used by the endpoint tests never
/// checks.
/// </summary>
public class GenesysAgentMappingPersistenceTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);

    // ---- The model, as SQL Server will see it ----

    private static TigerCsDbContext SqlServerModelContext() => new(
        new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseSqlServer("Server=(model-only);Database=(never-opened);")
            .Options);

    [Fact]
    public void Model_DeclaresAFilteredUniqueIndex_OnAspNetUsersGenesysUserId()
    {
        using var context = SqlServerModelContext();
        var user = context.Model.FindEntityType(typeof(ApplicationUser))!;

        Assert.Equal("AspNetUsers", user.GetTableName());
        Assert.Equal(ApplicationUser.GenesysUserIdMaxLength, user.FindProperty(nameof(ApplicationUser.GenesysUserId))!.GetMaxLength());
        Assert.True(user.FindProperty(nameof(ApplicationUser.GenesysUserId))!.IsNullable);
        Assert.Equal(ApplicationUser.GenesysEmailMaxLength, user.FindProperty(nameof(ApplicationUser.GenesysEmail))!.GetMaxLength());
        Assert.True(user.FindProperty(nameof(ApplicationUser.GenesysEmail))!.IsNullable);

        var index = Assert.Single(user.GetIndexes(), i => i.Name == "UX_AspNetUsers_GenesysUserId");
        Assert.True(index.IsUnique);
        Assert.Equal("[GenesysUserId] IS NOT NULL", index.GetFilter());
        Assert.Equal(nameof(ApplicationUser.GenesysUserId), Assert.Single(index.Properties).Name);
    }

    [Fact]
    public void Model_DeclaresAnOptionalRestrictForeignKey_FromTicketInteractionsHandledByUserId_ToAspNetUsers()
    {
        using var context = SqlServerModelContext();
        var interaction = context.Model.FindEntityType(typeof(TicketInteraction))!;

        Assert.Equal(TicketInteraction.GenesysAgentUserIdMaxLength, interaction.FindProperty(nameof(TicketInteraction.GenesysAgentUserId))!.GetMaxLength());
        Assert.True(interaction.FindProperty(nameof(TicketInteraction.GenesysAgentUserId))!.IsNullable);
        Assert.True(interaction.FindProperty(nameof(TicketInteraction.HandledByUserId))!.IsNullable);

        var fk = Assert.Single(interaction.GetForeignKeys(), f => f.PrincipalEntityType.ClrType == typeof(ApplicationUser));
        Assert.Equal(nameof(TicketInteraction.HandledByUserId), Assert.Single(fk.Properties).Name);
        Assert.False(fk.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);

        Assert.Single(interaction.GetIndexes(), i => i.Name == "IX_TicketInteractions_HandledByUserId");

        // The conversation uniqueness is now (conversation, service request) — see GenesysMultipleTicketsPerConversationTests — and the
        // other existing indexes are untouched.
        Assert.Single(interaction.GetIndexes(), i => i.Name == "UX_TicketInteractions_GenesysConversationId_ServiceRequestId");
        Assert.Single(interaction.GetIndexes(), i => i.Name == "UX_TicketInteractions_OneOriginatingPerTicket");
    }

    // ---- The migration, as `dotnet ef database update` will run it ----

    [Fact]
    public void Migration_AddsExactlyTheApprovedSchemaChanges()
    {
        var operations = new AddGenesysAgentMappingAndInteractionOwnership().UpOperations;

        var columns = operations.OfType<AddColumnOperation>().Select(c => (c.Table, c.Name, c.IsNullable)).ToList();
        Assert.Contains(("AspNetUsers", "GenesysUserId", true), columns);
        Assert.Contains(("AspNetUsers", "GenesysEmail", true), columns);
        Assert.Contains(("TicketInteractions", "GenesysAgentUserId", true), columns);
        Assert.Contains(("TicketInteractions", "HandledByUserId", true), columns);
        Assert.Equal(4, columns.Count);

        var unique = Assert.Single(operations.OfType<CreateIndexOperation>(), i => i.Name == "UX_AspNetUsers_GenesysUserId");
        Assert.True(unique.IsUnique);
        Assert.Equal("[GenesysUserId] IS NOT NULL", unique.Filter);
        Assert.Equal("AspNetUsers", unique.Table);

        var handledBy = Assert.Single(operations.OfType<CreateIndexOperation>(), i => i.Name == "IX_TicketInteractions_HandledByUserId");
        Assert.False(handledBy.IsUnique);

        var fk = Assert.Single(operations.OfType<AddForeignKeyOperation>());
        Assert.Equal("TicketInteractions", fk.Table);
        Assert.Equal("AspNetUsers", fk.PrincipalTable);
        Assert.Equal("HandledByUserId", Assert.Single(fk.Columns));
        Assert.Equal(ReferentialAction.Restrict, fk.OnDelete);

        // Additive only: no table is dropped or altered, no row is touched.
        Assert.Empty(operations.OfType<DropTableOperation>());
        Assert.Empty(operations.OfType<DropColumnOperation>());
        Assert.Empty(operations.OfType<AlterColumnOperation>());
        Assert.Empty(operations.OfType<SqlOperation>());
        Assert.Empty(operations.OfType<UpdateDataOperation>());
        Assert.Empty(operations.OfType<DeleteDataOperation>());

        // And fully reversible.
        var down = new AddGenesysAgentMappingAndInteractionOwnership().DownOperations;
        Assert.Equal(4, down.OfType<DropColumnOperation>().Count());
        Assert.Single(down.OfType<DropForeignKeyOperation>());
    }

    // ---- A real relational engine: the constraints are enforced ----

    /// <summary>
    /// The real model on SQLite. The one accommodation: SQL Server generates
    /// <c>Tickets.RowVersion</c> (rowversion), which SQLite cannot, so the
    /// SQLite schema gives that column a default. Nothing about the columns,
    /// indexes or foreign keys under test is changed.
    /// </summary>
    private sealed class SqliteTigerCsDbContext(DbContextOptions<TigerCsDbContext> options) : TigerCsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Ticket>().Property(t => t.RowVersion).HasDefaultValueSql("X'0000000000000000'");
        }
    }

    private sealed class SqliteDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public SqliteDatabase()
        {
            _connection.Open();
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }

        public TigerCsDbContext CreateContext() => new SqliteTigerCsDbContext(
            new DbContextOptionsBuilder<TigerCsDbContext>()
                .UseSqlite(_connection)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
                .Options);

        public void Dispose() => _connection.Dispose();
    }

    private static ApplicationUser User(string? genesysUserId, string? genesysEmail = null)
    {
        var id = Guid.NewGuid();
        return new ApplicationUser
        {
            Id = id,
            UserName = $"user-{id:N}",
            NormalizedUserName = $"USER-{id:N}",
            Email = $"user-{id:N}@example.test",
            SecurityStamp = Guid.NewGuid().ToString(),
            GenesysUserId = genesysUserId,
            GenesysEmail = genesysEmail
        };
    }

    [Fact]
    public async Task FindByEmailAsync_MatchesWholeAddressesOnly_CaseInsensitively_AndReturnsEveryDistinctUser()
    {
        using var db = new SqliteDatabase();
        var esraa = User("6f1d2c3b-0a9e-4b8d-9c7f-1e2d3c4b5a69");
        esraa.Email = "callcenter5@tigergroup.ae";
        esraa.NormalizedEmail = "CALLCENTER5@TIGERGROUP.AE";
        var viaGenesysEmail = User("7a1d2c3b-0a9e-4b8d-9c7f-1e2d3c4b5a70", "Genesys.Only@TigerGroup.ae");
        var lookalike = User(null);
        lookalike.Email = "callcenter55@tigergroup.ae";
        lookalike.NormalizedEmail = "CALLCENTER55@TIGERGROUP.AE";
        using (var context = db.CreateContext())
        {
            context.Users.AddRange(esraa, viaGenesysEmail, lookalike);
            context.SaveChanges();
        }

        using var read = db.CreateContext();
        var repository = new TigerCS.Infrastructure.Modules.GenesysIntegration.Repositories.GenesysAgentMappingRepository(read);

        var byAccountEmail = await repository.FindByEmailAsync("  CallCenter5@TigerGroup.ae ");
        Assert.Equal(esraa.Id, Assert.Single(byAccountEmail).UserId);                    // not the callcenter55 look-alike
        Assert.Equal(esraa.GenesysUserId, byAccountEmail[0].GenesysUserId);
        Assert.False(byAccountEmail[0].IsActive);                                        // no Employee row seeded here = not active

        Assert.Equal(viaGenesysEmail.Id, Assert.Single(await repository.FindByEmailAsync("genesys.only@tigergroup.ae")).UserId);
        Assert.Empty(await repository.FindByEmailAsync("callcenter"));                  // partial
        Assert.Empty(await repository.FindByEmailAsync("Esraa Alfadil Alnour Ahmed"));   // a name is never matched

        // Two accounts, one address: BOTH come back, so the resolver can refuse to choose.
        lookalike.Email = "callcenter5@tigergroup.ae";
        lookalike.NormalizedEmail = "CALLCENTER5@TIGERGROUP.AE";
        using (var context = db.CreateContext())
        {
            context.Users.Update(lookalike);
            context.SaveChanges();
        }

        using var again = db.CreateContext();
        Assert.Equal(2, (await new TigerCS.Infrastructure.Modules.GenesysIntegration.Repositories.GenesysAgentMappingRepository(again)
            .FindByEmailAsync("callcenter5@tigergroup.ae")).Count);
    }

    [Fact]
    public void Database_RejectsASecondUserWithTheSameGenesysUserId_ButAllowsAnyNumberOfUnmappedUsers()
    {
        using var db = new SqliteDatabase();
        const string genesysUserId = "6f1d2c3b-0a9e-4b8d-9c7f-1e2d3c4b5a69";

        using (var context = db.CreateContext())
        {
            // Many users with no mapping at all — the filter is what allows this.
            context.Users.AddRange(User(null), User(null), User(null));
            context.Users.Add(User(genesysUserId, "agent@tigerproperties.ae"));
            context.SaveChanges();
        }

        using (var context = db.CreateContext())
        {
            // The same Genesys agent mapped to a second Ticketing user — refused
            // by UX_AspNetUsers_GenesysUserId, whatever the email says.
            context.Users.Add(User(genesysUserId, "someone-else@tigerproperties.ae"));
            Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        }

        using (var context = db.CreateContext())
        {
            Assert.Equal(1, context.Users.Count(u => u.GenesysUserId == genesysUserId));
            Assert.Equal(3, context.Users.Count(u => u.GenesysUserId == null));
        }
    }

    [Fact]
    public void Database_HandledByUserId_MustReferenceARealUser_AndThatUserCannotBeDeletedWhileReferenced()
    {
        using var db = new SqliteDatabase();
        var (ticketId, userId) = SeedTicketAndUser(db);

        using (var context = db.CreateContext())
        {
            var dangling = TicketInteraction.CreateLocal(ticketId, WellKnownChannels.Phone, "+971500000001", Now);
            dangling.RecordHandlingAgentIfAbsent("some-genesys-id", Guid.NewGuid());
            context.TicketInteractions.Add(dangling);
            Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        }

        using (var context = db.CreateContext())
        {
            var owned = TicketInteraction.CreateFromGenesys(
                ticketId, WellKnownChannels.Phone, "+971500000001", "conv-fk", null, null, null, null, null, null, null, Now);
            owned.RecordHandlingAgentIfAbsent("some-genesys-id", userId);
            context.TicketInteractions.Add(owned);
            context.SaveChanges();
        }

        using (var context = db.CreateContext())
        {
            // Restrict: history outlives the user; the user cannot be hard-deleted.
            context.Users.Remove(context.Users.Single(u => u.Id == userId));
            Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        }

        using (var context = db.CreateContext())
        {
            var stored = context.TicketInteractions.Single(i => i.GenesysConversationId == "conv-fk");
            Assert.Equal(userId, stored.HandledByUserId);
            Assert.Equal("some-genesys-id", stored.GenesysAgentUserId);
        }
    }

    [Fact]
    public void HistoricalInteractionRows_RemainValidWithNoOwnership_AndCanBeOwnedLater()
    {
        using var db = new SqliteDatabase();
        var (ticketId, userId) = SeedTicketAndUser(db);

        // Rows exactly as every interaction was written BEFORE this feature:
        // a Genesys-sourced originating interaction and a walk-in one, with
        // no ownership columns set at all.
        using (var context = db.CreateContext())
        {
            context.TicketInteractions.Add(TicketInteraction.CreateFromGenesys(
                ticketId, WellKnownChannels.Phone, "+971500000001", "conv-historical", "+97142000000",
                "queue-1", "CS Queue", "ga-legacy", "Legacy Agent", Now, "Inbound", Now, isOriginatingInteraction: true));
            context.TicketInteractions.Add(TicketInteraction.CreateLocal(ticketId, WellKnownChannels.FaceToFaceKiosk, null, Now.AddHours(1)));
            context.SaveChanges();
        }

        using (var context = db.CreateContext())
        {
            var rows = context.TicketInteractions.Where(i => i.TicketId == ticketId).OrderBy(i => i.CreatedAtUtc).ToList();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Null(r.HandledByUserId);
                Assert.Null(r.GenesysAgentUserId);
            });
            Assert.Equal("ga-legacy", rows[0].GenesysAgentId);
            Assert.Equal("conv-historical", rows[0].GenesysConversationId);
            Assert.Equal(InteractionContextSource.Ticketing, rows[1].Source);

            // And the historical row can still gain an owner later.
            rows[0].RecordHandlingAgentIfAbsent("ga-legacy", userId);
            context.SaveChanges();
        }

        using (var context = db.CreateContext())
        {
            var owned = context.TicketInteractions.Single(i => i.GenesysConversationId == "conv-historical");
            Assert.Equal(userId, owned.HandledByUserId);
            Assert.Equal("ga-legacy", owned.GenesysAgentUserId);
        }
    }

    /// <summary>A department, the channel catalogue, one ticket and one user — the rows an interaction's foreign keys need under a real engine.</summary>
    private static (long TicketId, Guid UserId) SeedTicketAndUser(SqliteDatabase db)
    {
        using var context = db.CreateContext();
        ChannelReferenceData.SeedAsync(context).GetAwaiter().GetResult();

        var department = new Department("Customer Service", "CS");
        context.Departments.Add(department);
        var user = User("mapped-genesys-id");
        context.Users.Add(user);
        context.SaveChanges();

        var ticket = Ticket.CreateUnclassified("TG-CS-260911-0001", department.DepartmentId, "Phone call received via Genesys", Now, null, null);
        context.Tickets.Add(ticket);
        context.SaveChanges();

        return (ticket.TicketId, user.Id);
    }
}
