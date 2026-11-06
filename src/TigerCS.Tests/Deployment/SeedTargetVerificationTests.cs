using TigerCS.ProductionSeeder;

namespace TigerCS.Tests.Deployment;

/// <summary>Target verification: only the approved production server (name TGAESHJDBP02 or IP 10.10.10.124) and the exact expected database pass.</summary>
public sealed class SeedTargetVerificationTests
{
    private const string Db = SeedSafety.ProductionDatabase;

    [Theory]
    [InlineData("10.10.10.124", "TGAESHJDBP02", Db)]            // the reported scenario: connected by IP, server reports its name
    [InlineData("10.10.10.124,1433", "TGAESHJDBP02", Db)]
    [InlineData("tcp:10.10.10.124,1433", "TGAESHJDBP02", Db)]
    [InlineData("TGAESHJDBP02", "TGAESHJDBP02", Db)]
    [InlineData("tgaeshjdbp02", "tgaeshjdbp02", "tigercs_ticketing_prod")]
    [InlineData(" 10.10.10.124 ", "TGAESHJDBP02 ", Db + " ")]   // stray whitespace from the server or the connection string is not an identity difference
    public void Approved_target_is_accepted(string dataSource, string reportedServer, string reportedDatabase) =>
        Assert.Null(SeedSafety.VerifyTarget(dataSource, reportedServer, reportedDatabase, Db));

    [Theory]
    [InlineData("10.10.10.125", "TGAESHJDBP02", Db)]            // another IP
    [InlineData("10.10.10.12", "TGAESHJDBP02", Db)]             // prefix of the approved IP
    [InlineData("10.10.10.1244", "TGAESHJDBP02", Db)]
    [InlineData("10.10.10.124.evil.example", "TGAESHJDBP02", Db)]
    [InlineData("localhost", "TGAESHJDBP02", Db)]
    [InlineData(".", "TGAESHJDBP02", Db)]
    [InlineData("TGAESHJDBP02.example.com", "TGAESHJDBP02", Db)] // no wildcard / FQDN
    [InlineData("TGAESHJDBP01", "TGAESHJDBP02", Db)]
    [InlineData("", "TGAESHJDBP02", Db)]
    [InlineData(null, "TGAESHJDBP02", Db)]
    [InlineData("10.10.10.124", "TGAESHJDBP01", Db)]            // right address, but the server says it is another machine
    [InlineData("10.10.10.124", "10.10.10.124", Db)]            // an IP is never accepted as the server's own name
    [InlineData("10.10.10.124", "TGAESHJDBP02​", Db)]      // invisible character: refused, and shown escaped
    [InlineData("10.10.10.124", "TGAESHJDBP02", "TigerCS_Ticketing_SeedTest")]
    [InlineData("10.10.10.124", "TGAESHJDBP02", "master")]
    [InlineData("10.10.10.124", "TGAESHJDBP02", null)]
    [InlineData("10.10.10.124", null, Db)]
    public void Any_other_identity_is_rejected(string? dataSource, string? reportedServer, string? reportedDatabase) =>
        Assert.NotNull(SeedSafety.VerifyTarget(dataSource, reportedServer, reportedDatabase, Db));

    [Fact]
    public void Rejection_shows_invisible_characters_escaped()
    {
        var reason = SeedSafety.VerifyTarget("10.10.10.124", "TGAESHJDBP02​", Db, Db);
        Assert.Contains("\\u200B", reason);
    }

    [Theory]
    [InlineData("10.10.10.124", "10.10.10.124")]
    [InlineData("10.10.10.124,1433", "10.10.10.124")]
    [InlineData("tcp:10.10.10.124", "10.10.10.124")]
    [InlineData("TGAESHJDBP02\\INSTANCE", "TGAESHJDBP02")]
    [InlineData("tcp:TGAESHJDBP02,1433", "TGAESHJDBP02")]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(",1433", null)]
    public void DataSourceHost_extracts_only_the_host(string dataSource, string? expected) =>
        Assert.Equal(expected, SeedSafety.DataSourceHost(dataSource));

    [Fact]
    public void Dry_run_stays_read_only_and_apply_still_requires_backup_confirmation()
    {
        Assert.Null(SeedSafety.Parse(["validate", "--target", "production"]).Error);
        Assert.False(SeedSafety.Parse(["validate", "--target", "production"]).Seed);
        Assert.False(StaffProvisioningCommand.Parse(["provision-staff", "--target", "production"]).Apply);

        Assert.NotNull(SeedSafety.Parse(["seed", "--target", "production", "--confirm-production", "TGAESHJDBP02/TigerCS_Ticketing_Prod"]).Error);
        Assert.NotNull(StaffProvisioningCommand.Parse(
            ["provision-staff", "--target", "production", "--apply", "--confirm-production", "TGAESHJDBP02/TigerCS_Ticketing_Prod",
             "--actor-email", "a@b.c", "--credentials-out", "/tmp/never-created-by-test.txt"]).Error);
    }
}
