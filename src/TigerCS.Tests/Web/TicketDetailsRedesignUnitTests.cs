// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>
/// The pure presentation rules behind the redesigned Ticket Details: each SLA clock read on its own, the customer lookup states, the
/// owner label, the record-first-response gate and the Genesys call facts. No rendering, no Api.
/// </summary>
public sealed class TicketDetailsRedesignUnitTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ SLA

    private static TicketSlaSummaryResponseDto Sla(
        string state = "Running", DateTime? frDue = null, bool frBreached = false, DateTime? frAnswered = null,
        DateTime? resDue = null, bool resBreached = false, bool paused = false, string? pauseReason = null, bool started = true) =>
        new(state, frDue, frBreached, frAnswered, resDue, resBreached, paused, pauseReason, 0, "None",
            new SlaExplanationDto(started, 3, "Medium", "Priority policy", 60, 480, "24/7", 80, null, started ? Now.AddHours(-1) : null, "InitialCreation",
                started ? null : "No SLA is running: this ticket has no priority.", "rule", "pause", []));

    [Fact]
    public void SlaNotStarted_BothClocksSayNotStarted_AndTheReasonIsKept()
    {
        var view = TicketSlaView.Build(Sla(state: "NotApplicable", started: false), "Open", Now);

        Assert.Equal("Not started", view.FirstResponse.State);
        Assert.Equal("Not started", view.Resolution.State);
        Assert.Equal("Not started", view.Headline);
        Assert.Contains("no priority", view.NotStartedReason);
    }

    [Fact]
    public void SlaRunning_NamesTheNearestOpenDeadline_NotAlwaysTheResolution()
    {
        // First reply due in 10 minutes, resolution in 2 days: the badge must warn about the 10 minutes.
        var sla = Sla(frDue: Now.AddMinutes(10), resDue: Now.AddDays(2));

        var view = TicketSlaView.Build(sla, "InProgress", Now);

        Assert.Equal("First reply due in 10m", view.Headline);
        Assert.Equal("running", view.HeadlineCss);
        Assert.StartsWith("Awaiting a person's reply, due", view.FirstResponse.State);
        Assert.StartsWith("Due ", view.Resolution.State);
    }

    [Fact]
    public void SlaRunning_AfterAPersonReplied_TheResolutionIsTheOnlyOpenDeadline()
    {
        var sla = Sla(frDue: Now.AddMinutes(10), frAnswered: Now.AddMinutes(-5), resDue: Now.AddHours(3));

        var view = TicketSlaView.Build(sla, "InProgress", Now);

        Assert.Equal("Resolution due in 3h 0m", view.Headline);
        Assert.StartsWith("Met, answered", view.FirstResponse.State);
    }

    [Fact]
    public void SlaRunning_PastADeadlineTheApiHasNotFlaggedYet_ReadsPastDue_NeverDueIn()
    {
        var view = TicketSlaView.Build(Sla(frDue: Now.AddMinutes(-20), resDue: Now.AddDays(1)), "Open", Now);

        Assert.Equal("First reply past due 20m", view.Headline);
        Assert.Equal("overdue", view.HeadlineCss);
        Assert.DoesNotContain("due in", view.Headline);
    }

    [Fact]
    public void SlaPaused_ResolutionSaysPaused_AndFirstResponseIsNotPaused()
    {
        var sla = Sla(state: "Paused", frDue: Now.AddMinutes(30), resDue: Now.AddDays(1), paused: true, pauseReason: "Pending Customer");

        var view = TicketSlaView.Build(sla, "PendingCustomer", Now);

        Assert.Equal("Paused — Pending Customer", view.Resolution.State);
        Assert.Equal("paused", view.Resolution.CssKey);
        Assert.StartsWith("Awaiting a person's reply", view.FirstResponse.State);
    }

    [Fact]
    public void FirstResponseMissed_ResolutionStillRunning_ResolutionIsNotCalledBreached()
    {
        // The Api's single SlaState is "Breached" because the first response was missed; the resolution clock is healthy.
        var sla = Sla(state: "Breached", frDue: Now.AddHours(-2), frBreached: true, resDue: Now.AddHours(5));

        var view = TicketSlaView.Build(sla, "InProgress", Now);

        Assert.Equal("breached", view.FirstResponse.CssKey);
        Assert.StartsWith("Overdue since", view.FirstResponse.State);
        Assert.Equal("running", view.Resolution.CssKey);
        Assert.StartsWith("Due ", view.Resolution.State);
    }

    [Fact]
    public void ResolvedOnTime_AfterAMissedFirstResponse_ResolutionIsMet_AndTheStickyBreachStaysOnTheOverallLine()
    {
        var sla = Sla(state: "Breached", frDue: Now.AddHours(-9), frBreached: true, frAnswered: Now.AddHours(-8), resDue: Now.AddHours(5));

        var view = TicketSlaView.Build(sla, "Resolved", Now);

        Assert.Equal("Met", view.Resolution.State);
        Assert.StartsWith("Answered late", view.FirstResponse.State);
        Assert.Equal("Breached", view.Headline);   // no ever-growing "Breached 9h" once the ticket is finished
    }

    [Theory]
    [InlineData("Resolved")]
    [InlineData("Closed")]
    public void SlaStopped_ResolutionBreached_IsMissed(string status)
    {
        var sla = Sla(state: "Breached", frDue: Now.AddDays(-3), frAnswered: Now.AddDays(-3).AddMinutes(-1), resDue: Now.AddDays(-1), resBreached: true);

        var view = TicketSlaView.Build(sla, status, Now);

        Assert.Equal("Missed", view.Resolution.State);
        Assert.Equal("Breached", view.Headline);
    }

    [Fact]
    public void Reopened_NewCycleClean_OldBreachIsNotRepaintedOnTheNewResolutionClock()
    {
        // Reopen: Breached is sticky on the ticket, but the new period's flags are clean.
        var sla = Sla(state: "Breached", frDue: Now.AddDays(-4), frAnswered: Now.AddDays(-4).AddMinutes(-1), resDue: Now.AddHours(20));

        var view = TicketSlaView.Build(sla, "InProgress", Now);

        Assert.Equal("running", view.Resolution.CssKey);
        Assert.StartsWith("Met", view.FirstResponse.State);
    }

    // ------------------------------------------------------------------ customer

    private static TicketDetailDto Ticket(
        string verification = "Unverified", int? crmUnit = null, string? crmName = null, string? crmProject = null, string? crmUnitNumber = null,
        string? manualProject = null, string? manualUnit = null, string? source = null) =>
        new(1, "TG-1", 2, 2, null, null, null, null, 3, "Open", verification, "None", "Running", null, null, "Subject", 0, Now, "AAAA",
            CrmBuyerUnitId: crmUnit, CrmBuyerCustomerName: crmName, CrmBuyerProjectName: crmProject, CrmBuyerUnitNumber: crmUnitNumber,
            ManualProjectName: manualProject, ManualUnitNumber: manualUnit, CustomerVerificationSource: source);

    [Fact]
    public void RegisteredCustomer_IsVerifiedViaCrm_WithConfirmedUnit_AndNoHint()
    {
        var s = TicketCustomerSummary.Build(
            Ticket("Verified", crmUnit: 4, crmName: "Mariam", crmProject: "Tiger Tower", crmUnitNumber: "T-1204"),
            "Mariam", true, "Verified via Tiger CRM", "+971501234567", "/Customers/crm%3A1");

        Assert.Equal(CustomerLookupState.VerifiedCrm, s.State);
        Assert.Equal("T-1204 · Tiger Tower", s.ProjectUnit);
        Assert.True(s.UnitIsConfirmed);
        Assert.Null(s.Hint);
        Assert.Equal("verified", s.StateCss);
    }

    [Fact]
    public void ExternallyVerifiedCustomer_IsVerifiedViaThatSource()
    {
        var s = TicketCustomerSummary.Build(Ticket(source: "Pact", manualProject: "Palm", manualUnit: "P-9"), "Omar", true, "Verified via PACT", "+9715", null);

        Assert.Equal(CustomerLookupState.VerifiedExternal, s.State);
        Assert.True(s.UnitIsConfirmed);
        Assert.Equal("external", s.StateCss);
    }

    [Fact]
    public void UnregisteredCaller_IsPhoneOnly_AndTheHintSaysNoRegisteredCustomerMatched()
    {
        var s = TicketCustomerSummary.Build(Ticket(), "Unnamed caller", false, "Unverified", "+971500000000", "/Customers/phone%3A1");

        Assert.Equal(CustomerLookupState.PhoneOnly, s.State);
        Assert.False(s.HasName);
        Assert.Contains("not matched to a registered customer", s.Hint);
        Assert.Null(s.ProjectUnit);
    }

    [Fact]
    public void CrmLookupFailure_ProvisionalTicket_SaysDetailsAreNotConfirmed_NeverAName()
    {
        var s = TicketCustomerSummary.Build(Ticket("PendingCrmVerification"), "Customer not identified", false, "Pending CRM Verification", "+9715", null);

        Assert.Equal(CustomerLookupState.PendingCrm, s.State);
        Assert.Contains("could not be completed", s.Hint);
        Assert.False(s.HasName);
    }

    [Fact]
    public void TypedInUnit_IsManualEntry_AndNotConfirmed()
    {
        var s = TicketCustomerSummary.Build(Ticket(manualProject: "Tiger Tower", manualUnit: "T-1"), "Sara", true, "Manual entry / Not externally verified", null, null);

        Assert.Equal(CustomerLookupState.ManualEntry, s.State);
        Assert.False(s.UnitIsConfirmed);
        Assert.Null(s.Phone);
        Assert.Equal("T-1 · Tiger Tower", s.ProjectUnit);
    }

    [Fact]
    public void NothingKnown_IsNotIdentified()
    {
        var s = TicketCustomerSummary.Build(Ticket(), "Customer not identified", false, "Unverified", "  ", null);

        Assert.Equal(CustomerLookupState.NotIdentified, s.State);
        Assert.Null(s.Phone);
        Assert.Null(s.ProfileHref);
        Assert.Contains("Nothing on this ticket identifies the customer", s.Hint);
    }

    // ------------------------------------------------------------------ owner label

    [Fact]
    public void OwnerLabel_ActiveTicketWithoutOwner_IsAnAssignmentFailure_FinishedOneIsJustUnowned()
    {
        Assert.Equal(TicketDisplay.UnassignedCsAgentLabel, TicketDisplay.AssignedToLabel(null, null, 2, "CS"));
        Assert.Equal(TicketDisplay.NoOwnerOnFinishedTicketLabel, TicketDisplay.AssignedToLabel(null, null, 2, "CS", isActive: false));
        Assert.Equal("Layla", TicketDisplay.AssignedToLabel(Guid.NewGuid(), "Layla", 2, "CS", isActive: false));
    }

    // ------------------------------------------------------------------ record-first-response gate

    private static TicketActionContext Context(string role, bool owner = false, bool inDepartment = true, ISet<string>? denied = null)
    {
        var viewer = Guid.NewGuid();
        return new TicketActionContext(
            [role], viewer, owner ? viewer : Guid.NewGuid(), 2, inDepartment ? [2] : [9],
            ViewerDeniedPermissions: denied is null ? null : new HashSet<string>(denied));
    }

    [Theory]
    [InlineData(Roles.CsSupervisor, false, true, true)]
    [InlineData(Roles.CsManager, false, true, true)]
    [InlineData(Roles.GeneralManager, false, false, true)]
    [InlineData(Roles.CsAgent, true, true, true)]          // the owner
    [InlineData(Roles.CsAgent, false, true, false)]        // a non-owner agent: the Api answers 403
    [InlineData(Roles.DepartmentEmployee, false, true, false)]
    [InlineData(Roles.DepartmentHead, false, true, true)]
    [InlineData(Roles.DepartmentHead, false, false, false)] // head of another department
    [InlineData(Roles.ReportingUser, false, true, false)]
    public void RecordFirstResponse_FollowsTheApiRule(string role, bool owner, bool inDepartment, bool expected) =>
        Assert.Equal(expected, TicketActions.CanRecordFirstResponse(Context(role, owner, inDepartment)));

    [Fact]
    public void RecordFirstResponse_IsHidden_WhenTheViewerIsDeniedEscalate_OrThereIsNoContext()
    {
        Assert.False(TicketActions.CanRecordFirstResponse(Context(Roles.CsManager, denied: new HashSet<string> { TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsEscalate })));
        Assert.False(TicketActions.CanRecordFirstResponse(null));
    }

    // ------------------------------------------------------------------ Genesys call facts

    private static TicketInteractionDto Call(string? channel, string? direction, DateTime? ended, string? queue = null) =>
        new(1, true, "Genesys", 1, channel, "+9715", null, null, direction, "conv", null, queue, null, null, Now, ended, null, ended is null ? "Active" : "Ended", []);

    [Fact]
    public void CallFacts_DurationDirectionAndVoice()
    {
        Assert.Equal("7 min", ConversationHistoryView.Duration(Call("Phone", "Inbound", Now.AddMinutes(7))));
        Assert.Equal("1 h 5 min", ConversationHistoryView.Duration(Call("Phone", "Inbound", Now.AddMinutes(65))));
        Assert.Equal("under a minute", ConversationHistoryView.Duration(Call("Phone", "Inbound", Now.AddSeconds(20))));
        Assert.Null(ConversationHistoryView.Duration(Call("Phone", "Inbound", null)));
        Assert.Equal("Customer contacted us", ConversationHistoryView.DirectionLabel("Inbound"));
        Assert.Equal("We contacted the customer", ConversationHistoryView.DirectionLabel("Outbound"));
        Assert.Null(ConversationHistoryView.DirectionLabel(null));
        Assert.True(ConversationHistoryView.IsVoice(Call("Phone", null, null)));
        Assert.False(ConversationHistoryView.IsVoice(Call("WhatsApp", null, null)));
    }
}
