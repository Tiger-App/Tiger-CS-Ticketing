using TigerCS.Application.Modules.Reporting.Abstractions;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Reporting.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Reporting;

/// <summary>
/// Employee response time for Genesys AI → human voice transfers, shown in the employee's SLA details beside — and never changing — the
/// ticket's own SLA. Every scenario runs over real SQL (SQLite in-memory) and also proves the ticket SLA result is identical to the same
/// ticket read with no offer data at all.
///
/// The ticket used throughout: created at T0, handled by the AI, queued, offered to the employee at T0+30 min. Its First Response SLA (due
/// T0+15 min) was breached by the AI/queue delay before anyone was offered the call — an SLA fact the employee measurement must not touch.
/// </summary>
public sealed class EmployeeResponseTimeTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;
    private static readonly DateTime T0 = Now.AddDays(-2);
    private static readonly DateTime Offer = T0.AddMinutes(30);

    private readonly DashboardSqliteFixture _db = new();
    private readonly Guid _agentA;
    private readonly Guid _agentB;
    private long _ticketId;
    private string _ticketNumber = "";

    public EmployeeResponseTimeTests()
    {
        using var context = _db.CreateContext();
        _agentA = _db.AddEmployee(context, "Agent A");
        _agentB = _db.AddEmployee(context, "Agent B");
        context.UserDepartmentAssignments.AddRange(
            new UserDepartmentAssignment(_agentA, _db.CustomerServiceId, true, Now.AddDays(-50), null),
            new UserDepartmentAssignment(_agentB, _db.CustomerServiceId, true, Now.AddDays(-50), null));
        context.SlaPolicies.Add(new SlaPolicy((byte)PriorityLevel.Medium, 240, 1800, SlaClockBasis.BusinessHours, 75m, 1, 1));
        context.SaveChanges();

        var ticket = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: T0, owner: _agentA, slaBreached: true);
        _ticketId = ticket.TicketId;
        _ticketNumber = ticket.TicketNumber;
        context.TicketAssignments.Add(new TicketAssignment(ticket.TicketId, _agentA, ticket.CurrentDepartmentId, T0.AddMinutes(5), null)); // pre-assigned at creation: holds the ticket at the FR deadline

        var frDue = T0.AddMinutes(15);
        var period = TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, (byte)PriorityLevel.Medium, T0, frDue, T0.AddDays(1),
            new AppliedSlaPolicy(SlaClockBasis.BusinessHours, AppliedFirstResponseTargetMinutes: 240, AppliedResolutionTargetMinutes: 1800));
        period.MarkBreached(SlaDeadlineType.FirstResponse);
        context.TicketSlaInstances.Add(period);
        context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Running, (byte)SlaState.Breached,
            actorEmployeeId: null, actorIsSystem: true, note: $"FirstResponse SLA breached (due {frDue:O}).", Guid.NewGuid(), frDue));
        context.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------

    private sealed class FakeOffers(params (Guid Employee, GenesysOfferRecord Offer)[] offers) : IGenesysOfferHistory
    {
        public Task<IReadOnlyList<GenesysOfferRecord>> GetOffersAsync(Guid employeeId, DateTime start, DateTime end, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GenesysOfferRecord>>(offers
                .Where(o => o.Employee == employeeId
                    && (o.Offer.OfferedAtUtc is not { } t || (t >= start && t < end))) // an offer with no time is still the employee's
                .Select(o => o.Offer).ToList());
    }

    private sealed class FakeShifts(Func<Guid, DateTime, ShiftVerdict> verdict) : IEmployeeShiftHistory
    {
        public Task<ShiftVerdict> GetVerdictAsync(Guid employeeId, DateTime atUtc, CancellationToken ct = default) =>
            Task.FromResult(verdict(employeeId, atUtc));
    }

    private async Task<TeamPerformanceSlaDetailsDto> DetailsAsync(
        Guid employeeId, IGenesysOfferHistory? offers = null, IEmployeeShiftHistory? shifts = null)
    {
        using var context = _db.CreateContext();
        var service = new TeamPerformanceAppService(
            new TeamPerformanceQueryRepository(context), new DashboardSqliteFixture.FixedTimeProvider(Now), null, offers, shifts);
        var result = await service.GetSlaDetailsAsync(new TeamPerformanceSlaDetailsRequestDto(employeeId, PageSize: 100));
        Assert.Equal(TeamPerformanceSlaDetailsOutcome.Success, result.Outcome);
        return result.Details!;
    }

    private static readonly IEmployeeShiftHistory InShift = new FakeShifts((_, _) => ShiftVerdict.WithinShift);

    private GenesysOfferRecord OfferTo(DateTime? offeredAt, GenesysOfferOutcome outcome, DateTime? answeredAt = null) =>
        new(_ticketId, "conv-1", offeredAt, outcome, answeredAt);

    /// <summary>The ticket SLA result for the employee, with and without the response data: it must be byte-for-byte the same.</summary>
    private async Task AssertTicketSlaUnchangedAsync(Guid employeeId, TeamPerformanceSlaDetailsDto withResponses)
    {
        var baseline = await DetailsAsync(employeeId);
        Assert.Equal(baseline.Records, withResponses.Records);
        Assert.Equal(baseline.Summary, withResponses.Summary);
        Assert.Equal(baseline.TypeBreakdown, withResponses.TypeBreakdown);
        Assert.Equal(baseline.StatusCounts, withResponses.StatusCounts);

        // The ticket is a breached First Response, attributed to Agent A, whatever the offer said.
        var record = Assert.Single(baseline.Records, r => r.TicketId == _ticketId && r.SlaType == TeamPerformanceSlaTypes.FirstResponse && r.AssignedEmployeeId == _agentA);
        Assert.Equal(TeamPerformanceSlaStatuses.Breached, record.Status);

        using var context = _db.CreateContext();
        var ticket = context.Tickets.Single(t => t.TicketId == _ticketId);
        Assert.Null(ticket.FirstHumanResponseAtUtc);
        Assert.Equal(SlaState.Breached, ticket.SlaState);
    }

    // ---------------------------------------------------------------
    // Data actually available today
    // ---------------------------------------------------------------

    [Fact]
    public async Task WithNoOfferOrShiftData_NothingIsMeasured_AndTheTicketSlaIsStillReported()
    {
        var details = await DetailsAsync(_agentA);

        Assert.NotNull(details.EmployeeResponseSummary);
        Assert.Empty(details.EmployeeResponses!);
        Assert.Equal(0, details.EmployeeResponseSummary!.Offers);
        Assert.Null(details.EmployeeResponseSummary.AverageResponseSeconds);
        Assert.Equal(1, details.Summary.BreachEvents);
    }

    [Fact]
    public async Task ACallTheEmployeeConnectedTo_WithNoOfferEvent_IsAvailabilityUnknown_WithItsAnswerTimeButNoDuration()
    {
        var connectedAt = Offer.AddSeconds(12);
        using (var context = _db.CreateContext())
        {
            var interaction = TicketInteraction.CreateFromGenesys(
                _ticketId, WellKnownChannels.Phone, "+971500000000", "conv-1",
                calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
                interactionStartedAtUtc: T0, direction: "Inbound", createdAtUtc: T0, isOriginatingInteraction: true, genesysServiceRequestId: "sr-1");
            context.TicketInteractions.Add(interaction);
            context.SaveChanges();
            context.TicketInteractionHandlers.Add(TicketInteractionHandler.Create(
                interaction.TicketInteractionId, _ticketId, _agentA, "genesys-a", InteractionHandlerSource.Routing, connectedAt, connectedAt));
            // A transcript-sourced row is not an answer event, even with a time.
            context.SaveChanges();
        }

        var details = await DetailsAsync(_agentA);

        var row = Assert.Single(details.EmployeeResponses!);
        Assert.Equal(EmployeeOfferOutcomes.AvailabilityUnknown, row.Outcome);
        Assert.Null(row.OfferedAtUtc);
        Assert.Equal(connectedAt, row.AnsweredAtUtc);
        Assert.Null(row.ResponseSeconds);
        Assert.False(row.CountsTowardPerformance);
        Assert.Equal(1, details.EmployeeResponseSummary!.AvailabilityUnknown);
        Assert.Null(details.EmployeeResponseSummary.AverageResponseSeconds);
        await AssertTicketSlaUnchangedAsync(_agentA, details);
    }

    // ---------------------------------------------------------------
    // The six required cases
    // ---------------------------------------------------------------

    [Fact]
    public async Task Case1_AiAndQueueDelayBeforeTheOffer_AnswerTwelveSecondsAfterTheOffer_MeasuresTwelveSeconds()
    {
        // Ticket created T0, offered 30 minutes later (AI + queue time), answered 12 s after the offer. Only the 12 s is the employee's.
        var offers = new FakeOffers((_agentA, OfferTo(Offer, GenesysOfferOutcome.Answered, Offer.AddSeconds(12))));

        var details = await DetailsAsync(_agentA, offers, InShift);

        var row = Assert.Single(details.EmployeeResponses!);
        Assert.Equal(EmployeeOfferOutcomes.Answered, row.Outcome);
        Assert.Equal(Offer, row.OfferedAtUtc);
        Assert.Equal(Offer.AddSeconds(12), row.AnsweredAtUtc);
        Assert.Equal(12d, row.ResponseSeconds);
        Assert.Equal("12 s", row.ResponseText);
        Assert.Equal(_ticketNumber, row.TicketNumber);
        Assert.True(row.CountsTowardPerformance);
        Assert.Equal(12d, details.EmployeeResponseSummary!.AverageResponseSeconds);
        await AssertTicketSlaUnchangedAsync(_agentA, details);
    }

    [Fact]
    public async Task Case2_GenesysConfirmsNoAnswerOnAnInShiftOffer_IsNotResponding_AndNotATicketBreach()
    {
        var offers = new FakeOffers((_agentA, OfferTo(Offer, GenesysOfferOutcome.NoAnswerConfirmed)));

        var details = await DetailsAsync(_agentA, offers, InShift);

        var row = Assert.Single(details.EmployeeResponses!);
        Assert.Equal("Not Responding", row.Outcome);
        Assert.Null(row.AnsweredAtUtc);
        Assert.Null(row.ResponseSeconds);
        Assert.True(row.CountsTowardPerformance);
        Assert.Equal(1, details.EmployeeResponseSummary!.NotResponding);
        // Tracked apart from the ticket SLA: the SLA breach count is exactly the one the engine recorded, not one more.
        Assert.Equal(1, details.Summary.BreachEvents);
        await AssertTicketSlaUnchangedAsync(_agentA, details);
    }

    [Theory]
    [InlineData(ShiftVerdict.OutsideShift)]
    [InlineData(ShiftVerdict.OnApprovedLeave)]
    public async Task Case3_OfferOutsideTheShiftOrOnApprovedLeave_IsOutsideShift_AndExcluded_EvenIfGenesysSaysNoAnswer(ShiftVerdict verdict)
    {
        var offers = new FakeOffers((_agentA, OfferTo(Offer, GenesysOfferOutcome.NoAnswerConfirmed)));
        var shifts = new FakeShifts((_, _) => verdict);

        var details = await DetailsAsync(_agentA, offers, shifts);

        var row = Assert.Single(details.EmployeeResponses!);
        Assert.Equal("Outside Shift", row.Outcome);
        Assert.False(row.CountsTowardPerformance);
        Assert.Null(row.ResponseSeconds);
        Assert.Equal(1, details.EmployeeResponseSummary!.OutsideShift);
        Assert.Equal(0, details.EmployeeResponseSummary.NotResponding);
        await AssertTicketSlaUnchangedAsync(_agentA, details);
    }

    [Fact]
    public async Task Case4a_NoHistoricalShiftData_IsAvailabilityUnknown_EvenWhenGenesysSaysNoAnswerOrAnswered()
    {
        var offers = new FakeOffers(
            (_agentA, OfferTo(Offer, GenesysOfferOutcome.NoAnswerConfirmed)),
            (_agentA, new GenesysOfferRecord(_ticketId, "conv-2", Offer.AddHours(1), GenesysOfferOutcome.Answered, Offer.AddHours(1).AddSeconds(5))));
        var noRoster = new FakeShifts((_, _) => ShiftVerdict.Unknown);

        var details = await DetailsAsync(_agentA, offers, noRoster);

        Assert.All(details.EmployeeResponses!, r =>
        {
            Assert.Equal("Availability Unknown", r.Outcome);
            Assert.False(r.CountsTowardPerformance);
            Assert.Null(r.ResponseSeconds);
        });
        Assert.Equal(2, details.EmployeeResponseSummary!.AvailabilityUnknown);
        Assert.Equal(0, details.EmployeeResponseSummary.NotResponding);
        Assert.Null(details.EmployeeResponseSummary.AverageResponseSeconds);
        await AssertTicketSlaUnchangedAsync(_agentA, details);
    }

    [Fact]
    public async Task Case4b_MissingOfferEvent_IsAvailabilityUnknown_NotInferredFromAssignmentOrPresence()
    {
        // The record has no offer time; the ticket was assigned to Agent A at the moment of the (unreported) offer and A is "in shift" — neither is a substitute.
        var offers = new FakeOffers((_agentA, OfferTo(offeredAt: null, GenesysOfferOutcome.Answered, Offer.AddSeconds(12))));

        var details = await DetailsAsync(_agentA, offers, InShift);

        var row = Assert.Single(details.EmployeeResponses!);
        Assert.Equal("Availability Unknown", row.Outcome);
        Assert.Null(row.OfferedAtUtc);
        Assert.Null(row.ResponseSeconds);
        await AssertTicketSlaUnchangedAsync(_agentA, details);
    }

    [Theory]
    [InlineData(GenesysOfferOutcome.Unverified, null)]
    [InlineData(GenesysOfferOutcome.Answered, null)]      // "answered" without an answer time cannot be measured
    [InlineData(GenesysOfferOutcome.Answered, -5)]        // an answer before the offer is not a valid measurement
    public async Task Case4c_OutcomeCannotBeVerified_IsAvailabilityUnknown(GenesysOfferOutcome outcome, int? answerOffsetSeconds)
    {
        var answeredAt = answerOffsetSeconds is { } s ? Offer.AddSeconds(s) : (DateTime?)null;
        var offers = new FakeOffers((_agentA, OfferTo(Offer, outcome, answeredAt)));

        var details = await DetailsAsync(_agentA, offers, InShift);

        var row = Assert.Single(details.EmployeeResponses!);
        Assert.Equal("Availability Unknown", row.Outcome);
        Assert.Null(row.ResponseSeconds);
        Assert.False(row.CountsTowardPerformance);
        await AssertTicketSlaUnchangedAsync(_agentA, details);
    }

    [Fact]
    public async Task Case5_OfferedToAgentA_AnsweredByAgentB_EachOutcomeBelongsToTheEmployeeWhoReceivedThatOffer()
    {
        // Agent A was offered the call at T and Genesys confirms A did not respond; the call was then offered to B at T+20s and B answered 8 s later.
        var offerA = OfferTo(Offer, GenesysOfferOutcome.NoAnswerConfirmed);
        var offerB = OfferTo(Offer.AddSeconds(20), GenesysOfferOutcome.Answered, Offer.AddSeconds(28));
        var offers = new FakeOffers((_agentA, offerA), (_agentB, offerB));

        var a = await DetailsAsync(_agentA, offers, InShift);
        var b = await DetailsAsync(_agentB, offers, InShift);

        var rowA = Assert.Single(a.EmployeeResponses!);
        Assert.Equal("Not Responding", rowA.Outcome);
        Assert.Null(rowA.AnsweredAtUtc);            // B's answer is never shown as A's
        Assert.Null(rowA.ResponseSeconds);

        var rowB = Assert.Single(b.EmployeeResponses!);
        Assert.Equal("Answered", rowB.Outcome);
        Assert.Equal(Offer.AddSeconds(20), rowB.OfferedAtUtc);   // measured from B's own offer, not A's
        Assert.Equal(8d, rowB.ResponseSeconds);

        await AssertTicketSlaUnchangedAsync(_agentA, a);
        // B holds no SLA evaluation on this ticket; its SLA records are unchanged by the offer data too.
        var bBaseline = await DetailsAsync(_agentB);
        Assert.Equal(bBaseline.Records, b.Records);
        Assert.Equal(bBaseline.Summary, b.Summary);
    }

    [Fact]
    public async Task Case5b_OfferedToAgentA_AnsweredByAgentB_WithoutAConfirmedOutcomeForA_AIsUnknown_NotNotResponding()
    {
        // B answering does not prove A ignored their offer (A may have declined, or the offer was withdrawn): without Genesys saying so, it is Unknown.
        var offers = new FakeOffers(
            (_agentA, OfferTo(Offer, GenesysOfferOutcome.Unverified)),
            (_agentB, OfferTo(Offer.AddSeconds(20), GenesysOfferOutcome.Answered, Offer.AddSeconds(28))));

        var a = await DetailsAsync(_agentA, offers, InShift);

        Assert.Equal("Availability Unknown", Assert.Single(a.EmployeeResponses!).Outcome);
        Assert.Equal(0, a.EmployeeResponseSummary!.NotResponding);
    }

    [Theory]
    [InlineData(GenesysOfferOutcome.Declined, "Declined")]
    [InlineData(GenesysOfferOutcome.Redirected, "Redirected")]
    [InlineData(GenesysOfferOutcome.CallerAbandoned, "Caller Abandoned")]
    public async Task ConfirmedDeclinedRedirectedOrAbandoned_AreShownSeparately_NeverNotResponding_AndNotScored(GenesysOfferOutcome outcome, string label)
    {
        var offers = new FakeOffers((_agentA, OfferTo(Offer, outcome)));

        var details = await DetailsAsync(_agentA, offers, InShift);

        var row = Assert.Single(details.EmployeeResponses!);
        Assert.Equal(label, row.Outcome);
        Assert.False(row.CountsTowardPerformance);
        Assert.Equal(0, details.EmployeeResponseSummary!.NotResponding);
        Assert.Equal(1, details.EmployeeResponseSummary.Declined + details.EmployeeResponseSummary.Redirected + details.EmployeeResponseSummary.CallerAbandoned);
        await AssertTicketSlaUnchangedAsync(_agentA, details);
    }

    [Fact]
    public async Task DeclinedOutsideTheShift_IsStillOutsideShift()
    {
        var details = await DetailsAsync(_agentA, new FakeOffers((_agentA, OfferTo(Offer, GenesysOfferOutcome.Declined))), new FakeShifts((_, _) => ShiftVerdict.OutsideShift));
        Assert.Equal("Outside Shift", Assert.Single(details.EmployeeResponses!).Outcome);
    }

    [Fact]
    public async Task OffersOutsideThePeriod_AreNotListed()
    {
        var offers = new FakeOffers((_agentA, OfferTo(Now.AddDays(-90), GenesysOfferOutcome.Answered, Now.AddDays(-90).AddSeconds(3))));

        var details = await DetailsAsync(_agentA, offers, InShift);

        Assert.Empty(details.EmployeeResponses!);
    }

    [Fact]
    public async Task NoThresholdExists_ASlowAnswerIsReportedAsMeasured_NotAsABreach()
    {
        var offers = new FakeOffers((_agentA, OfferTo(Offer, GenesysOfferOutcome.Answered, Offer.AddMinutes(47).AddSeconds(5))));

        var details = await DetailsAsync(_agentA, offers, InShift);

        var row = Assert.Single(details.EmployeeResponses!);
        Assert.Equal("Answered", row.Outcome);
        Assert.Equal(47 * 60 + 5d, row.ResponseSeconds);
        Assert.Equal("47 m 5 s", row.ResponseText);
        Assert.Equal(1, details.Summary.BreachEvents); // only the ticket SLA breach the engine recorded
    }

    // ---------------------------------------------------------------
    // The classifier on its own
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(12, "12 s")]
    [InlineData(65, "1 m 5 s")]
    [InlineData(3723, "1 h 2 m 3 s")]
    public void FormatSeconds_IsReadable(double seconds, string expected) =>
        Assert.Equal(expected, TeamPerformanceAppService.FormatSeconds(seconds));
}
