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

    /// <summary>The one IP address approved for <see cref="Server"/>: the same machine, reached by address. Nothing else is accepted.</summary>
    public const string ServerAddress = "10.10.10.124";
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

    /// <summary>
    /// The host part of a SQL Server <c>Data Source</c> (protocol prefix, port and instance name removed), or null when there is none.
    /// Examples: <c>10.10.10.124,1433</c>, <c>tcp:10.10.10.124</c>, <c>TGAESHJDBP02\SQL</c>.
    /// </summary>
    public static string? DataSourceHost(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource)) return null;
        var host = dataSource.Trim();
        var colon = host.IndexOf(':');
        if (colon >= 0 && colon < host.Length - 1 && host[..colon].ToLowerInvariant() is "tcp" or "np" or "lpc") host = host[(colon + 1)..];
        var cut = host.IndexOfAny([',', '\\']);
        if (cut >= 0) host = host[..cut];
        host = host.Trim();
        return host.Length == 0 ? null : host;
    }

    /// <summary>True only for the approved production server's name or its approved IP address (exact, case-insensitive; no wildcards, no other hosts).</summary>
    public static bool IsApprovedServerHost(string? host) =>
        host is not null
        && (string.Equals(host.Trim(), Server, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host.Trim(), ServerAddress, StringComparison.Ordinal));

    /// <summary>
    /// Verifies the connection really targets the approved server and the expected database. Two independent facts must both hold:
    /// the connection string's Data Source is the approved name or IP, AND the server itself reports (<c>@@SERVERNAME</c>) the approved name
    /// and (<c>DB_NAME()</c>) the expected database. Returns null when verified, otherwise the reason, with the reported values escaped so
    /// invisible characters are visible. An IP address is never reported by the server, so it is only ever accepted from the Data Source
    /// and never as a substitute for the server's own name.
    /// </summary>
    public static string? VerifyTarget(string? dataSource, string? reportedServer, string? reportedDatabase, string expectedDatabase)
    {
        var host = DataSourceHost(dataSource);
        if (!IsApprovedServerHost(host))
            return $"the connection string Data Source host {Escape(host)} is not the approved server ({Server} or {ServerAddress}).";

        if (!string.Equals(reportedServer?.Trim(), Server, StringComparison.OrdinalIgnoreCase))
            return $"the server reports itself as {Escape(reportedServer)}, not the approved {Server}.";

        if (!string.Equals(reportedDatabase?.Trim(), expectedDatabase, StringComparison.OrdinalIgnoreCase))
            return $"the connected database is {Escape(reportedDatabase)}, not {expectedDatabase}.";

        return null;
    }

    private static string Escape(string? value) =>
        value is null ? "(none)" : "\"" + string.Concat(value.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}")) + "\"";

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
