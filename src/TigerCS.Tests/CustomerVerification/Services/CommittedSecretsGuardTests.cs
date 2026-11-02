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

    /// <summary>
    /// Settings that hold a credential and therefore must be empty or absent in every committed settings
    /// file (src and publish). The values come from user-secrets (Development) or environment variables /
    /// the host's secret store (UAT/Production). Crm:SecretKey and connection-string passwords have their own
    /// guards above and below.
    /// </summary>
    private static readonly string[] SecretSettingPaths =
    [
        "EmailNotifications:Password",
        "Sms:Broadnet:Password",
        "PactApi:ApiKey",
        "Collections:GenesysOutbound:ClientSecret",
        "Jwt:SigningKey",
        "CrmDocuments:OtpCodePepper",
    ];

    /// <summary>
    /// True when <paramref name="settingPath"/> (colon-separated, matched case-insensitively like the
    /// configuration system itself) exists in <paramref name="root"/> with a value that is not empty or
    /// whitespace. Returns only a verdict: the value is never read into a message.
    /// </summary>
    private static bool CarriesANonEmptyValue(JsonElement root, string settingPath)
    {
        var current = root;

        foreach (var segment in settingPath.Split(':'))
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var found = false;
            foreach (var property in current.EnumerateObject())
            {
                if (string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    current = property.Value;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(current.GetString()),
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true
        };
    }

    private static IReadOnlyList<string> CommittedFilesCarryingANonEmptyValue(string settingPath)
    {
        var root = RepositoryRoot();

        return [.. CommittedSettingsFiles()
            .Where(path =>
            {
                using var document = Parse(path);
                return CarriesANonEmptyValue(document.RootElement, settingPath);
            })
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))];
    }

    private static string SecretFoundMessage(string settingPath, IEnumerable<string> offenders)
        => $"{settingPath} must not be committed with a value in any appsettings file (src or publish): it comes from "
            + "user-secrets (Development) or the environment variable / secret store (UAT/Production), per docs/DEV-SETUP.md "
            + "and docs/system/20-smtp-credential-exposure-and-rotation.md. Set it to an empty string in: "
            + string.Join(", ", offenders)
            + ". A value that was ever committed must also be rotated, because it remains in Git history.";

    [Fact]
    public void NoCommittedSettingsFile_CarriesANonEmptySmtpPassword_InEitherSrcOrPublish()
    {
        var offenders = CommittedFilesCarryingANonEmptyValue("EmailNotifications:Password");

        Assert.True(offenders.Count == 0, SecretFoundMessage("EmailNotifications:Password", offenders));
    }

    [Theory]
    [InlineData("Sms:Broadnet:Password")]
    [InlineData("PactApi:ApiKey")]
    [InlineData("Collections:GenesysOutbound:ClientSecret")]
    [InlineData("Jwt:SigningKey")]
    [InlineData("CrmDocuments:OtpCodePepper")]
    public void NoCommittedSettingsFile_CarriesANonEmptyCredential_InEitherSrcOrPublish(string settingPath)
    {
        Assert.Contains(settingPath, SecretSettingPaths);

        var offenders = CommittedFilesCarryingANonEmptyValue(settingPath);

        Assert.True(offenders.Count == 0, SecretFoundMessage(settingPath, offenders));
    }

    /// <summary>
    /// A detector that never fires would pass forever. Pin its verdicts on synthetic JSON (no real
    /// credential involved) and that the failure message names files but never repeats the value.
    /// </summary>
    [Theory]
    [InlineData("{\"EmailNotifications\":{\"Password\":\"synthetic-value\"}}", true)]
    [InlineData("{\"emailnotifications\":{\"password\":\"synthetic-value\"}}", true)]
    [InlineData("{\"EmailNotifications\":{\"Password\":42}}", true)]
    [InlineData("{\"EmailNotifications\":{\"Password\":\"\"}}", false)]
    [InlineData("{\"EmailNotifications\":{\"Password\":\"   \"}}", false)]
    [InlineData("{\"EmailNotifications\":{\"Password\":null}}", false)]
    [InlineData("{\"EmailNotifications\":{}}", false)]
    [InlineData("{}", false)]
    [InlineData("{\"EmailNotifications\":\"not-an-object\"}", false)]
    public void TheSecretDetector_FlagsOnlyAValue_AndIsCaseInsensitive(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, CarriesANonEmptyValue(document.RootElement, "EmailNotifications:Password"));
    }

    [Fact]
    public void TheSecretFailureMessage_NamesTheFiles_NeverTheValue()
    {
        const string synthetic = "synthetic-value-that-must-not-be-echoed";
        using var document = JsonDocument.Parse($"{{\"EmailNotifications\":{{\"Password\":\"{synthetic}\"}}}}");
        Assert.True(CarriesANonEmptyValue(document.RootElement, "EmailNotifications:Password"));

        var message = SecretFoundMessage("EmailNotifications:Password", ["publish/Example/appsettings.json"]);

        Assert.Contains("publish/Example/appsettings.json", message, StringComparison.Ordinal);
        Assert.DoesNotContain(synthetic, message, StringComparison.Ordinal);
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
    /// A scan that silently finds nothing would pass forever. Pin that both
    /// trees are actually reached, so a moved test file or a renamed directory
    /// fails loudly instead of turning the guards above into no-ops.
    /// </summary>
    [Fact]
    public void TheScan_ActuallyReachesBothTrees()
    {
        var root = RepositoryRoot();
        var scanned = CommittedSettingsFiles()
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        Assert.True(
            File.Exists(Path.Combine(root, "docs", "DEV-SETUP.md")),
            $"Repository root did not resolve to the repository: {root}");

        foreach (var tree in ScannedRoots)
        {
            Assert.True(
                scanned.Any(path => path.StartsWith($"{tree}/", StringComparison.Ordinal)),
                $"Found no appsettings*.json under '{tree}/' — the guard would be vacuous. Scanned: "
                + (scanned.Count == 0 ? "(nothing)" : string.Join(", ", scanned)));
        }
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
