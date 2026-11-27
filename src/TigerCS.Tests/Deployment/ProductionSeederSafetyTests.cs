using TigerCS.ProductionSeeder;

namespace TigerCS.Tests.Deployment;

public sealed class ProductionSeederSafetyTests
{
    private const string Target = "TGAESHJDBP02/TigerCS_Ticketing_Prod";

    [Theory]
    [InlineData("seed")]
    [InlineData("seed", "--target", "production")]
    [InlineData("seed", "--target", "production", "--confirm-production", Target)]                                   // no backup confirmation
    [InlineData("seed", "--target", "production", "--confirm-production", "TGAESHJDBP02/TigerCS_Ticketing_SeedTest", "--backup-confirmed")]
    [InlineData("seed", "--target", "production", "--confirm-seed-test", "--backup-confirmed")]
    [InlineData("seed", "--target", "seedtest")]
    [InlineData("seed", "--target", "other", "--confirm-seed-test")]
    [InlineData("validate", "--confirm-production", Target)]
    [InlineData("migrate")]
    public void AnythingLessThanAnExplicitCompleteRequest_IsRefused(params string[] args) =>
        Assert.NotNull(SeedSafety.Parse(args).Error);

    [Fact]
    public void ProductionSeed_NeedsTheExactTargetAndABackupConfirmation()
    {
        var request = SeedSafety.Parse(["seed", "--target", "production", "--confirm-production", Target, "--backup-confirmed"]);

        Assert.Null(request.Error);
        Assert.True(request.Seed);
        Assert.Equal(SeedTarget.Production, request.Target);
        Assert.False(request.AllowUntrustedCertificate);
    }

    [Fact]
    public void Validate_IsReadOnlyAndDefaultsToSeedTest()
    {
        var plain = SeedSafety.Parse(["validate"]);
        var production = SeedSafety.Parse(["validate", "--target", "production"]);

        Assert.Null(plain.Error);
        Assert.False(plain.Seed);
        Assert.Equal(SeedTarget.SeedTest, plain.Target);
        Assert.False(production.Seed);
        Assert.Equal(SeedTarget.Production, production.Target);
    }

    [Fact]
    public void LegacySeedTestInvocation_StillWorks() =>
        Assert.Null(SeedSafety.Parse(["seed", "--target", "seedtest", "--confirm-seed-test"]).Error);

    [Fact]
    public void MigrationComparison_ComparesIdentities_NotCounts()
    {
        Assert.Empty(SeedSafety.CompareMigrations(["B", "A"], ["A", "B"]));

        // Same count, different identity.
        var swapped = SeedSafety.CompareMigrations(["A", "C"], ["A", "B"]);
        Assert.Contains("Not applied: B", swapped);
        Assert.Contains("Applied but unknown to this build: C", swapped);

        Assert.NotEmpty(SeedSafety.CompareMigrations([], []));
    }

    [Fact]
    public void CountComparison_RequiresTheExactTestedReferenceData()
    {
        var exact = SeedSafety.ExpectedCounts.ToDictionary(e => e.Key, e => e.Value);
        Assert.Empty(SeedSafety.CompareCounts(exact));

        exact["Workflows"] = 34;
        exact.Remove("Categories");
        var problems = SeedSafety.CompareCounts(exact);
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void ApiAndWeb_NeverReferenceTheSeeder()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        foreach (var project in new[] { "TigerCS.Api", "TigerCS.Web" })
        {
            Assert.DoesNotContain("ProductionSeeder", File.ReadAllText(Path.Combine(src, project, project + ".csproj")));
            Assert.DoesNotContain("ProductionReferenceData", File.ReadAllText(Path.Combine(src, project, "Program.cs")));
        }
    }

    [Fact]
    public void PreSeedState_DistinguishesEmpty_AlreadySeeded_AndPartial()
    {
        var empty = SeedSafety.ExpectedCounts.ToDictionary(e => e.Key, _ => 0);
        var exact = SeedSafety.ExpectedCounts.ToDictionary(e => e.Key, e => e.Value);
        var partial = SeedSafety.ExpectedCounts.ToDictionary(e => e.Key, e => e.Value);
        partial["RequestTypes"] = 10;

        Assert.Equal(PreSeedState.Empty, SeedSafety.Classify(empty));
        Assert.Equal(PreSeedState.AlreadySeeded, SeedSafety.Classify(exact));
        Assert.Equal(PreSeedState.Partial, SeedSafety.Classify(partial));
    }
}
