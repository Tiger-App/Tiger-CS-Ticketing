using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.EmployeeReviews;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Application.Modules.EmployeeReviews.Services;
using TigerCS.Infrastructure.Modules.EmployeeReviews;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.EmployeeReviews;

/// <summary>
/// The activation-code feature on a REAL SQL Server (skipped, never "passed", without <c>TIGERCS_TEST_SQL_SERVER</c>), each test in a throw-away
/// database with the application's real migrations. What SQLite and the in-memory provider cannot prove is proved here: the migration changes
/// nothing about tablets and reviews that already exist, and the concurrency tokens really do make a code single-use and every guess counted
/// when requests run in parallel.
/// </summary>
public sealed class EmployeeReviewActivationSqlServerTests : IDisposable
{
    private const string Before = "20261124071552_AddEmployeeReviews";

    private readonly string? _master = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsActivationTest_{Guid.NewGuid():N}";
    private readonly EmployeeReviewFixture.FixedTimeProvider _time = new(new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc));

    private readonly Guid _amal = Guid.NewGuid();
    private readonly Guid _bilal = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    private sealed class NoServiceIdentities : IServiceIdentityRegistry
    {
        public bool IsServiceIdentity(Guid employeeId) => false;

        public IReadOnlyCollection<Guid> ServiceIdentityIds => [];
    }

    private sealed class DbAuditWriter(TigerCsDbContext db) : IAuditEntryWriter
    {
        public async Task WriteAsync(Guid? actorEmployeeId, string action, string entityType, string? entityId, string? beforeValue, string? afterValue,
            Guid correlationId, CancellationToken cancellationToken = default) =>
            await db.AuditEntries.AddAsync(new TigerCS.Domain.Audit.AuditEntry(
                actorEmployeeId, action, entityType, entityId, beforeValue, afterValue, correlationId, DateTime.UtcNow), cancellationToken);
    }

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;

    private TigerCsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);

    private EmployeeReviewRepository Repository(TigerCsDbContext c) => new(c, new NoServiceIdentities());

    private EmployeeReviewActivationService Activation(TigerCsDbContext c) => new(Repository(c), new DbAuditWriter(c), _time);

    private EmployeeReviewDeviceAdminService Admin(TigerCsDbContext c) => new(Repository(c), new DbAuditWriter(c), _time);

    private EmployeeReviewTabletService Tablet(TigerCsDbContext c) => new(Repository(c), _time);

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_master))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        command.ExecuteNonQuery();
    }

    private static void Exec(TigerCsDbContext c, string sql, params object[] p) =>
        c.Database.ExecuteSqlRaw(sql, p.Select((v, i) => (object)new SqlParameter($"@p{i}", v ?? DBNull.Value)).ToArray());

    private void CreateDatabase()
    {
        using var master = new SqlConnection(_master);
        master.Open();
        using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{_database}]";
        create.ExecuteNonQuery();
    }

    private void SeedEmployees(TigerCsDbContext c)
    {
        Exec(c, "INSERT INTO Departments(Name,Code,IsActive) VALUES (N'Customer Service',N'CS',1)");
        var cs = c.Database.SqlQuery<int>($"SELECT DepartmentId AS Value FROM Departments WHERE Code = N'CS'").Single();
        foreach (var (id, name) in new[] { (_amal, "Amal Agent"), (_bilal, "Bilal Agent"), (_admin, "Adam Admin") })
        {
            Exec(c, "INSERT INTO AspNetUsers(Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount) VALUES ({0},{1},{2},{3},{4},1,0,0,0,0)",
                id, "u" + id.ToString("N"), "U" + id.ToString("N"), id.ToString("N") + "@example.test", id.ToString("N").ToUpperInvariant() + "@EXAMPLE.TEST");
            Exec(c, "INSERT INTO Employees(EmployeeId,DisplayName,IsGeynessStaff,CreatedAtUtc) VALUES ({0},{1},0,SYSUTCDATETIME())", id, name);
        }

        foreach (var id in new[] { _amal, _bilal })
        {
            Exec(c, "INSERT INTO UserDepartmentAssignments(EmployeeId,DepartmentId,IsPrimary,AssignedAtUtc) VALUES ({0},{1},1,SYSUTCDATETIME())", id, cs);
        }
    }

    private async Task<(Guid DeviceId, string Code)> RegisterAsync(string name, Guid employeeId)
    {
        using var c = CreateContext();
        var result = await Admin(c).RegisterAsync(_admin, new(name, employeeId));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors ?? []));
        return (result.Value!.Device.Id, result.Value.ActivationCode);
    }

    // ------------------------------------------------------------------------------------------------ the migration over existing data

    [SqlServerFact]
    public async Task TheMigration_LeavesEveryExistingTabletAndReview_Untouched_AndTheyKeepWorking()
    {
        CreateDatabase();
        using var c = CreateContext();
        c.GetService<IMigrator>().Migrate(Before);
        SeedEmployees(c);

        // Tablets and reviews as they exist in production today: two activated (their token hash), one revoked, reviews on each.
        var legacyToken = DeviceToken.Generate();
        var otherToken = DeviceToken.Generate();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var d3 = Guid.NewGuid();
        Exec(c, "INSERT INTO EmployeeReviewDevices(Id,DeviceName,EmployeeId,TokenHash,IsActive,CreatedAtUtc,UpdatedAtUtc,LastSeenAtUtc) VALUES ({0},N'Reception desk',{1},{2},1,'2026-11-24 08:00','2026-11-24 08:00','2026-11-25 10:00')", d1, _amal, DeviceToken.Hash(legacyToken)!);
        Exec(c, "INSERT INTO EmployeeReviewDevices(Id,DeviceName,EmployeeId,TokenHash,IsActive,CreatedAtUtc,UpdatedAtUtc) VALUES ({0},N'Lobby desk',{1},{2},1,'2026-11-24 08:05','2026-11-24 08:05')", d2, _bilal, DeviceToken.Hash(otherToken)!);
        Exec(c, "INSERT INTO EmployeeReviewDevices(Id,DeviceName,EmployeeId,TokenHash,IsActive,CreatedAtUtc,UpdatedAtUtc) VALUES ({0},N'Retired desk',{1},NULL,0,'2026-11-24 08:10','2026-11-24 09:00')", d3, _bilal);
        var reviews = new[] { (d1, _amal, 5, "Great"), (d1, _amal, 4, null), (d1, _amal, 1, "Long wait"), (d2, _bilal, 3, "Okay"), (d3, _amal, 2, "Before the tablet was reassigned") };
        foreach (var (device, employee, rating, comment) in reviews)
        {
            Exec(c, "INSERT INTO EmployeeReviews(Id,DeviceId,EmployeeId,Rating,Comment,CreatedAtUtc,SubmissionId) VALUES (NEWID(),{0},{1},{2},{3},'2026-11-25 09:00',NEWID())", device, employee, rating, comment!);
        }

        const string DeviceChecksum = "SELECT CHECKSUM_AGG(CHECKSUM(Id,DeviceName,EmployeeId,TokenHash,IsActive,CreatedAtUtc,UpdatedAtUtc,LastSeenAtUtc)) AS Value FROM EmployeeReviewDevices";
        const string ReviewChecksum = "SELECT CHECKSUM_AGG(CHECKSUM(Id,DeviceId,EmployeeId,Rating,Comment,CreatedAtUtc,SubmissionId)) AS Value FROM EmployeeReviews";
        var devicesBefore = c.Database.SqlQueryRaw<int?>(DeviceChecksum).Single();
        var reviewsBefore = c.Database.SqlQueryRaw<int?>(ReviewChecksum).Single();

        c.GetService<IMigrator>().Migrate();

        Assert.Equal(devicesBefore, c.Database.SqlQueryRaw<int?>(DeviceChecksum).Single());
        Assert.Equal(reviewsBefore, c.Database.SqlQueryRaw<int?>(ReviewChecksum).Single());
        Assert.Equal(3, c.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM EmployeeReviewDevices").Single());
        Assert.Equal(5, c.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM EmployeeReviews").Single());
        Assert.Equal(0, c.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM EmployeeReviewDevices WHERE ActivationCodeHash IS NOT NULL OR ActivationCodeSalt IS NOT NULL OR ActivationCodeExpiresAtUtc IS NOT NULL").Single());
        Assert.Equal(0, c.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM EmployeeReviewActivationThrottles").Single());

        // And the real services, on the migrated database, still serve the tablets that were activated before the change.
        using var fresh = CreateContext();
        Assert.Equal(TabletSessionStatus.Ready, (await Tablet(fresh).GetSessionAsync(legacyToken)).Status);
        Assert.Equal(TabletSessionStatus.Ready, (await Tablet(fresh).GetSessionAsync(otherToken)).Status);
        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, (await Tablet(fresh).SubmitAsync(legacyToken, new(Guid.NewGuid(), 5, "after the migration"))).Outcome);
        var listed = await Admin(fresh).ListAsync();
        Assert.Equal(3, listed.Count);
        Assert.All(listed.Where(d => d.DeviceName != "Retired desk"), d => Assert.True(d.HasCredential && d.IsActive && !d.ActivationPending));
        Assert.Equal(4, c.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM EmployeeReviews WHERE DeviceId = {d1}").Single());
    }

    // ------------------------------------------------------------------------------------------------ the real database enforces the design

    [SqlServerFact]
    public async Task OnARealSqlServer_ARegisteredTablet_IsActivatedOnce_Expires_AndIsReActivatedOnlyOnConfirmation()
    {
        CreateDatabase();
        using (var c = CreateContext())
        {
            c.GetService<IMigrator>().Migrate();
            SeedEmployees(c);
        }

        var (deviceId, code) = await RegisterAsync("Desk 1", _amal);
        using (var c = CreateContext())
        {
            var stored = c.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM EmployeeReviewDevices WHERE Id = {deviceId} AND ActivationCodeHash IS NOT NULL AND DATALENGTH(ActivationCodeHash) = 32 AND DATALENGTH(ActivationCodeSalt) = 16").Single();
            Assert.Equal(1, stored);
        }

        string token;
        using (var c = CreateContext())
        {
            var activated = await Activation(c).ActivateAsync("office", code);
            Assert.Equal(ActivateTabletOutcome.Activated, activated.Outcome);
            token = activated.DeviceToken!;
        }

        using (var c = CreateContext())
        {
            Assert.Equal(ActivateTabletOutcome.InvalidCode, (await Activation(c).ActivateAsync("office", code)).Outcome);   // single use
            Assert.Equal(TabletSessionStatus.Ready, (await Tablet(c).GetSessionAsync(token)).Status);
        }

        // Re-activating a working tablet is refused unless confirmed, and refusing changes nothing.
        using (var c = CreateContext())
        {
            Assert.Equal(TigerCS.Application.Modules.Administration.Dto.AdminOutcome.Conflict, (await Admin(c).IssueActivationCodeAsync(_admin, deviceId)).Outcome);
        }

        string second;
        using (var c = CreateContext())
        {
            Assert.Equal(TabletSessionStatus.Ready, (await Tablet(c).GetSessionAsync(token)).Status);
            second = (await Admin(c).IssueActivationCodeAsync(_admin, deviceId, confirmReplaceActiveCredential: true)).Value!.ActivationCode;
        }

        // The new code expires 24 hours after it was issued: usable up to the last second, refused at the 24-hour mark (datetime2 round trip).
        _time.UtcNow = _time.UtcNow.AddHours(24).AddSeconds(-1);
        using (var c = CreateContext())
        {
            Assert.Single(await Repository(c).ListRedeemableActivationDevicesAsync(_time.UtcNow));
        }

        _time.UtcNow = _time.UtcNow.AddSeconds(1);
        using (var c = CreateContext())
        {
            Assert.Empty(await Repository(c).ListRedeemableActivationDevicesAsync(_time.UtcNow));
            Assert.Equal(ActivateTabletOutcome.InvalidCode, (await Activation(c).ActivateAsync("office", second)).Outcome);
        }
    }

    [SqlServerFact]
    public async Task TwoRequestsRedeemingTheSameCodeAtTheSameTime_HaveExactlyOneWinner()
    {
        CreateDatabase();
        using (var c = CreateContext())
        {
            c.GetService<IMigrator>().Migrate();
            SeedEmployees(c);
        }

        for (var round = 0; round < 5; round++)
        {
            var (deviceId, code) = await RegisterAsync($"Desk {round}", _amal);

            // Every request has its own context and connection, and they all start together.
            using var gate = new ManualResetEventSlim(false);
            var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                using var c = CreateContext();
                gate.Wait();
                return await Activation(c).ActivateAsync($"client-{round}-{i}", code);
            })).ToArray();
            gate.Set();
            var results = await Task.WhenAll(attempts);

            Assert.Equal(1, results.Count(r => r.Outcome == ActivateTabletOutcome.Activated));
            Assert.Equal(7, results.Count(r => r.Outcome == ActivateTabletOutcome.InvalidCode));
            using var check = CreateContext();
            var winner = results.Single(r => r.Outcome == ActivateTabletOutcome.Activated).DeviceToken!;
            Assert.Equal(DeviceToken.Hash(winner), (await check.EmployeeReviewDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId)).TokenHash);
        }
    }

    [SqlServerFact]
    public async Task ParallelGuessesFromOneClient_CanNeverExceedTheAttemptLimit_AndTheCorrectCodeIsRefusedOnceItIsHit()
    {
        CreateDatabase();
        using (var c = CreateContext())
        {
            c.GetService<IMigrator>().Migrate();
            SeedEmployees(c);
        }

        var (deviceId, code) = await RegisterAsync("Desk 1", _amal);
        var wrong = code[0] == 'A' ? "CCCCCCCC" : "AAAAAAAA";

        using var gate = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            using var c = CreateContext();
            gate.Wait();
            return await Activation(c).ActivateAsync("attacker", wrong);
        })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(attempts);

        // However the 40 requests interleave, no more than the limit are ever allowed to check a code; the rest are refused unchecked.
        Assert.True(results.Count(r => r.Outcome == ActivateTabletOutcome.InvalidCode) <= EmployeeReviewActivationService.MaxAttemptsPerClient,
            string.Join(",", results.Select(r => r.Outcome)));
        Assert.Equal(40, results.Count(r => r.Outcome is ActivateTabletOutcome.InvalidCode or ActivateTabletOutcome.TooManyAttempts));

        using var after = CreateContext();
        Assert.Equal(ActivateTabletOutcome.TooManyAttempts, (await Activation(after).ActivateAsync("attacker", code)).Outcome);
        Assert.NotNull((await after.EmployeeReviewDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId)).ActivationCodeHash);   // not used up
        Assert.Equal(ActivateTabletOutcome.Activated, (await Activation(after).ActivateAsync("somebody-else", code)).Outcome);
    }
}
