using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// The repository-wide guard on secrets that must never be committed: no
/// <c>appsettings*.json</c> under <c>src/</c> or <c>publish/</c> may carry
/// <c>Crm:SecretKey</c>, nor a connection string with a password in it.
///
/// <para>
/// Both gaps were found the same way — a named-file test covered <c>src/</c>
/// while <c>publish/</c>, which is committed build output (docs/DEV-SETUP.md),
/// kept its own copies and a real value survived there. So these walk both
/// trees instead of naming files, and a new project or a freshly published
/// output is covered the day it appears.
/// </para>
///
/// <para>
/// Failure messages name offending files only. They never echo the value: a
/// leaked secret must not be copied into CI logs by the very test that catches
/// it.
/// </para>
/// </summary>
public sealed class CommittedSecretsGuardTests
{
    private const string SecretKeyName = "SecretKey";

    /// <summary>The scanned trees, relative to the repository root.</summary>
    private static readonly string[] ScannedRoots = ["src", "publish"];

    /// <summary>
    /// Build output copies the committed files verbatim, so <c>bin</c>/<c>obj</c>
    /// would report the same file twice — and after a fix, a stale copy would
    /// fail a guard about what is *committed*.
    /// </summary>
    private static readonly string[] IgnoredDirectories = ["bin", "obj"];

    private static string RepositoryRoot([CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!, "..", "..", "..", ".."));

    private static IReadOnlyList<string> CommittedSettingsFiles()
    {
        var root = RepositoryRoot();

        return [.. ScannedRoots
            .Select(tree => Path.Combine(root, tree))
            .Where(Directory.Exists)
            .SelectMany(tree => Directory.EnumerateFiles(tree, "appsettings*.json", SearchOption.AllDirectories))
            .Where(path => !Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    private static JsonDocument Parse(string path)
        => JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    private static bool CarriesCrmSecretKey(string path)
    {
        using var document = Parse(path);

        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("Crm", out var crm)
            && crm.ValueKind == JsonValueKind.Object
            && crm.EnumerateObject().Any(p => string.Equals(p.Name, SecretKeyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The names of the connection strings in <paramref name="path"/> whose
    /// value carries a non-empty password. Parsed with
    /// <see cref="SqlConnectionStringBuilder"/> rather than by splitting on
    /// ';', so the <c>Password</c>/<c>pwd</c> synonyms and quoted values are
    /// read the way the driver itself reads them.
    /// </summary>
    private static IEnumerable<string> ConnectionStringsCarryingAPassword(string path)
    {
        using var document = Parse(path);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("ConnectionStrings", out var section)
            || section.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var entry in section.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            SqlConnectionStringBuilder builder;
            try
            {
                builder = new SqlConnectionStringBuilder(entry.Value.GetString());
            }
            catch (ArgumentException)
            {
                // Not a connection string this driver understands. A guard is
                // the wrong place to fail the build over that, and an
                // unparseable value cannot be asserted about either.
                continue;
            }

            if (!string.IsNullOrWhiteSpace(builder.Password))
            {
                yield return entry.Name;
            }
        }
    }

    [Fact]
    public void NoCommittedSettingsFile_CarriesCrmSecretKey_InEitherSrcOrPublish()
    {
        var root = RepositoryRoot();

        var offenders = CommittedSettingsFiles()
            .Where(CarriesCrmSecretKey)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Crm:SecretKey must not be committed in any appsettings file — it comes from user-secrets "
            + "(Development) or the Crm__SecretKey environment variable (UAT/Production), per docs/DEV-SETUP.md §3a. "
            + "Remove the key entirely (an empty value still fails) from: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void NoCommittedSettingsFile_CarriesAConnectionStringPassword_InEitherSrcOrPublish()
    {
        var root = RepositoryRoot();

        var offenders = CommittedSettingsFiles()
            .SelectMany(path => ConnectionStringsCarryingAPassword(path)
                .Select(name => $"{Path.GetRelativePath(root, path).Replace('\\', '/')} [ConnectionStrings:{name}]"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A committed connection string must not carry a password — the whole connection string comes from "
            + "user-secrets (Development) or the ConnectionStrings__TigerCsDatabase environment variable "
            + "(UAT/Production), per docs/DEV-SETUP.md §2. Leave Password= empty and supply the real value there. "
            + "Offending entries: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A scan that silently finds nothing would pass forever. Pin that the
    /// committed tree is actually reached, so a moved test file or a renamed directory
    /// fails loudly instead of turning the guards above into no-ops.
    /// </summary>
    [Fact]
    public void TheScan_ActuallyReachesTheCommittedTree()
    {
        var root = RepositoryRoot();
        var scanned = CommittedSettingsFiles()
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        Assert.True(
            File.Exists(Path.Combine(root, "docs", "DEV-SETUP.md")),
            $"Repository root did not resolve to the repository: {root}");

        // publish/ is no longer a committed tree (tools/check-repo-hygiene.sh forbids tracking it), so it is scanned only when a local, git-ignored
        // publish exists; the committed tree that must always be reached is src/.
        Assert.True(
            scanned.Any(path => path.StartsWith("src/", StringComparison.Ordinal)),
            "Found no appsettings*.json under 'src/' — the guard would be vacuous. Scanned: "
            + (scanned.Count == 0 ? "(nothing)" : string.Join(", ", scanned)));
    }

    /// <summary>
    /// The password guard is only meaningful while it is actually reading
    /// connection strings. Pin that at least one committed file still declares
    /// one, so emptying or renaming the section does not quietly retire the
    /// check above.
    /// </summary>
    [Fact]
    public void TheScan_ActuallyReadsConnectionStrings()
    {
        var root = RepositoryRoot();

        var withConnectionStrings = CommittedSettingsFiles()
            .Where(path =>
            {
                using var document = Parse(path);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("ConnectionStrings", out var section)
                    && section.ValueKind == JsonValueKind.Object
                    && section.EnumerateObject().Any(e => e.Value.ValueKind == JsonValueKind.String);
            })
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        Assert.True(
            withConnectionStrings.Count > 0,
            "No committed appsettings file declares a connection string, so the password guard reads nothing. "
            + "If the section really has gone away, retire the guard deliberately rather than leaving it vacuous.");
    }
}
