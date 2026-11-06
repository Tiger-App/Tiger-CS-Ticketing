using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Templates;

namespace TigerCS.Tests.Notifications.Templates;

/// <summary>The assigned / in-progress / waiting / cancelled templates and the enriched closed, created and reopened ones, in both languages.</summary>
public class LifecycleEmailTemplatesTests
{
    private static readonly DateTime When = new(2026, 11, 6, 6, 30, 0, DateTimeKind.Utc); // 10:30 Dubai

    private static CustomerEmailTicketModel Model(string language = "en", string? name = "Ahmed Al-Farsi") => new(
        "TG-CS-20261106-0042", name, When, TicketSubject: "Maintenance Request", Language: language, TicketStatus: "Open");

    public static TheoryData<string, Func<CustomerEmailTicketModel, CustomerEmailContent>, string, string> Templates => new()
    {
        { "assigned", CustomerEmailTemplates.TicketAssigned, "Your request has been assigned – Ticket TG-CS-20261106-0042", "Status: Open" },
        { "in-progress", CustomerEmailTemplates.TicketInProgress, "We are working on your request – Ticket TG-CS-20261106-0042", "Status: In progress" },
        { "waiting", CustomerEmailTemplates.TicketWaitingForCustomer, "We need your input – Ticket TG-CS-20261106-0042", "Status: Waiting for your reply" },
        { "cancelled", CustomerEmailTemplates.TicketCancelled, "Your request has been cancelled – Ticket TG-CS-20261106-0042", "Status: Cancelled" }
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryNewTemplate_CarriesNameNumberSubjectStatusAndDate(
        string name, Func<CustomerEmailTicketModel, CustomerEmailContent> render, string subject, string status)
    {
        var content = render(Model());

        Assert.Equal(subject, content.Subject);
        Assert.Contains("Dear Ahmed Al-Farsi,", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Ticket number: TG-CS-20261106-0042", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Subject: Maintenance Request", content.TextBody, StringComparison.Ordinal);
        Assert.Contains(status, content.TextBody, StringComparison.Ordinal);
        Assert.Contains("06 November 2026, 10:30 GST (UTC+4)", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Tiger Customer Service Team", content.TextBody, StringComparison.Ordinal);
        Assert.Contains(">TG-CS-20261106-0042<", content.HtmlBody, StringComparison.Ordinal);
        Assert.True(content.HtmlBody.Contains("lang=\"en\"", StringComparison.Ordinal), name);
        // Never a review request on a non-terminal or cancelled email.
        Assert.DoesNotContain("How was your experience?", content.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryNewTemplate_RendersArabicRightToLeft(
        string name, Func<CustomerEmailTicketModel, CustomerEmailContent> render, string subject, string status)
    {
        _ = subject;
        _ = status;
        var content = render(Model("ar", "أحمد الفارسي"));

        Assert.Contains("عزيزي/عزيزتي أحمد الفارسي،", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("رقم التذكرة: TG-CS-20261106-0042", content.TextBody, StringComparison.Ordinal);
        Assert.True(content.HtmlBody.Contains("dir=\"rtl\"", StringComparison.Ordinal), name);
        Assert.Contains("فريق خدمة عملاء Tiger", content.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Dear", content.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryNewTemplate_EncodesDynamicValues(
        string name, Func<CustomerEmailTicketModel, CustomerEmailContent> render, string subject, string status)
    {
        _ = subject;
        _ = status;
        var content = render(Model() with { CustomerName = "<b>Ahmed</b>", TicketSubject = "<script>alert(1)</script>", PendingReason = "<img src=x>" });

        Assert.DoesNotContain("<script>", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Ahmed</b>", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x>", content.HtmlBody, StringComparison.Ordinal);
        Assert.True(content.HtmlBody.Contains("&lt;script&gt;", StringComparison.Ordinal), name);
    }

    [Fact]
    public void Waiting_QuotesTheReasonOnlyWhenGiven()
    {
        var without = CustomerEmailTemplates.TicketWaitingForCustomer(Model());
        var with = CustomerEmailTemplates.TicketWaitingForCustomer(Model() with { PendingReason = "Please send the signed form" });

        Assert.DoesNotContain("What we need from you", without.TextBody, StringComparison.Ordinal);
        Assert.Contains("What we need from you: Please send the signed form", with.TextBody, StringComparison.Ordinal);
        Assert.Contains("ما نحتاجه منك: نموذج", CustomerEmailTemplates.TicketWaitingForCustomer(Model("ar") with { PendingReason = "نموذج" }).TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void InProgress_AfterWaiting_ThanksForTheReply()
    {
        var resumed = CustomerEmailTemplates.TicketInProgress(Model() with { ResumedAfterWaiting = true });

        Assert.Contains("Thank you for your reply. Our team has resumed work", resumed.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Closed_CarriesTheOutcome_AndSaysWhyWhenClosedForInactivity()
    {
        var resolved = CustomerEmailTemplates.TicketClosed(Model() with { ResolutionStatus = "Resolved" });
        var inactive = CustomerEmailTemplates.TicketClosed(Model() with { ResolutionStatus = "Cancelled", ClosedForCustomerInactivity = true });
        var arabic = CustomerEmailTemplates.TicketClosed(Model("ar") with { ResolutionStatus = "Cancelled", ClosedForCustomerInactivity = true });

        Assert.Contains("Status: Closed", resolved.TextBody, StringComparison.Ordinal);
        Assert.Contains("Outcome: Resolved", resolved.TextBody, StringComparison.Ordinal);
        Assert.Contains("Thank you for giving us the opportunity", resolved.TextBody, StringComparison.Ordinal);
        Assert.Contains("We did not receive a reply from you", inactive.TextBody, StringComparison.Ordinal);
        Assert.Contains("Outcome: Cancelled", inactive.TextBody, StringComparison.Ordinal);
        Assert.Contains("لم نتلقَّ رداً منك", arabic.TextBody, StringComparison.Ordinal);
        Assert.Contains("النتيجة: ملغاة", arabic.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Created_AddsTheSubjectOnlyWhenItSaysMoreThanTheRequestType()
    {
        var same = CustomerEmailTemplates.TicketCreated(Model() with { RequestTypeName = "Maintenance Request" });
        var different = CustomerEmailTemplates.TicketCreated(Model() with { RequestTypeName = "Maintenance", TicketSubject = "AC not cooling" });

        Assert.Equal(1, same.TextBody.Split("Maintenance Request").Length - 1);
        Assert.Contains("Request type: Maintenance", different.TextBody, StringComparison.Ordinal);
        Assert.Contains("Subject: AC not cooling", different.TextBody, StringComparison.Ordinal);
        Assert.Contains("Status: Open", different.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Reopened_CarriesTheSubjectAndTheWorkingStatus()
    {
        var content = CustomerEmailTemplates.TicketReopened(Model());

        Assert.Contains("Subject: Maintenance Request", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Status: In progress", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("الحالة: قيد المعالجة", CustomerEmailTemplates.TicketReopened(Model("ar")).TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalTemplates_RenderTheLinkOnlyWhenAbsolute_AndNeverCustomerContactDetails()
    {
        var model = new InternalTicketEmailModel(
            "TG-CS-20261106-0042", "Sara Ali", "InProgress", When, TicketSubject: "Maintenance Request", CustomerName: "Ahmed Al-Farsi",
            DepartmentName: "Facilities", TicketUrl: "https://tickets.example.com/Tickets/42", ActorName: "Mona <Supervisor>", MessageCount: 3);

        var assigned = InternalEmailTemplates.TicketAssignedToYou(model);
        var reply = InternalEmailTemplates.CustomerReplyReceived(model);
        var noLink = InternalEmailTemplates.TicketAssignedToYou(model with { TicketUrl = "javascript:alert(1)" });

        Assert.Equal("Ticket TG-CS-20261106-0042 assigned to you", assigned.Subject);
        Assert.Contains("assigned to you by Mona <Supervisor>", assigned.TextBody, StringComparison.Ordinal);
        Assert.Contains("Mona &lt;Supervisor&gt;", assigned.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Open the ticket: https://tickets.example.com/Tickets/42", assigned.TextBody, StringComparison.Ordinal);
        Assert.Contains("Department: Facilities", assigned.TextBody, StringComparison.Ordinal);
        Assert.Equal("Customer replied on ticket TG-CS-20261106-0042", reply.Subject);
        Assert.Contains("3 new messages", reply.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", noLink.HtmlBody + noLink.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Open the ticket", noLink.TextBody, StringComparison.Ordinal);
    }
}
