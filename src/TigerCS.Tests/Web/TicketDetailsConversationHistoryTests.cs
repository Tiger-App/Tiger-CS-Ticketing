// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Runtime.CompilerServices;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details' Conversation History: the calls and chats a ticket came from, shown in the Overview (below Request Details) as a
/// chat window instead of in a separate tab. Presentation only — the underlying read model is covered by
/// <c>GenesysConversationEndAppServiceTests</c>; the rendered page by <c>TicketDetailsOverviewRenderTests</c>.
/// </summary>
public sealed class TicketDetailsConversationHistoryTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string TicketDetailsViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml")));

    private static string ConversationPartial() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "Shared", "_TicketConversationHistory.cshtml")));

    private static string TicketDetailsModelSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml.cs")));

    private static string SiteCss() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "css", "site.css")));

    // ---- where it lives ----

    [Fact]
    public void TheInteractionsTab_IsGone_AndTheConversationIsPartOfTheOverview()
    {
        var html = TicketDetailsViewHtml();

        Assert.DoesNotContain("id=\"tab-conversations\"", html);
        Assert.DoesNotContain("id=\"panel-conversations\"", html);
        Assert.DoesNotContain(">Interactions", html);
        Assert.DoesNotContain("tab-conversations:checked", SiteCss());
        Assert.Contains("Html.PartialAsync(\"_TicketConversationHistory\", Model)", html);
    }

    [Fact]
    public void TheOverview_FollowsTheWorkspaceOrder()
    {
        var html = TicketDetailsViewHtml();

        int At(string marker) => html.IndexOf(marker, StringComparison.Ordinal);
        var order = new[]
        {
            At("class=\"ticket-header\""),
            At("What needs your attention?"),
            At("_TicketCustomerCard"),
            At("<div class=\"facts-section__title\">Request Details</div>"),
            At("_TicketConversationHistory"),
            At("id=\"customer-satisfaction\""),
            At("<div class=\"facts-section__title\">Latest Activity</div>"),
        };

        Assert.All(order, position => Assert.True(position >= 0));
        Assert.Equal(order.OrderBy(p => p), order);
    }

    [Fact]
    public void TheQuickFactsSidebar_StaysOperationalOnly_AndDoesNotRepeatTheConversationOrSatisfaction()
    {
        var html = TicketDetailsViewHtml();
        var start = html.IndexOf("aria-label=\"Quick facts\"", StringComparison.Ordinal);
        var quickFacts = html[start..html.IndexOf("</aside>", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("Conversation", quickFacts);
        Assert.DoesNotContain("Satisfaction", quickFacts);
        Assert.DoesNotContain("review", quickFacts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomerSatisfaction_IsAlwaysVisible_NotInsideTheCollapsedReferenceDetails()
    {
        var html = TicketDetailsViewHtml();

        var satisfaction = html.IndexOf("id=\"customer-satisfaction\"", StringComparison.Ordinal);
        var collapsed = html.IndexOf("<details class=\"more-details\">", StringComparison.Ordinal);
        Assert.True(satisfaction > 0 && collapsed > satisfaction, "Customer Satisfaction must come before, and outside, the collapsed reference details.");

        // Rating, feedback, review date and the test indicator are all still rendered.
        foreach (var marker in new[] { "<dt>Rating</dt>", "<dt>Feedback</dt>", "<dt>Submitted</dt>", "Test review" })
        {
            Assert.Contains(marker, html);
        }
    }

    [Fact]
    public void Model_LoadsInteractions_ViaTicketsApiClient_AndNeverCallsGenesysDirectly()
    {
        var source = TicketDetailsModelSource();

        Assert.Contains("ticketsApiClient.GetInteractionsAsync", source);
        Assert.DoesNotContain("GenesysApiClient", source);
        Assert.DoesNotContain("api/genesys", source);
    }

    [Fact]
    public void TheView_NeverSortsOrDeduplicatesMessages()
    {
        var partial = ConversationPartial();
        var helper = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Models", "ConversationHistoryView.cs")));

        foreach (var source in new[] { partial, helper })
        {
            Assert.DoesNotContain("OrderBy", source);
            Assert.DoesNotContain("Distinct", source);
            Assert.DoesNotContain("GroupBy", source);
        }
    }

    [Fact]
    public void TheChatUi_HasNoNumberedTimelineMarkers_AndDistinguishesTheSides()
    {
        var partial = ConversationPartial();
        var css = SiteCss();

        Assert.DoesNotContain("timeline-icon", partial);
        Assert.DoesNotContain("<ol class=\"timeline\">", partial);
        Assert.Contains(".chat-msg--customer { justify-content: flex-end; }", css);
        Assert.Contains(".chat-msg--bot, .chat-msg--human { justify-content: flex-start; }", css);
        Assert.Contains(".chat-scroll {", css);
        Assert.Contains("overflow-y: auto", css);
        Assert.Contains("overflow-wrap: anywhere", css);
    }

    // ---- presentation helpers ----

    private static readonly DateTime Start = new(2026, 10, 22, 10, 5, 0, DateTimeKind.Utc); // 14:05 UAE

    private static TicketInteractionMessageDto Msg(int seq, string sender, string? name, DateTime at, string body) => new(seq, sender, name, at, body);

    private static TicketInteractionDto Interaction(
        IReadOnlyList<TicketInteractionMessageDto> messages, string? handling = "Ai", string? agent = null, DateTime? ended = null,
        AgentHandoffSummaryDto? handoff = null) => new(
        1, true, "Genesys", 3, "Website chat", "+971501234567", "Mariam", null, "Inbound", "conv-1", null, null, agent, null,
        Start, ended, ended is null ? null : "Customer disconnected", ended is null ? "Active" : "Ended", messages, handoff, handling);

    [Theory]
    [InlineData("Customer", ChatBubbleKind.Customer)]
    [InlineData("VirtualAgent", ChatBubbleKind.Bot)]
    [InlineData("HumanAgent", ChatBubbleKind.Human)]
    [InlineData("Agent", ChatBubbleKind.Human)]
    [InlineData("System", ChatBubbleKind.System)]
    [InlineData("SomethingNew", ChatBubbleKind.System)]
    public void EachSender_IsDrawnAsItsOwnKindOfLine(string sender, ChatBubbleKind expected) =>
        Assert.Equal(expected, ConversationHistoryView.KindOf(sender));

    [Fact]
    public void HumanAgentMessages_CarryTheAgentsNameAndAHumanLabel_TheBotIsLabelledAsAVirtualAgent()
    {
        Assert.Equal("Layla Hassan · Human agent", ConversationHistoryView.WhoLabel(Msg(1, "HumanAgent", "Layla Hassan", Start, "Hi")));
        Assert.Equal("Human agent", ConversationHistoryView.WhoLabel(Msg(1, "HumanAgent", null, Start, "Hi")));
        Assert.Equal("Virtual agent", ConversationHistoryView.WhoLabel(Msg(1, "VirtualAgent", null, Start, "Hi")));
        Assert.Equal("Customer", ConversationHistoryView.WhoLabel(Msg(1, "Customer", null, Start, "Hi")));
        Assert.Equal("Mariam", ConversationHistoryView.WhoLabel(Msg(1, "Customer", "Mariam", Start, "Hi")));
    }

    [Fact]
    public void MessageTimes_AreUaeTime_AndTheDayIsNamedOnlyWhenItChanges()
    {
        var late = new DateTime(2026, 10, 22, 21, 30, 0, DateTimeKind.Utc);   // 01:30 next day in UAE
        var interaction = Interaction(
        [
            Msg(1, "Customer", null, Start, "Hello"),
            Msg(2, "VirtualAgent", null, Start.AddMinutes(1), "Hi, how can I help?"),
            Msg(3, "Customer", null, late, "Still there?")
        ]);

        var lines = ConversationHistoryView.Lines(interaction);

        Assert.Equal(["14:05", "14:06", "01:30"], lines.Select(l => l.Time));
        Assert.Equal([null, null, "Fri 23 Oct 2026"], lines.Select(l => l.DateHeading));
    }

    [Fact]
    public void TheTranscript_IsKeptInTheOrderTheApiGaveIt_WithTheOriginalText()
    {
        var body = "Line one\n  indented — “quoted” & <b>not markup</b>";
        var interaction = Interaction(
        [
            Msg(1, "Customer", null, Start, body),
            Msg(2, "VirtualAgent", null, Start.AddMinutes(-5), "A message the provider stamped earlier"),
        ]);

        var lines = ConversationHistoryView.Lines(interaction);

        Assert.Equal([body, "A message the provider stamped earlier"], lines.Select(l => l.Body));
    }

    [Fact]
    public void RealDuplicateMessages_AreAllKept()
    {
        var interaction = Interaction(
        [
            Msg(1, "Customer", null, Start, "Hello"),
            Msg(2, "Customer", null, Start, "Hello"),                      // same sender, text and minute: two messages
            Msg(3, "VirtualAgent", null, Start.AddMinutes(1), "Hello"),   // same text, other sender
            Msg(4, "Customer", null, Start.AddMinutes(2), "Hello"),
        ]);

        Assert.Equal(4, ConversationHistoryView.Lines(interaction).Count);
    }

    [Fact]
    public void TheTimeWindow_IsInUaeTime_AndSaysWhenTheConversationIsStillLive()
    {
        var sameDay = Interaction([], ended: Start.AddMinutes(7));
        var overnight = Interaction([], ended: Start.AddHours(12));      // 02:05 on the 23rd UAE
        var live = Interaction([]);

        Assert.Equal("22 Oct 2026 14:05 → 14:12 (UAE)", ConversationHistoryView.TimeWindow(sameDay));
        Assert.Equal("22 Oct 2026 14:05 → 23 Oct 2026 02:05 (UAE)", ConversationHistoryView.TimeWindow(overnight));
        Assert.Equal("22 Oct 2026 14:05 → not ended (UAE)", ConversationHistoryView.TimeWindow(live));
    }

    [Fact]
    public void HandledBy_NamesAnAgentOnlyWhenAPersonHasTheConversation()
    {
        Assert.Equal("AI (virtual agent)", ConversationHistoryView.HandledBy(Interaction([], handling: "Ai", agent: "Layla Hassan")));
        Assert.Equal("Human agent · Layla Hassan", ConversationHistoryView.HandledBy(Interaction([], handling: "Human", agent: "Layla Hassan")));
        Assert.Equal("Human agent", ConversationHistoryView.HandledBy(Interaction([], handling: "Human")));
        Assert.Contains("human requested", ConversationHistoryView.HandledBy(Interaction([], handling: "WaitingForHuman"))!);
        Assert.Null(ConversationHistoryView.HandledBy(Interaction([], handling: null)));
    }

    [Fact]
    public void HandoffStatus_IsStatedForGenesysInteractions_AndAbsentForOnesTicketingCreated()
    {
        var handoff = new AgentHandoffSummaryDto(9, "WaitingForAgent", null, "Customer asked for a person", Start, null, null, null, null, "CustomerRequestedHuman");

        Assert.Equal("No handoff", ConversationHistoryView.HandoffStatus(Interaction([], handling: "Ai")));
        Assert.Equal("Handoff: Waiting for agent", ConversationHistoryView.HandoffStatus(Interaction([], handling: "WaitingForHuman", handoff: handoff)));
        Assert.Null(ConversationHistoryView.HandoffStatus(Interaction([], handling: null)));
    }

    [Fact]
    public void WhenSeveralInteractionsExist_OnlyTheMostRecentOpensByDefault()
    {
        var several = new[] { Interaction([]), Interaction([]), Interaction([]) };

        Assert.Equal([false, false, true], Enumerable.Range(0, several.Length).Select(i => ConversationHistoryView.OpensByDefault(several, i)));
        Assert.True(ConversationHistoryView.OpensByDefault([several[0]], 0));
    }
}
