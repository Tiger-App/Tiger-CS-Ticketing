using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Seed;
using TigerCS.Infrastructure.Persistence;
using TigerCS.ProductionSeeder;

// Reference-data seeder. Never run by the Api/Web. Nothing is written unless a complete, explicit seed request is given and
// every check below passes; the seed and its verification run in ONE transaction that is rolled back on any failure.
// Output never includes the connection string or any credential.

if (args.Length > 0 && args[0] == "provision-staff")
{
    return await StaffProvisioningCommand.RunAsync(args);
}

var request = SeedSafety.Parse(args);
if (request.Error is not null)
{
    Console.Error.WriteLine($"BLOCKED: {request.Error}");
    Console.WriteLine(SeedSafety.Usage);
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

if (request.Target == SeedTarget.Production && cs.TrustServerCertificate)
{
    if (request.Seed && !request.AllowUntrustedCertificate)
    {
        Console.Error.WriteLine("BLOCKED: TrustServerCertificate=True is not accepted for a production seed. Fix the SQL certificate, or pass --allow-untrusted-certificate to accept the risk explicitly.");
        return 1;
    }

    Console.WriteLine("WARNING: TrustServerCertificate=True - the SQL Server identity is not being validated.");
}

var services = new ServiceCollection();
services.AddLogging(x => x.AddSimpleConsole(o => o.SingleLine = true));
services.AddSingleton<IConfiguration>(configuration);
services.AddDbContext<TigerCsDbContext>(options => options.UseSqlServer(connectionString));
services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<TigerCsDbContext>();

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
var clock = System.Diagnostics.Stopwatch.StartNew();

static async Task<Dictionary<string, int>> CountsAsync(TigerCsDbContext db) => new()
{
    ["Roles"] = await db.Roles.CountAsync(),
    ["Departments"] = await db.Departments.CountAsync(),
    ["Priorities"] = await db.Priorities.CountAsync(),
    ["SlaPolicies"] = await db.SlaPolicies.CountAsync(),
    ["Workflows"] = await db.Workflows.CountAsync(),
    ["RequestTypes"] = await db.RequestTypes.CountAsync(),
    ["Categories"] = await db.Categories.CountAsync()
};

static string Format(IReadOnlyDictionary<string, int> counts) => string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}"));

try
{
    await db.Database.OpenConnectionAsync();
    var connection = (SqlConnection)db.Database.GetDbConnection();

    // 1. Hard target verification, asked of the server itself.
    string server, database;
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT @@SERVERNAME, DB_NAME()";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Could not identify SQL Server.");
        server = reader.GetString(0);
        database = reader.GetString(1);
    }

    if (SeedSafety.VerifyTarget(cs.DataSource, server, database, expectedDatabase) is { } targetProblem)
    {
        Console.Error.WriteLine($"BLOCKED: unexpected SQL target - {targetProblem}");
        return 1;
    }

    Console.WriteLine($"Target: {request.Target} - server {server}, database {database}");

    // 2. Full migration identity comparison (not a count).
    var applied = await db.Database.GetAppliedMigrationsAsync();
    var problems = SeedSafety.CompareMigrations(applied, db.Database.GetMigrations());
    if (problems.Count > 0)
    {
        Console.Error.WriteLine("BLOCKED: migration history does not match this build.");
        foreach (var problem in problems) Console.Error.WriteLine($"  {problem}");
        return 1;
    }

    Console.WriteLine($"Migrations: {applied.Count()} applied, identical to this build.");

    // 3. Pre-seed state.
    var before = await CountsAsync(db);
    Console.WriteLine($"Current rows: {Format(before)}");
    var state = SeedSafety.Classify(before);

    // Content check used both for an already-seeded target and for the post-seed verification.
    async Task<List<string>> ContentProblemsAsync()
    {
        var problems = new List<string>();
        var roleNames = await db.Roles.Select(r => r.Name!).ToListAsync();
        problems.AddRange(Roles.All.Except(roleNames).Select(r => $"Missing role: {r}"));
        var departmentCodes = await db.Departments.Select(d => d.Code).ToListAsync();
        problems.AddRange(new[] { "CS", "COL", "REG", "HO", "CC", "ACC", "LCS", "FM" }
            .Except(departmentCodes, StringComparer.OrdinalIgnoreCase).Select(c => $"Missing department: {c}"));
        return problems;
    }

    if (!request.Seed)
    {
        Console.WriteLine(state switch
        {
            PreSeedState.Empty => "Validation successful. Reference tables are empty; a seed would be allowed. No changes made.",
            PreSeedState.AlreadySeeded => "Validation successful. Reference data is already present with the expected counts; a seed would be a no-op. No changes made.",
            _ => "Validation successful. Reference data is PARTIALLY present; a seed would be BLOCKED. No changes made."
        });
        return 0;
    }

    if (state == PreSeedState.AlreadySeeded)
    {
        var existing = await ContentProblemsAsync();
        if (existing.Count > 0)
        {
            Console.Error.WriteLine("BLOCKED: counts match but content differs. Nothing was written.");
            foreach (var line in existing) Console.Error.WriteLine($"  {line}");
            return 1;
        }

        Console.WriteLine("Reference data is already seeded and verified. Nothing to do; nothing was written.");
        return 0;
    }

    if (state == PreSeedState.Partial)
    {
        var populated = before.Where(c => c.Value > 0).Select(c => c.Key);
        Console.Error.WriteLine($"BLOCKED: reference data is partially present ({string.Join(", ", populated)}). Nothing was written; a person must inspect the target.");
        return 1;
    }

    // 4. Seed + verify inside one transaction; disposing it without Commit rolls everything back.
    Console.WriteLine($"Starting reference-data seed for {request.Target}...");
    await using var transaction = await db.Database.BeginTransactionAsync();

    await ProductionReferenceData.SeedAsync(scope.ServiceProvider);

    var after = await CountsAsync(db);
    var verification = SeedSafety.CompareCounts(after).ToList();

    verification.AddRange(await ContentProblemsAsync());

    if (verification.Count > 0)
    {
        Console.Error.WriteLine("Post-seed verification FAILED; rolling back. Nothing was written.");
        foreach (var line in verification) Console.Error.WriteLine($"  {line}");
        await transaction.RollbackAsync();
        return 1;
    }

    await transaction.CommitAsync();
    Console.WriteLine($"Seed committed and verified: {Format(after)} ({clock.Elapsed.TotalSeconds:F1}s).");
    return 0;
}
catch (Exception ex)
{
    // Type and message only: no stack of arguments, no connection string. An uncommitted transaction has been rolled back.
    Console.Error.WriteLine($"Seed failed ({ex.GetType().Name}): {ex.Message}");
    Console.Error.WriteLine("Any open transaction was rolled back. Run 'validate' to inspect the target before retrying.");
    return 1;
}
