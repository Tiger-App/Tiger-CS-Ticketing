using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Ticket 187: a chat ticket was closed for customer inactivity at 08:26 while a human agent replied in the same conversation at 09:05.
/// The inactivity closer checked owner, handler, open handoff and the human-follow-up flag — but not whether a person had actually spoken.
/// These tests pin the guard: a HumanAgent line stops both clocks at once, the job re-checks for one at fire time (so a later customer
/// message cannot restart a clock that would then close a conversation a person is working), and a reply that reaches an already-Closed
/// ticket leaves forward-compatible audit evidence without reopening or rewriting anything.
/// </summary>
public class HumanActiveInactivityGuardTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Phone = "+971500000001";

    private static async Task<(GenesysServiceFixture F, long TicketId, string Conv)> IngestAsync(string conv = "conv-ig")
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conv, GenesysChannel.WebsiteChat, CustomerPhone: Phone, DepartmentId: department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, result.Ticket!.TicketId, conv);
    }

    private static Task<GenesysTranscriptAppendResult> SayAsync(GenesysServiceFixture f, long ticketId, string conv, string sender, string body, string id) =>
        f.ConversationEnd.AppendTranscriptAsync(
            ServiceAccount, conv, [new GenesysTranscriptMessageDto(sender, DateTime.UtcNow, body, SenderName: sender, ExternalMessageId: id)], ticketId: ticketId);

    private static async Task<ChatbotInactivityCloseOutcome> RunJobAsync(GenesysServiceFixture f)
    {
        var outcome = ChatbotInactivityCloseOutcome.NotDue;
        foreach (var id in await f.InactivityClose.ListDueAsync())
        {
            outcome = await f.InactivityClose.TryCloseAsync(id);
        }

        return outcome;
    }

    [Fact]
    public async Task Control_WithoutAHumanLine_TheSilentChatStillClosesAfterTenMinutes()
    {
        var (f, _, _) = await IngestAsync();

        f.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await RunJobAsync(f));
        Assert.Equal(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task AVirtualAgentLine_DoesNotStopTheClock()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await SayAsync(f, ticketId, conv, "VirtualAgent", "How can I help?", "m1");

        f.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await RunJobAsync(f));
    }

    [Fact]
    public async Task AHumanAgentLine_StopsTheRunningClock_SoNothingIsDue()
    {
        var (f, ticketId, conv) = await IngestAsync();
        Assert.NotNull(f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);

        var stored = await SayAsync(f, ticketId, conv, "HumanAgent", "Hi, this is Kawther.", "m1");
        Assert.Equal(GenesysTranscriptAppendOutcome.Appended, stored.Outcome);

        Assert.Null(f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);
        f.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await RunJobAsync(f));
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task ACustomerMessageAfterTheHumanLine_RestartsTheClock_ButTheJobStandsDownAndRecordsWhy()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await SayAsync(f, ticketId, conv, "HumanAgent", "Hi, this is Kawther.", "m1");

        // The customer answers; the clock starts again (no owner, no handoff — nothing else says a person is here).
        var reported = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, CustomerActive: true));
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, reported.Outcome);
        Assert.NotNull(f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);

        f.Clock.Advance(TimeSpan.FromMinutes(11));
        var outcome = await RunJobAsync(f);

        Assert.Equal(ChatbotInactivityCloseOutcome.Excluded, outcome);
        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
        Assert.Empty(f.Resolutions.Added);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");
        var skipped = Assert.Single(f.Audit.Entries, e => e.Action == "ChatbotInactivityCloseSkippedHumanActive");
        Assert.Contains("Reason=HumanAgentActive", skipped.AfterValue);
        Assert.Contains($"TicketId={ticketId}", skipped.AfterValue);
        Assert.True(f.InteractionFor(conv)!.RequiresHumanFollowUp);
        Assert.Null(f.InteractionFor(conv)!.LastCustomerInteractionAtUtc);
        Assert.Null(f.InteractionFor(conv)!.InactivityClosedAtUtc);
    }

    [Fact]
    public async Task AHumanReplyReachingAClosedTicket_IsAuditedAsHumanActivityAfterClosure_WithoutReopeningOrRewriting()
    {
        var (f, ticketId, conv) = await IngestAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await RunJobAsync(f));
        var closedBefore = f.StatusHistory.Added.Count;
        var resolutionsBefore = f.Resolutions.Added.Count;

        var stored = await SayAsync(f, ticketId, conv, "HumanAgent", "Sorry for the wait.", "m9");

        Assert.Equal(GenesysTranscriptAppendOutcome.Appended, stored.Outcome); // the reply is still kept in the transcript
        var ticket = f.Tickets.All.Single();
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);                // nothing reopened
        Assert.Equal(closedBefore, f.StatusHistory.Added.Count);               // no history written or rewritten
        Assert.Equal(resolutionsBefore, f.Resolutions.Added.Count);

        var evidence = Assert.Single(f.Audit.Entries, e => e.Action == "TicketHumanActivityAfterClosure");
        Assert.Equal("Ticket", evidence.EntityType);
        Assert.Equal(ticketId.ToString(), evidence.EntityId);
        Assert.Contains($"TicketId={ticketId}", evidence.AfterValue);
        Assert.Contains("HumanMessages=1", evidence.AfterValue);
        Assert.Contains("TicketStatus=Closed", evidence.AfterValue);
    }

    [Fact]
    public async Task ACustomerLineOnAClosedTicket_LeavesNoHumanActivityEvidence()
    {
        var (f, ticketId, conv) = await IngestAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(11));
        await RunJobAsync(f);

        await SayAsync(f, ticketId, conv, "Customer", "Hello?", "m2");

        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "TicketHumanActivityAfterClosure");
    }
}
