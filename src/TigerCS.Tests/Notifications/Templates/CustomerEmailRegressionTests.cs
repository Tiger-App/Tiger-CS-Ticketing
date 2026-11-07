using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.Notifications.Templates;

namespace TigerCS.Tests.Notifications.Templates;

/// <summary>Regression: placeholder customer names, UAE time and the logo URL in every customer-facing email.</summary>
public class CustomerEmailRegressionTests
{
    // 13:40 UTC is 17:40 in the UAE (UTC+4).
    private static readonly DateTime When = new(2026, 10, 22, 13, 40, 0, DateTimeKind.Utc);

    private static IEnumerable<(string Name, Func<CustomerEmailTicketModel, CustomerEmailContent> Render)> Templates() =>
    [
        ("created", CustomerEmailTemplates.TicketCreated),
        ("resolved", CustomerEmailTemplates.TicketResolved),
        ("closed", CustomerEmailTemplates.TicketClosed),
        ("reopened", CustomerEmailTemplates.TicketReopened),
        ("assigned", CustomerEmailTemplates.TicketAssigned),
        ("in-progress", CustomerEmailTemplates.TicketInProgress),
        ("waiting", CustomerEmailTemplates.TicketWaitingForCustomer),
        ("cancelled", CustomerEmailTemplates.TicketCancelled)
    ];

