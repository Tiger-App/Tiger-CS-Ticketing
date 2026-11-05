// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// Regression for the two reported failures, end to end through the real Razor pipeline against a fake TigerCS.Api:
/// <c>GET /Customers</c> ("could not load the customer directory") and
/// <c>GET /Customers/phone:…?fromTicket=440</c> ("This customer profile could not be loaded").
/// With a healthy Api both render (and the profile still leads back to the ticket it was opened from); when the Api's read
/// fails, both pages say so truthfully — never an empty directory, never a fabricated profile — and the Web logs the failing
/// Api call so the cause stays diagnosable.
/// </summary>
public sealed class CustomerPagesRegressionTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    private const string Phone = "971501112222";
    private const string ProfilePath = "/Customers/phone%3A" + Phone + "?fromTicket=440";

    private readonly ConcurrentQueue<string> _logs = new();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;
    private bool _customersApiFails;

    public CustomerPagesRegressionTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FakeApi(() => _customersApiFails)));
                services.AddLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(new CapturingLoggerProvider(_logs)));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                });
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [Fact]
    public async Task Customers_RendersTheDirectory_WhenTheApiAnswers()
    {
        var response = await Client().GetAsync("/Customers");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("could not load the customer directory", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers/phone%3A" + Phone + "\"", html, StringComparison.Ordinal);
        Assert.Contains("TG-CS-00440", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomerProfile_OpenedFromATicket_RendersTheProfile_AndLeadsBackToThatTicket()
    {
        var response = await Client().GetAsync(ProfilePath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("This customer profile could not be loaded", html, StringComparison.Ordinal);
        Assert.Contains("Unverified &#xB7; phone only", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets/440\">← Back to TG-CS-00440</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customers_WhenTheApiReadFails_SaysSo_NeverAnEmptyDirectory_AndLogsTheFailedCall()
    {
        _customersApiFails = true;

        var response = await Client().GetAsync("/Customers");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("could not load the customer directory", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No customers yet", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No customers match", html, StringComparison.Ordinal);
        Assert.Contains(_logs, l => l.Contains("TigerCS.Api call failed", StringComparison.Ordinal) && l.Contains("api/customers", StringComparison.Ordinal) && l.Contains("500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CustomerProfile_WhenTheApiReadFails_SaysSo_NeverAFabricatedProfile_AndLogsTheFailedCall()
    {
        _customersApiFails = true;

        var response = await Client().GetAsync(ProfilePath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("This customer profile could not be loaded.", html, StringComparison.Ordinal);
        Assert.Contains("Back to Customers", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Unnamed caller", html, StringComparison.Ordinal);
        Assert.Contains(_logs, l => l.Contains("TigerCS.Api call failed", StringComparison.Ordinal) && l.Contains("api/customers/profile", StringComparison.Ordinal) && l.Contains("500", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue($"{logLevel}: {formatter(state, exception)}");
        }
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Agent"),
                new Claim(ClaimTypes.Role, Roles.CsAgent),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    /// <summary>One phone-only caller whose latest ticket is 440; the customers reads can be switched to a server error.</summary>
    private sealed class FakeApi(Func<bool> customersApiFails) : HttpMessageHandler
    {
        private static readonly CustomerDirectoryRowDto Caller = new(
            "phone:" + Phone, "Phone", null, "+" + Phone, "Marina Heights", "M-401", "Unverified", 1, 1, 440, "TG-CS-00440", "Open", Now.AddDays(-1), null);

        private static readonly CustomerDirectoryProfileDto CallerProfile = new(
            "phone:" + Phone, "Phone", null, ["+" + Phone], [], "Unverified", null, null, null, 1, 1, Now.AddDays(-1), Now.AddDays(-1), 440,
            [new CustomerDirectoryUnitDto("Marina Heights", "M-401", "Manual", 1, Now.AddDays(-1), null, null)],
            [new CustomerDirectoryTicketDto(440, "TG-CS-00440", "Open", 4, 2, Now.AddDays(-1), "Marina Heights", "M-401", "Noise complaint", Now.AddDays(-1), null, "Unverified")],
            []);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);

            if (path.StartsWith("/api/customers", StringComparison.Ordinal) && customersApiFails())
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = JsonContent.Create(new { title = "An error occurred while processing your request.", status = 500 }),
                });
            }

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Agent", [Roles.CsAgent], [new DepartmentMembershipDto(2, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service") },
                "/api/customers" => new CustomerDirectoryListResultDto([Caller], 1, 1, 25),
                "/api/customers/profile/phone:" + Phone => CallerProfile,
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }
    }
}
