using System.Diagnostics;

namespace TigerCS.Tests.Deployment;

/// <summary>
/// Build output, binaries and secrets must never be tracked (the old tracked publish/ folder was a stale build of an October commit and carried a committed SMTP password).
/// Runs the same rules CI runs (<c>tools/check-repo-hygiene.sh</c>); the rule set itself is also pinned here so it cannot be weakened silently.
/// </summary>
public sealed class RepositoryHygieneTests
{
    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "check-repo-hygiene.sh")) && Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
        return null;
    }

    [Fact]
    public void TheRepositoryTracksNoBuildOutputBinariesOrSecrets()
    {
        var root = RepoRoot();
        if (root is null) return;   // a source archive without .git: nothing to check (CI always has the clone)

        var start = new ProcessStartInfo("bash", "tools/check-repo-hygiene.sh") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, output);
    }

    [Fact]
    public void PublishIsIgnored_AndTheGuardCoversBinariesAndSecretKeys()
    {
        var root = RepoRoot() ?? throw new InvalidOperationException("repo root not found");
        var ignore = File.ReadAllLines(Path.Combine(root, ".gitignore")).Select(l => l.Trim()).ToList();
        Assert.Contains("/publish/", ignore);

        var script = File.ReadAllText(Path.Combine(root, "tools", "check-repo-hygiene.sh"));
        foreach (var token in new[] { "publish|bin|obj", "dll|exe|pdb", "pfx", "password|secret|apikey|signingkey|pepper|token", "never printed" })
            Assert.Contains(token, script);

        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Contains("tools/check-repo-hygiene.sh", ci);
    }
}
