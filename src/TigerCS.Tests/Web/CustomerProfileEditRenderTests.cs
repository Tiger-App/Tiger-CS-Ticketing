// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

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
/// The Customer Profile's Edit Customer button, property forms and Change History tab, rendered through the real Razor pipeline against a
/// fake Api: who sees what. The history holds personal data, so a viewer without <c>Customers.ViewHistory</c> must not get the tab - and
/// not the values either; a viewer without <c>Customers.Edit</c> gets no button and no form but still sees the properties and the
/// Verified / Manually added indicators.
/// </summary>
public sealed class CustomerProfileEditRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private HttpClient Client(string role)
    {
        var factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FakeApi(role)));
                services.AddSingleton(new TestRole(role));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                });
            });
        });
        _disposables.Add(factory);
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    private async Task<string> ProfileHtmlAsync(string role, string query = "")
    {
        var response = await Client(role).GetAsync("/Customers/crm%3A9001" + query);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{html[..Math.Min(html.Length, 1500)]}");
        return html;
    }

    // ------------------------------------------------------------------------------------------------ edit

    [Fact]
    public async Task AnEditor_SeesTheEditCustomerButton_AndTheFormWithEveryField()
    {
        var html = await ProfileHtmlAsync(Roles.CsManager, "?edit=1");

        Assert.Contains("data-edit-customer-open", html, StringComparison.Ordinal);
        Assert.Contains(">\n                        <svg", html.Replace("\r", ""), StringComparison.Ordinal);
        Assert.Contains("Edit Customer", html, StringComparison.Ordinal);
        Assert.Contains("data-edit-customer", html, StringComparison.Ordinal);
        Assert.Contains("<details class=\"panel edit-customer\" id=\"edit-customer\" open=\"open\"", html, StringComparison.Ordinal);   // ?edit=1 opens it
        foreach (var field in new[]
                 {
                     "name=\"fullName\"", "name=\"phoneNumber\"", "name=\"landlineNumber\"", "name=\"additionalPhones\"", "name=\"email\"",
                     "name=\"nationality\"", "name=\"country\"", "name=\"emirate\"", "name=\"location\"", "name=\"preferredLanguage\"", "name=\"reason\"",
                 })
        {
            Assert.Contains(field, html, StringComparison.Ordinal);
        }

        // Pre-filled with what the customer is shown with, so saving without a change changes nothing.
        Assert.Contains("value=\"Mariam Al Falasi\"", html, StringComparison.Ordinal);
        Assert.Contains(Enc("+97141234567"), html, StringComparison.Ordinal);   // the saved landline
        Assert.Contains("<option value=\"Dubai\" selected=\"selected\">Dubai</option>", html, StringComparison.Ordinal);
        Assert.Contains("Nothing is sent to Tiger CRM, PACT or Tasleeh", html, StringComparison.Ordinal);   // CRM-owned data is respected, and said so
        Assert.Contains("name=\"operationId\"", html, StringComparison.Ordinal);                               // double submits record once
    }

    [Fact]
    public async Task TheEditFormIsClosedUntilAsked_AndTheDetailsAreShownOnTheContactTab()
    {
        var html = await ProfileHtmlAsync(Roles.CsManager);

        Assert.DoesNotContain("id=\"edit-customer\" open", html, StringComparison.Ordinal);
        Assert.Contains("data-profile-details", html, StringComparison.Ordinal);
        Assert.Contains("data-detail=\"landline\">" + Enc("+97141234567") + "<", html, StringComparison.Ordinal);
        Assert.Contains("data-detail=\"nationality\">Jordanian<", html, StringComparison.Ordinal);
        Assert.Contains("data-detail=\"emirate\">Dubai<", html, StringComparison.Ordinal);
        Assert.Contains("data-detail=\"language\">Arabic<", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AViewerWithoutTheEditPermission_GetsNoEditButtonAndNoForms_ButStillSeesThePropertiesWithTheirIndicators()
    {
        var html = await ProfileHtmlAsync(Roles.CsAgent, "?edit=1");   // asking for the form does not open it

        Assert.DoesNotContain("data-edit-customer", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit Customer", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"fullName\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-add-property", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirm remove", html, StringComparison.Ordinal);

        Assert.Contains("data-manual-properties-table", html, StringComparison.Ordinal);
        Assert.Contains("Marina Heights", html, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ properties

    [Fact]
    public async Task VerifiedAndManuallyAddedPropertiesAreToldApart_AndAManualOneIsNeverShownAsVerified()
    {
        var html = await ProfileHtmlAsync(Roles.CsManager);

        // The unit from CRM / the verifying source is Verified; one typed in on a ticket is Manual entry.
        Assert.Contains("data-unit-status=\"verified\"", html, StringComparison.Ordinal);
        Assert.Contains("data-unit-status=\"manual\"", html, StringComparison.Ordinal);

        // The property added in Tiger Ticketing is only ever "Manually added", with its relationship, and says so in words.
        var manual = html[html.IndexOf("data-manual-properties-table", StringComparison.Ordinal)..];
        manual = manual[..manual.IndexOf("</table>", StringComparison.Ordinal)];
        Assert.Contains("Marina Heights", manual, StringComparison.Ordinal);
        Assert.Contains("Tenant", manual, StringComparison.Ordinal);
        Assert.Contains("data-property-status=\"manual\">Manually added", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("Verified", manual, StringComparison.Ordinal);
        Assert.Contains("not verified ownership", html, StringComparison.Ordinal);

        // An editor can add, edit and remove it - and only it.
        Assert.Contains("data-add-property", html, StringComparison.Ordinal);
        Assert.Contains("name=\"relationship\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Owner\">Owner</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Representative\">Representative</option>", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, "Confirm remove"));   // one manual property, one remove: the verified units have none
    }

    // ------------------------------------------------------------------------------------------------ history

    [Fact]
    public async Task TheChangeHistoryTab_ShowsTheChangedFieldOldValueNewValueAndWhoModifiedIt_ToAViewerWithThePermission()
    {
        var html = await ProfileHtmlAsync(Roles.CsManager, "?tab=history");

        Assert.Contains("id=\"tab-changes\"", html, StringComparison.Ordinal);
        Assert.Contains("Change History", html, StringComparison.Ordinal);
        Assert.Contains("id=\"tab-changes\" class=\"tab-input\" checked", html, StringComparison.Ordinal);   // ?tab=history opens it

        var panel = html[html.IndexOf("id=\"panel-changes\"", StringComparison.Ordinal)..];
        Assert.Contains("<th>Changed field</th><th>Old value</th><th>New value</th>", panel, StringComparison.Ordinal);
        Assert.Contains("data-change-field=\"FullName\"", panel, StringComparison.Ordinal);
        Assert.Contains("Full name", panel, StringComparison.Ordinal);
        Assert.Contains("data-old-value>Mariam Falasi<", panel, StringComparison.Ordinal);
        Assert.Contains("data-new-value>Mariam Al Falasi<", panel, StringComparison.Ordinal);
        Assert.Contains("data-modified-by>Hala Manager<", panel, StringComparison.Ordinal);
        Assert.Contains("(UI)", panel, StringComparison.Ordinal);                                   // modification source
        Assert.Contains("Reason: Customer sent a corrected ID", panel, StringComparison.Ordinal);   // change reason
        Assert.Contains("href=\"/Tickets/6\"", panel, StringComparison.Ordinal);                     // related ticket
        Assert.Contains("data-old-value>" + Enc("—") + "<", panel, StringComparison.Ordinal);                      // an empty old value is shown as a dash
        Assert.Equal(1, Count(panel, "data-change-operation"));                                      // two fields, ONE operation: grouped by correlation id
        Assert.Equal(2, Count(panel, "data-change-field="));
    }

    [Fact]
    public async Task AViewerWithoutTheHistoryPermission_GetsNeitherTheTabNorAnyOfItsValues()
    {
        foreach (var role in new[] { Roles.CsAgent, Roles.CsSupervisor, Roles.GeneralManager })
        {
            var html = await ProfileHtmlAsync(role, "?tab=history");

            Assert.DoesNotContain("tab-changes", html, StringComparison.Ordinal);
            Assert.DoesNotContain("panel-changes", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Change History", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Mariam Falasi", html, StringComparison.Ordinal);    // the history's old value must not reach the page
            Assert.DoesNotContain("Hala Manager", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Customer sent a corrected ID", html, StringComparison.Ordinal);
        }
    }

    /// <summary>The way Razor writes a value into HTML ("+" and "—" become entities).</summary>
    private static string Enc(string value) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(value);

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // ------------------------------------------------------------------------------------------------ fakes

    private sealed record TestRole(string Role);

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TestRole role)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Viewer"),
                new Claim(ClaimTypes.Role, role.Role),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    /// <summary>Enough of TigerCS.Api for the profile page: a CRM customer with a verified unit, a ticket-entered unit, a manual property, saved details and one change operation.</summary>
    private sealed class FakeApi(string role) : HttpMessageHandler
    {
        private static readonly CustomerDirectoryProfileDto Profile = new(
            "crm:9001", "Crm", "Mariam Al Falasi", ["+971501234567"], ["mariam@example.test"], "Crm", 9001, null, null, 1, 2,
            Now.AddDays(-10), Now.AddDays(-1), 6,
            [
                new CustomerDirectoryUnitDto("Tiger Tower", "T-1310", "Crm", 1, Now.AddDays(-1), 42, null),
                new CustomerDirectoryUnitDto("Palm Residence", "P-08", "Manual", 1, Now.AddDays(-10), null, null),
            ],
            [new CustomerDirectoryTicketDto(6, "TG-CS-00006", "InProgress", 2, 2, Now.AddDays(-1), "Tiger Tower", "T-1310", "Leak under the sink", Now.AddHours(-2), null, "Verified")],
            [],
            new CustomerProfileEditStateDto("+971501234567", "Tiger Tower", "T-1310", null, null, null, null),
            Guid.NewGuid(),
            new CustomerProfileDetailsDto(
                "Mariam Al Falasi", "mariam@example.test", "+97141234567", [], "Jordanian", "United Arab Emirates", "Dubai", "Business Bay", "Arabic",
                ["FullName"], Now.AddHours(-5), "Mariam Al Falasi", "mariam@example.test"),
            [new CustomerPropertyDto(11, "Marina Heights", "M-401", "Tenant", false, Now.AddHours(-4))]);

        private static readonly Guid Operation = Guid.NewGuid();

        private static readonly CustomerChangeHistoryDto History = new(
            [
                new CustomerChangeDto(2, "Email", "Email address", null, "mariam@example.test", Guid.NewGuid(), "Hala Manager", Now.AddHours(-5), "UI", "Customer sent a corrected ID", 6, Operation),
                new CustomerChangeDto(1, "FullName", "Full name", "Mariam Falasi", "Mariam Al Falasi", Guid.NewGuid(), "Hala Manager", Now.AddHours(-5), "UI", "Customer sent a corrected ID", 6, Operation),
            ],
            2, 1, 25);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var canViewHistory = role is Roles.CsManager or Roles.SystemAdministrator;

            if (path.EndsWith("/history", StringComparison.Ordinal) && !canViewHistory)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));   // the Api refuses; the page must not even ask
            }

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Viewer", [role], [new DepartmentMembershipDto(2, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service") },
                "/api/channels" => new[] { new ChannelDto(3, "WhatsApp", "WHATSAPP", true, true, true, 2) },
                "/api/projects" => new[] { new ProjectOptionDto("Marina Heights", null), new ProjectOptionDto("Tiger Tower", null) },
                "/api/customers/profile/crm:9001" => Profile,
                "/api/customers/profile/crm:9001/history" => History,
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }
    }
}
