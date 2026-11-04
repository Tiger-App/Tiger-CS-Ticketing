using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details must never claim a customer asked for a human unless Genesys said so, and must never show a human agent for a
/// conversation the AI is handling alone. Presentation checks over the Razor page and its display helpers (the Web project is not
/// referenced by this assembly); the behaviour behind them is covered by <c>GenesysHandoffAndClassificationRegressionTests</c>.
/// </summary>
public sealed class HandoffWordingTests
{
    private static string Read(string relativeToSrc, [CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..")), relativeToSrc));

    [Fact]
    public void TheActivityFeed_NoLongerHardCodesThatTheCustomerAskedForAHuman()
    {
        var page = Read("TigerCS.Web/Pages/TicketDetails.cshtml.cs");

        Assert.DoesNotContain("=> \"asked for a human agent\"", page);
        Assert.Contains("TicketDisplay.HandoffRequestedActivity(approvalEvent.Note)", page);
    }

    [Fact]
    public void TheHandoffSentence_FollowsTheStatedInitiator()
    {
        var display = Read("TigerCS.Web/Models/TicketDisplay.cs");

        Assert.Contains("\"CustomerRequestedHuman\" => \"customer asked for a human agent\"", display);
        Assert.Contains("\"AiEscalated\" => \"AI escalated the conversation to a human agent\"", display);
        Assert.Contains("_ => \"a human agent was requested\"", display);
        Assert.Contains("null or \"\" => \"Not stated by Genesys\"", display);
    }

    [Fact]
    public void TheConversationHistory_ShowsHandlingHandoffRequestedInitiatorAndStartEnd_AndAnAgentOnlyWhenAHumanHasTheConversation()
    {
        var view = Read("TigerCS.Web/Pages/Shared/_TicketConversationHistory.cshtml");
        var helper = Read("TigerCS.Web/Models/ConversationHistoryView.cs");

        // A named agent appears only for a Human-handled interaction; an AI-only conversation never shows one.
        Assert.Contains("\"Human\" => string.IsNullOrWhiteSpace(interaction.AgentName) ? \"Human agent\" : $\"Human agent · {interaction.AgentName.Trim()}\"", helper);
        Assert.Contains("ConversationHistoryView.HandledBy(interaction)", view);
        Assert.Contains("ConversationHistoryView.HandoffStatus(interaction)", view);
        Assert.Contains("<dt>Handoff requested</dt>", view);
        Assert.Contains("TicketDisplay.HandoffInitiatorLabel(handoff.Trigger)", view);
        Assert.Contains("<dt>Reason</dt>", view);
        Assert.Contains("ConversationHistoryView.TimeWindow(interaction)", view);
    }

    [Fact]
    public void TheOverview_AlwaysShowsTheRequestType_AndLeavesWhoIsHandlingToTheConversationHistory()
    {
        var view = Read("TigerCS.Web/Pages/TicketDetails.cshtml");

        // A ticket on the default classification shows its request type; one with a category but no request type says "Not specified".
        Assert.Contains("t.RequestTypeName ?? (t.CategoryId is null ? \"Not yet classified\" : \"Not specified\")", view);
        Assert.Contains("_TicketConversationHistory", view);
        // Who handled it is said once, per interaction, in Conversation History — not repeated in the request facts.
        Assert.DoesNotContain("Being handled by", view);
    }
}