    public static TheoryData<string?> InvalidNames => new()
    {
        null, "", "   ", "NOT_REGISTERED", "not_registered", "Not Registered", "NOT-REGISTERED", "UNKNOWN", "unknown", "N/A", "n/a", "NA", "N.A.",
        "NULL", "None", "-", "---", "123", "UNREGISTERED", "Customer", "TBD"
    };

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void InvalidCustomerName_GreetsGenerically_InEveryTemplate_English(string? name)
    {
        foreach (var (template, render) in Templates())
        {
            var content = render(new CustomerEmailTicketModel("TG-1", name, When));

            Assert.Contains("Dear Customer,", content.TextBody, StringComparison.Ordinal);
            Assert.Contains("Dear Customer,", content.HtmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain("NOT_REGISTERED", content.HtmlBody + content.TextBody, StringComparison.OrdinalIgnoreCase);
            Assert.True(content.HtmlBody.Contains(">Dear Customer,<", StringComparison.Ordinal), template);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void InvalidCustomerName_GreetsGenerically_InEveryTemplate_Arabic(string? name)
    {
        foreach (var (_, render) in Templates())
        {
            var content = render(new CustomerEmailTicketModel("TG-1", name, When, Language: "ar"));

            Assert.Contains("عزيزنا العميل،", content.TextBody, StringComparison.Ordinal);
            Assert.Contains("عزيزنا العميل،", content.HtmlBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidName_IsUsed_AndTrimmed_InEveryTemplate()
    {
        foreach (var (_, render) in Templates())
        {
            Assert.Contains("Dear Ahmed Al-Farsi,", render(new CustomerEmailTicketModel("TG-1", "  Ahmed Al-Farsi ", When)).TextBody, StringComparison.Ordinal);
            Assert.Contains("عزيزي/عزيزتي أحمد،", render(new CustomerEmailTicketModel("TG-1", "أحمد", When, Language: "ar")).TextBody, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Not Registered Khan", true)]
    [InlineData("Ahmed", true)]
    [InlineData("أحمد", true)]
    [InlineData("NOT_REGISTERED", false)]
    [InlineData("N/A", false)]
    [InlineData(null, false)]
    public void NameValidator_RejectsPlaceholders_AcceptsRealNames(string? name, bool valid) =>
        Assert.Equal(valid, CustomerNameValidator.IsValid(name));

    [Fact]
    public async Task Resolver_SkipsAPlaceholderName_AndFallsThroughToTheNextRealOne()
    {
        var f = new Fakes.NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync("a@example.com", customerName: "NOT_REGISTERED");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        // The snapshot-less Genesys ticket has only the placeholder: no name at all, never the placeholder.
        Assert.Null(contact.DisplayName);
        Assert.Equal("en", contact.Language);
    }

    [Fact]
    public void Dates_AreUaeTime_NeverUtc_InEveryTemplate()
    {
        foreach (var (_, render) in Templates())
        {
            var content = render(new CustomerEmailTicketModel("TG-1", "Ahmed", When));
            var all = content.HtmlBody + content.TextBody;

            Assert.Contains("22 October 2026, 17:40 GST (UTC+4)", all, StringComparison.Ordinal);
            Assert.DoesNotContain("13:40", all, StringComparison.Ordinal);
            Assert.DoesNotContain(", 17:40 UTC", all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Dates_CrossMidnightIntoTheNextUaeDay()
    {
        var late = new DateTime(2026, 10, 22, 21, 15, 0, DateTimeKind.Utc);

        var content = CustomerEmailTemplates.TicketCreated(new CustomerEmailTicketModel("TG-1", "A", late));

        Assert.Contains("23 October 2026, 01:15 GST (UTC+4)", content.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/img/tiger-logo.png")]
    [InlineData("img/tiger-logo.png")]
    [InlineData("http://www.tigergroup.ae/logos/TigerHead.jpg")]
    [InlineData("https://localhost:7219/img/tiger-logo.png")]
    [InlineData("https://127.0.0.1/logo.png")]
    [InlineData("https://intranet/logo.png")]
    [InlineData("file:///C:/logo.png")]
    public void ConfiguredPolicy_NeverUsesARelativeLocalOrInsecureLogo(string? configured)
    {
        var policy = new CustomerNotificationModePolicy(
            "true", "t@example.com", null, "https://localhost:7219", null, new CustomerEmailBranding(configured, "https://www.tigergroup.ae", null, null));

        Assert.Equal("https://www.tigergroup.ae/logos/TigerHead.jpg", policy.Branding.LogoUrl);
    }

    [Fact]
    public void ConfiguredPolicy_KeepsAValidPublicHttpsLogo()
    {
        var policy = new CustomerNotificationModePolicy(
            "true", "t@example.com", null, null, null,
            new CustomerEmailBranding("https://www.tigergroup.ae/logos/TigerHead.jpg", "https://www.tigergroup.ae", null, null));

        Assert.Equal("https://www.tigergroup.ae/logos/TigerHead.jpg", policy.Branding.LogoUrl);
    }

    [Fact]
    public void EveryTemplate_RendersTheExactOfficialLogoUrl_AndNoLocalPaths()
    {
        var branding = new CustomerNotificationModePolicy("true", "t@example.com", null, "https://localhost:7219").Branding;

        foreach (var (_, render) in Templates())
        {
            var html = render(new CustomerEmailTicketModel("TG-1", "Ahmed", When, Branding: branding, IsTest: true)).HtmlBody;

            Assert.Contains("<img src=\"https://www.tigergroup.ae/logos/TigerHead.jpg\" alt=\"Tiger Properties\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("localhost", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/img/tiger-logo.png", html, StringComparison.Ordinal);
            Assert.DoesNotContain("src=\"/", html, StringComparison.Ordinal);
            Assert.DoesNotContain("src=\"cid:", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ModelWithAnUnsafeLogo_RendersTheBrandName_NotABrokenImage()
    {
        var html = CustomerEmailTemplates.TicketClosed(new CustomerEmailTicketModel(
            "TG-1", "A", When, Branding: new CustomerEmailBranding("https://localhost/x.png", "https://www.tigergroup.ae", null, null))).HtmlBody;

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedConfiguration_UsesTheOfficialLogo()
    {
        foreach (var file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TigerCS.Api", file);
            var json = File.ReadAllText(path);
            Assert.Contains("\"LogoUrl\": \"https://www.tigergroup.ae/logos/TigerHead.jpg\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("/img/tiger-logo.png", json, StringComparison.Ordinal);
        }
    }
}
