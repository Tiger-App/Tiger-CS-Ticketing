using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.ProductionSeeder;

/// <summary>What the command line asked of <c>provision-staff</c>. <see cref="Error"/> is set when the request is not explicit and complete.</summary>
public sealed record StaffProvisioningRequest(
    bool Apply, SeedTarget Target, string? ActorEmail, string? CredentialsOut, bool AllowUntrustedCertificate, string? Error);

public static class StaffProvisioningCommand
{
    public const string Usage =
        "  provision-staff --target seedtest|production                         (DRY RUN: read-only plan + department coverage report)\n" +
        "  provision-staff --target seedtest --apply --confirm-seed-test --actor-email <active System Administrator> --credentials-out <new file>\n" +
        "  provision-staff --target production --apply --confirm-production " + SeedSafety.Server + "/" + SeedSafety.ProductionDatabase +
        " --backup-confirmed --actor-email <active System Administrator> --credentials-out <new file>\n" +
        "Initial passwords are generated at run time, written ONLY to --credentials-out (mode 600, must not exist) and never printed or logged.";

    public static StaffProvisioningRequest Parse(string[] args)
    {
        static StaffProvisioningRequest Fail(string error) => new(false, SeedTarget.SeedTest, null, null, false, error);

        string? target = null, actor = null, credentials = null, confirmProduction = null;
        bool apply = false, confirmSeedTest = false, backup = false, untrusted = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Length: target = args[++i]; break;
                case "--actor-email" when i + 1 < args.Length: actor = args[++i]; break;
                case "--credentials-out" when i + 1 < args.Length: credentials = args[++i]; break;
                case "--confirm-production" when i + 1 < args.Length: confirmProduction = args[++i]; break;
                case "--apply": apply = true; break;
                case "--confirm-seed-test": confirmSeedTest = true; break;
                case "--backup-confirmed": backup = true; break;
                case "--allow-untrusted-certificate": untrusted = true; break;
                default: return Fail($"Unexpected argument '{args[i]}'.");
            }
        }

        SeedTarget resolved;
        if (string.Equals(target, "seedtest", StringComparison.OrdinalIgnoreCase)) resolved = SeedTarget.SeedTest;
        else if (string.Equals(target, "production", StringComparison.OrdinalIgnoreCase)) resolved = SeedTarget.Production;
        else return Fail("--target seedtest|production is required.");

        if (!apply)
        {
            return confirmSeedTest || confirmProduction is not null || backup || actor is not null || credentials is not null
                ? Fail("Confirmation, --actor-email and --credentials-out are only valid with --apply.")
                : new StaffProvisioningRequest(false, resolved, null, null, untrusted, null);
        }

        if (string.IsNullOrWhiteSpace(actor)) return Fail("--apply requires --actor-email (an active System Administrator, named in the audit entries).");
        if (string.IsNullOrWhiteSpace(credentials)) return Fail("--apply requires --credentials-out <path> for the one-time initial passwords.");
        if (File.Exists(credentials)) return Fail("--credentials-out must not already exist.");

        if (resolved == SeedTarget.SeedTest && !confirmSeedTest) return Fail("Applying to SeedTest requires --confirm-seed-test.");
        if (resolved == SeedTarget.Production)
        {
            if (!string.Equals(confirmProduction, $"{SeedSafety.Server}/{SeedSafety.ProductionDatabase}", StringComparison.OrdinalIgnoreCase))
                return Fail($"Applying to Production requires --confirm-production {SeedSafety.Server}/{SeedSafety.ProductionDatabase} (exact).");
            if (!backup) return Fail("Applying to Production requires --backup-confirmed (a full backup was taken immediately before).");
        }

        return new StaffProvisioningRequest(true, resolved, actor.Trim(), credentials, untrusted, null);
    }

    /// <summary>A policy-compliant random password (upper, lower, digit, symbol; 20 characters) from the OS CSPRNG.</summary>
    public static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!@#$%^&*-_=+";
        var all = upper + lower + digits + symbols;
        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)], lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)], symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        };
        while (chars.Count < 20) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        RandomNumberGenerator.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chars));
        return new string(chars.ToArray());
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var request = Parse(args);
        if (request.Error is not null)
        {
            Console.Error.WriteLine($"BLOCKED: {request.Error}");
            Console.WriteLine(Usage);
            return 1;
        }

        var expectedDatabase = SeedSafety.DatabaseFor(request.Target);
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var connectionString = configuration.GetConnectionString("TigerCsDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("BLOCKED: ConnectionStrings__TigerCsDatabase is not set.");
            return 1;
        }

        var cs = new SqlConnectionStringBuilder(connectionString);
        if (!string.Equals(cs.InitialCatalog, expectedDatabase, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"BLOCKED: the connection string does not name the {request.Target} database ({expectedDatabase}).");
            return 1;
        }

        if (request.Apply && request.Target == SeedTarget.Production && cs.TrustServerCertificate && !request.AllowUntrustedCertificate)
        {
            Console.Error.WriteLine("BLOCKED: TrustServerCertificate=True is not accepted for a production apply. Fix the SQL certificate, or pass --allow-untrusted-certificate to accept the risk explicitly.");
            return 1;
        }

        var services = new ServiceCollection();
        services.AddLogging(x => x.AddSimpleConsole(o => o.SingleLine = true));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTigerCsInfrastructure(configuration);   // the SAME services User Management uses

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<TigerCsDbContext>();

        try
        {
            await db.Database.OpenConnectionAsync();
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT @@SERVERNAME, DB_NAME()";
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) throw new InvalidOperationException("Could not identify SQL Server.");
                var server = reader.GetString(0);
                var database = reader.GetString(1);
                if (!string.Equals(server, SeedSafety.Server, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(database, expectedDatabase, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"BLOCKED: unexpected SQL target (server {server}, database {database}).");
                    return 1;
                }

                Console.WriteLine($"Target: {request.Target} - server {server}, database {database}");
            }

            var problems = SeedSafety.CompareMigrations(await db.Database.GetAppliedMigrationsAsync(), db.Database.GetMigrations());
            if (problems.Count > 0)
            {
                Console.Error.WriteLine("BLOCKED: migration history does not match this build.");
                foreach (var problem in problems) Console.Error.WriteLine($"  {problem}");
                return 1;
            }

            var provisioning = sp.GetRequiredService<StaffProvisioningService>();
            var coverage = sp.GetRequiredService<DepartmentCoverageService>();
            var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<StaffProvisioningLine> lines;
            if (!request.Apply)
            {
                Console.WriteLine("DRY RUN - read-only. Nothing is written.");
                lines = await provisioning.PlanAsync(StaffRoster.RequestedStaff);
            }
            else
            {
                var actorId = await ResolveActorAsync(sp, request.ActorEmail!);
                if (actorId is null)
                {
                    Console.Error.WriteLine("BLOCKED: --actor-email is not an active System Administrator.");
                    return 1;
                }

                await using var transaction = await db.Database.BeginTransactionAsync();
                lines = await provisioning.ApplyAsync(StaffRoster.RequestedStaff, actorId.Value, entry =>
                    passwords[entry.Email] = GeneratePassword());

                if (lines.Any(l => l.Status == StaffProvisioningStatus.Created && l.EmployeeId is null))
                {
                    await transaction.RollbackAsync();
                    Console.Error.WriteLine("Post-check failed; rolled back. Nothing was written.");
                    return 1;
                }

                // Credentials first (restricted file, never the console); commit only once they are safely on disk.
                var created = lines.Where(l => l.Status == StaffProvisioningStatus.Created).ToList();
                if (created.Count > 0)
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows())
                    {
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // owner-only from the first byte
                    }

                    await using var stream = new FileStream(request.CredentialsOut!, options);
                    await using var writer = new StreamWriter(stream);
                    await writer.WriteLineAsync("# One-time initial passwords. Hand over securely, then delete this file. Users should change them at first sign-in.");
                    foreach (var line in created)
                        await writer.WriteLineAsync($"{line.Entry.Email}\t{passwords[line.Entry.Email]}");
                }

                await transaction.CommitAsync();
            }

            foreach (var line in lines)
            {
                Console.WriteLine($"{line.Status,-13} {line.Entry.FullName} <{line.Entry.Email}> dept={line.DepartmentName ?? "-"} roles=[{string.Join(", ", line.Roles)}] - {line.Detail}");
            }

            Console.WriteLine($"Summary: created/would-create={lines.Count(l => l.Status is StaffProvisioningStatus.Created or StaffProvisioningStatus.WouldCreate)}, " +
                              $"already existing={lines.Count(l => l.Status == StaffProvisioningStatus.AlreadyExists)}, skipped={lines.Count(l => l.Status == StaffProvisioningStatus.Skipped)}");

            Console.WriteLine("Department coverage (active departments):");
            foreach (var d in await coverage.GetAsync())
            {
                Console.WriteLine($"  {d.Code,-4} {d.Name,-28} active={d.ActiveEmployeeCount} assignable={d.AssignableEmployeeCount} status={d.Status}");
            }

            return lines.Any(l => l.Status == StaffProvisioningStatus.Skipped) ? 2 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Provisioning failed ({ex.GetType().Name}): {ex.Message}");
            Console.Error.WriteLine("Any open transaction was rolled back.");
            return 1;
        }
    }

    private static async Task<Guid?> ResolveActorAsync(IServiceProvider sp, string email)
    {
        var accounts = sp.GetRequiredService<IUserAccountManager>();
        var ids = await accounts.SearchAccountIdsAsync(email);
        var matches = await accounts.GetAccountsAsync(ids.ToList());
        var account = matches.Values.FirstOrDefault(a =>
            string.Equals(a.UserName, email, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
        if (account is null) return null;

        var employee = await sp.GetRequiredService<IEmployeeRepository>().GetByIdAsync(account.EmployeeId);
        var roles = await sp.GetRequiredService<IUserRoleReader>().GetRolesAsync(account.EmployeeId);
        return employee is { IsActive: true } && roles.Contains(Roles.SystemAdministrator) ? account.EmployeeId : null;
    }
}
