namespace TigerCS.ProductionSeeder;

public enum SeedTarget { SeedTest, Production }

/// <summary>State of the reference tables before a seed: nothing there, exactly the expected data, or something in between.</summary>
public enum PreSeedState { Empty, AlreadySeeded, Partial }

/// <summary>What the command line asked for. <see cref="Error"/> is set instead when the arguments are not an explicit, complete request.</summary>
public sealed record SeedRequest(bool Seed, SeedTarget Target, bool AllowUntrustedCertificate, string? Error);

/// <summary>Pure decisions of the seeder, kept free of I/O so they can be tested without a database.</summary>
public static class SeedSafety
{
    public const string Server = "TGAESHJDBP02";
    public const string SeedTestDatabase = "TigerCS_Ticketing_SeedTest";
    public const string ProductionDatabase = "TigerCS_Ticketing_Prod";

    public const string Usage =
        "Usage:\n" +
        "  validate [--target seedtest|production]            (read-only; default target: seedtest)\n" +
        "  seed --target seedtest --confirm-seed-test\n" +
        "  seed --target production --confirm-production " + Server + "/" + ProductionDatabase + " --backup-confirmed\n" +
        "Optional: --allow-untrusted-certificate (accepts TrustServerCertificate=True for a production seed; logged as a warning)\n" +
        StaffProvisioningCommand.Usage + "\n" +
        "The connection string is read only from the ConnectionStrings__TigerCsDatabase environment variable.";

    public static string DatabaseFor(SeedTarget target) =>
        target == SeedTarget.Production ? ProductionDatabase : SeedTestDatabase;

    public static SeedRequest Parse(string[] args)
    {
        static SeedRequest Fail(string error) => new(false, SeedTarget.SeedTest, false, error);

        if (args.Length == 0 || args[0] is not ("validate" or "seed"))
        {
            return Fail("Unknown command.");
        }

        var seed = args[0] == "seed";
        string? target = null, confirmProduction = null;
        bool confirmSeedTest = false, backup = false, untrusted = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Length: target = args[++i]; break;
                case "--confirm-production" when i + 1 < args.Length: confirmProduction = args[++i]; break;
                case "--confirm-seed-test": confirmSeedTest = true; break;
                case "--backup-confirmed": backup = true; break;
                case "--allow-untrusted-certificate": untrusted = true; break;
                default: return Fail($"Unexpected argument '{args[i]}'.");
            }
        }

        SeedTarget resolved;
        if (target is null && !seed) resolved = SeedTarget.SeedTest;   // legacy: plain "validate"
        else if (string.Equals(target, "seedtest", StringComparison.OrdinalIgnoreCase)) resolved = SeedTarget.SeedTest;
        else if (string.Equals(target, "production", StringComparison.OrdinalIgnoreCase)) resolved = SeedTarget.Production;
        else return Fail("--target seedtest|production is required.");

        if (seed && resolved == SeedTarget.SeedTest && !confirmSeedTest)
        {
            return Fail("Seeding SeedTest requires --confirm-seed-test.");
        }

        if (seed && resolved == SeedTarget.Production)
        {
            if (!string.Equals(confirmProduction, $"{Server}/{ProductionDatabase}", StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"Seeding Production requires --confirm-production {Server}/{ProductionDatabase} (exact).");
            }

            if (!backup)
            {
                return Fail("Seeding Production requires --backup-confirmed (a full backup was taken immediately before).");
            }
        }

        if (!seed && (confirmSeedTest || confirmProduction is not null || backup))
        {
            return Fail("Confirmation flags are only valid with 'seed'.");
        }

        return new SeedRequest(seed, resolved, untrusted, null);
    }

    /// <summary>Exact comparison of the applied migration identities with the ones the code ships, ordered. Empty result means identical.</summary>
    public static IReadOnlyList<string> CompareMigrations(IEnumerable<string> applied, IEnumerable<string> shipped)
    {
        var a = applied.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var s = shipped.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var problems = new List<string>();

        if (s.Length == 0) problems.Add("The build contains no migrations.");
        problems.AddRange(s.Except(a, StringComparer.Ordinal).Select(m => $"Not applied: {m}"));
        problems.AddRange(a.Except(s, StringComparer.Ordinal).Select(m => $"Applied but unknown to this build: {m}"));
        return problems;
    }

    public static readonly IReadOnlyDictionary<string, int> ExpectedCounts = new Dictionary<string, int>
    {
        ["Roles"] = 9,
        ["Departments"] = 8,
        ["Priorities"] = 4,
        ["SlaPolicies"] = 4,
        ["Workflows"] = 35,
        ["RequestTypes"] = 45,
        ["Categories"] = 45
    };

    /// <summary>Differences between actual and expected row counts; empty when every table matches exactly.</summary>
    public static IReadOnlyList<string> CompareCounts(IReadOnlyDictionary<string, int> actual) =>
        ExpectedCounts
            .Where(e => !actual.TryGetValue(e.Key, out var n) || n != e.Value)
            .Select(e => $"{e.Key}: expected {e.Value}, found {(actual.TryGetValue(e.Key, out var n) ? n.ToString() : "n/a")}")
            .ToArray();

    /// <summary>Empty = seed may run; AlreadySeeded = counts equal the expected set (re-run is a verified no-op); Partial = refuse, a human must look.</summary>
    public static PreSeedState Classify(IReadOnlyDictionary<string, int> counts) =>
        counts.Values.All(n => n == 0) ? PreSeedState.Empty
        : CompareCounts(counts).Count == 0 ? PreSeedState.AlreadySeeded
        : PreSeedState.Partial;
}
