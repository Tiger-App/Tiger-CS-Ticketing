// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>
/// The "What needs your attention?" guidance against the agent scenarios it exists for. The guidance
/// only words state and permissions the page already derives, so each test sets those and asserts
/// what a person would be told — and that nothing is offered the viewer cannot do.
/// </summary>
public sealed class TicketNextActionTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static TicketGuidanceFacts Facts(
        string status = "InProgress",
        bool classified = true,
        bool hasOwner = true,
        bool viewerIsOwner = false,
        string? handoff = null,
        string? handling = null,
        bool slaBreached = false,
        bool readOnly = false,
        bool canClassify = false,
        bool canAssign = false,
        bool canChangeStatus = false,
        bool canResolve = false,
        bool canClose = false,
        bool canEscalate = false,
        bool canReopen = false,
        bool isReopenEligible = false,
        int reopenCount = 0,
        bool firstResponsePending = false,
        bool canRecordFirstResponse = false,
        bool paused = false,
        bool pendingDowngrade = false,
        bool canDecideDowngrade = false,
        IReadOnlyList<PendingApprovalFact>? pending = null,
        IReadOnlyList<RequiredApprovalFact>? required = null,
        bool reopenApproved = false) =>
        new(
            TicketStatus: status,
            IsClassified: classified,
            HasOwner: hasOwner,
            ViewerIsOwner: viewerIsOwner,
            OwnerName: hasOwner ? "Ahmed" : null,
            DepartmentName: "Facility Management",
            ReopenCount: reopenCount,
            IsReopenEligible: isReopenEligible,
            PendingCrmVerification: false,
            HandoffState: handoff ?? "NotRequired",
            HandoffRequestedAtUtc: handoff == "WaitingForAgent" ? Now.AddMinutes(-12) : null,
            HandlingState: handling,
            SlaBreached: slaBreached,
            SlaBreachText: slaBreached ? "Resolution SLA is overdue by 2h 5m." : null,
            FirstResponsePending: firstResponsePending,
            ResolutionClockPaused: paused,
            ViewerIsReadOnly: readOnly,
            CanClassify: canClassify,
            CanAssign: canAssign,
            CanTransfer: false,
            CanChangeStatus: canChangeStatus,
            CanResolve: canResolve,
            CanClose: canClose,
            CanEscalate: canEscalate,
            CanReopen: canReopen,
            CanRequestDowngrade: false,
            CanRecordFirstResponse: canRecordFirstResponse,
            HasPendingDowngrade: pendingDowngrade,
            CanDecideDowngrade: canDecideDowngrade,
            PendingApprovals: pending ?? [],
            RequiredApprovals: required ?? [],
            ReopenApproved: reopenApproved,
            NowUtc: Now);

    // ---- The seven scenarios ----

    [Fact]
    public void NewUnclassifiedTicket_AsksForClassification_AndPointsAtTheClassifyAction()
    {
        var g = TicketNextAction.Build(Facts(status: "Open", classified: false, hasOwner: false, canClassify: true, canAssign: true));

        Assert.Equal(AttentionLevel.Urgent, g.Level);
        Assert.Equal("Classify this ticket", g.Headline);
        Assert.Equal("classify", g.Primary!.Key);
        Assert.Contains(g.Alerts, a => a.Text == "Needs human classification.");
    }

    [Fact]
    public void UnclassifiedTicket_ForSomeoneWhoCannotClassify_SaysSoWithoutOfferingTheAction()
    {
        var g = TicketNextAction.Build(Facts(status: "Open", classified: false, hasOwner: false));

        Assert.Equal(AttentionLevel.Urgent, g.Level);
        Assert.Null(g.Primary);
        Assert.Contains(g.Unavailable, u => u.Label == "Classify");
    }

    [Fact]
    public void UnassignedTicket_WhenViewerCanAssign_PointsAtAssign()
    {
        var g = TicketNextAction.Build(Facts(hasOwner: false, canAssign: true));

        Assert.Equal("Assign this ticket", g.Headline);
        Assert.Equal("assign", g.Primary!.Key);
        Assert.Contains("Facility Management queue", g.Reason);
    }

    [Fact]
    public void UnassignedTicket_WhenViewerCannotAssign_ExplainsWhy_AndDoesNotOfferAssign()
    {
        var g = TicketNextAction.Build(Facts(hasOwner: false, canAssign: false));

        Assert.NotEqual("assign", g.Primary?.Key);
        Assert.Contains("cannot assign", g.Reason);
        Assert.Contains(g.Unavailable, u => u.Label == "Assign" && u.Reason.Length > 0);
    }

    [Fact]
    public void AiHandledTicket_SaysNoPersonIsNeeded_AndLinksToTheConversation()
    {
        var g = TicketNextAction.Build(Facts(status: "Open", hasOwner: false, handling: "Ai", canAssign: true));

        Assert.Equal(AttentionLevel.Info, g.Level);
        Assert.Equal("AI is handling this customer", g.Headline);
        // The conversation is part of the Overview, so the card scrolls to it rather than switching tab.
        Assert.Equal("conversation", g.Primary!.Key);
        Assert.False(g.Primary.IsTab);
    }

    [Fact]
    public void CustomerWaitingForAPerson_IsUrgent_AndSaysHowLong()
    {
        var g = TicketNextAction.Build(Facts(status: "Open", hasOwner: false, handoff: "WaitingForAgent", handling: "WaitingForHuman", canAssign: true));

        Assert.Equal(AttentionLevel.Urgent, g.Level);
        Assert.Contains("12 min", g.Reason);
        Assert.Equal("assign", g.Primary!.Key);
    }

    [Fact]
    public void PendingCustomer_SaysNothingToDo_AndOffersChangeStatusOnlyWhenAllowed()
    {
        var allowed = TicketNextAction.Build(Facts(status: "PendingCustomer", canChangeStatus: true, paused: true));
        var denied = TicketNextAction.Build(Facts(status: "PendingCustomer", canChangeStatus: false));

        Assert.Equal("Waiting for the customer", allowed.Headline);
        Assert.Equal("status", allowed.Primary!.Key);
        Assert.Contains(allowed.Alerts, a => a.Text == "The resolution clock is paused.");
        Assert.Null(denied.Primary);
    }

    [Fact]
    public void ResolvedTicket_ForCloser_PointsAtClose_ForOthersExplainsItIsWaiting()
    {
        var closer = TicketNextAction.Build(Facts(status: "Resolved", canClose: true));
        var other = TicketNextAction.Build(Facts(status: "Resolved", canClose: false));

        Assert.Equal("close", closer.Primary!.Key);
        Assert.Null(other.Primary);
        Assert.Contains(other.Unavailable, u => u.Label == "Close ticket");
    }

    [Fact]
    public void ClosedTicket_IsDone_AndReopenIsOnlyOfferedWhenEligibleAndPermitted()
    {
        var plain = TicketNextAction.Build(Facts(status: "Closed"));
        var reopenable = TicketNextAction.Build(Facts(status: "Closed", isReopenEligible: true, canReopen: true));
        var noPermission = TicketNextAction.Build(Facts(status: "Closed", isReopenEligible: true, canReopen: false));
        var approved = TicketNextAction.Build(Facts(status: "Closed", isReopenEligible: true, canReopen: true, reopenApproved: true));

        Assert.Equal(AttentionLevel.Done, plain.Level);
        Assert.Null(plain.Primary);
        Assert.Contains(plain.Unavailable, u => u.Label == "Reopen");
        Assert.Null(reopenable.Primary);
        Assert.Empty(reopenable.Unavailable);
        Assert.Contains(noPermission.Unavailable, u => u.Label == "Reopen");
        Assert.Equal("reopen", approved.Primary!.Key);
    }

    [Fact]
    public void ReopenedTicket_OwnedByViewer_MentionsTheReopen()
    {
        var g = TicketNextAction.Build(Facts(viewerIsOwner: true, canResolve: true, reopenCount: 2));

        Assert.Contains("Reopened 2 times", g.Reason);
        Assert.Equal("resolve", g.Primary!.Key);
    }

    // ---- Urgency and approvals ----

    [Fact]
    public void OverdueSla_LiftsAnInformationalCardToUrgent_AndListsTheOverdueTime()
    {
        var g = TicketNextAction.Build(Facts(status: "PendingCustomer", slaBreached: true));

        Assert.Equal(AttentionLevel.Urgent, g.Level);
        Assert.Contains(g.Alerts, a => a.Level == AttentionLevel.Urgent && a.Text.Contains("overdue by 2h 5m"));
    }

    [Fact]
    public void OverdueSla_NeverMakesAClosedTicketUrgent()
    {
        var g = TicketNextAction.Build(Facts(status: "Closed", slaBreached: true));

        Assert.Equal(AttentionLevel.Done, g.Level);
        Assert.Empty(g.Alerts);
    }

    [Fact]
    public void ApprovalWaitingOnViewer_IsTheHeadline_AndOneWaitingOnSomeoneElseIsNot()
    {
        var mine = TicketNextAction.Build(Facts(viewerIsOwner: true, pending: [new("Accounting Approval", "Accounting department", true)]));
        var theirs = TicketNextAction.Build(Facts(viewerIsOwner: true, pending: [new("Accounting Approval", "Accounting department", false)]));

        Assert.Equal(AttentionLevel.Urgent, mine.Level);
        Assert.Equal("tab-approvals", mine.Primary!.Key);
        Assert.Equal(AttentionLevel.Info, theirs.Level);
        Assert.Contains("Accounting department must decide", theirs.Reason);
    }

    [Fact]
    public void BlockingApprovalNobodyRequested_IsOfferedOnlyToThoseWhoMayRequestIt()
    {
        var can = TicketNextAction.Build(Facts(viewerIsOwner: true, required: [new("Accounting Approval", "Accounting department", true, true)]));
        var cannot = TicketNextAction.Build(Facts(viewerIsOwner: true, required: [new("Accounting Approval", "Accounting department", true, false)]));

        Assert.Equal("Request approval: Accounting Approval", can.Headline);
        Assert.Equal(AttentionLevel.Info, cannot.Level);
        Assert.Contains("cannot request it", cannot.Reason);
    }

    [Fact]
    public void PendingDowngrade_IsADecisionOnlyForTheDecider()
    {
        var decider = TicketNextAction.Build(Facts(viewerIsOwner: true, pendingDowngrade: true, canDecideDowngrade: true));
        var other = TicketNextAction.Build(Facts(viewerIsOwner: true, pendingDowngrade: true));

        Assert.Equal("pending-downgrade", decider.Primary!.Key);
        Assert.Contains(other.Alerts, a => a.Text.Contains("downgrade"));
        Assert.NotEqual("pending-downgrade", other.Primary?.Key);
    }

    [Fact]
    public void ReadOnlyViewer_IsToldTheyCanOnlyRead_AndOffered_NoAction()
    {
        var g = TicketNextAction.Build(Facts(readOnly: true, hasOwner: false));

        Assert.Equal("You have view-only access", g.Headline);
        Assert.Null(g.Primary);
        Assert.Single(g.Unavailable);
    }

    [Fact]
    public void UnavailableReasons_NeverNameARole_SoTheyCannotDriftFromTheRoleSets()
    {
        var all = new[]
        {
            TicketNextAction.Build(Facts(hasOwner: false)),
            TicketNextAction.Build(Facts(status: "Resolved")),
            TicketNextAction.Build(Facts(status: "Closed", isReopenEligible: true))
        }.SelectMany(g => g.Unavailable.Select(u => u.Reason + g.Reason));

        foreach (var text in all)
        {
            Assert.DoesNotContain("CS Agent", text);
            Assert.DoesNotContain("CS Supervisor", text);
            Assert.DoesNotContain("CS Manager", text);
        }
    }

    [Fact]
    public void Guidance_NeverShowsTechnicalValues()
    {
        var g = TicketNextAction.Build(Facts(status: "PendingThirdParty", canChangeStatus: true));
        var text = $"{g.Headline} {g.Reason}";

        Assert.DoesNotContain("PendingThirdParty", text);
        Assert.DoesNotContain("NOT_REGISTERED", text);
    }

    [Fact]
    public void Overview_LeadsWithTheAttentionCard_AndKeepsEveryExistingAction()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "TigerCS.Web", "Pages", "TicketDetails.cshtml")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var view = File.ReadAllText(Path.Combine(dir!, "TigerCS.Web", "Pages", "TicketDetails.cshtml"));

        Assert.True(view.IndexOf("What needs your attention?", StringComparison.Ordinal) < view.IndexOf("_TicketCustomerCard", StringComparison.Ordinal));
        foreach (var handler in new[] { "Assign", "Classify", "Transfer", "ChangeStatus", "Resolve", "Escalate", "RequestDowngrade", "Close", "Reopen", "RecordFirstResponse", "AddNote", "RequestApproval", "DecideApproval", "RecordEvent" })
        {
            Assert.Contains($"asp-page-handler=\"{handler}\"", view);
        }

        // Technical ids are not shown in the header any more.
        Assert.DoesNotContain("hdrCat", view);
    }
}
