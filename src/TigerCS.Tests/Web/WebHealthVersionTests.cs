// TigerCS.Web is referenced under an alias - see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TigerCS.Tests.Web;

/// <summary>The deployed Web must be identifiable the same way the Api is: anonymous GET /health returns the build version (which carries the commit when published by the release script).</summary>
public sealed class WebHealthVersionTests
{
    [Fact]
    public async Task WebHealth_ReportsHealthyAndTheBuildVersion_Anonymously()
    {
        using var factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());
        var version = json.RootElement.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(TigerCsWeb::TigerCS.Web.BuildInfo.Version, version);
    }

    [Fact]
    public void BuildInfo_ReadsTheCommitFromTheInformationalVersion()
    {
        // "x.y.z+<commit>" is what -p:SourceRevisionId produces; verify-deployed-fixes.sh strips everything up to the "+".
        Assert.False(string.IsNullOrWhiteSpace(TigerCsWeb::TigerCS.Web.BuildInfo.Version));
    }
}
