using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// The First Response timestamp cannot be backdated or mislabelled through the API. A person may record a response that happened outside
/// the system (Manual, from the ticket's creation up to now); the system sources are not theirs to claim; undefined sources are rejected;
/// and the history/audit rows say who recorded what, when it happened and when it was entered. One test per supported source.
/// </summary>
public class FirstResponseIntegrityTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = CreatedAt.AddHours(3);   // well past the 1 h first-response deadline

    private sealed class Clock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class Registry(params Guid[] ids) : IServiceIdentityRegistry
    {
        public bool IsServiceIdentity(Guid employeeId) => ids.Contains(employeeId);

        public IReadOnlyCollection<Guid> ServiceIdentityIds => ids;
    }

    private sealed record Harness(SlaServiceFixture Sla, SlaFirstResponseAppService Service, Ticket Ticket, Guid OwnerId, TicketSlaInstance Period);

    private static async Task<Harness> CreateAsync(bool ownerIsServiceIdentity = false)
    {
        var sla = new SlaServiceFixture(timeProvider: new Clock(Now));
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", 2, unitReferenceId: 10, contactReferenceId: 20, categoryId: 5, (byte)PriorityLevel.High, "AC", CreatedAt);
        await sla.Tickets.AddAsync(ticket);
        var owner = Guid.NewGuid();
        ticket.AssignTo(owner);
        ticket.ChangeStatus(TicketStatus.InProgress);
        var period = TicketSlaInstance.OpenInitialPeriod(ticket.TicketId, (byte)PriorityLevel.High, CreatedAt, CreatedAt.AddHours(1), CreatedAt.AddHours(24));
        await sla.SlaInstances.AddAsync(period);

        var service = new SlaFirstResponseAppService(
            sla.Tickets, sla.SlaInstances, sla.DepartmentAssignments, sla.FirstHumanResponse, sla.UnitOfWork, new Clock(Now),
            new Registry(ownerIsServiceIdentity ? [owner] : []));
        return new Harness(sla, service, ticket, owner, period);
    }

    private static RecordFirstResponseRequestDto Request(string source, DateTime? at) => new(source, at, RowVersion: []);

    private static Task<SlaOperationResult<TicketSlaSummaryResponseDto>> Record(Harness h, string source, DateTime? at, string role = Roles.CsAgent) =>
        h.Service.RecordAsync(h.OwnerId, [role], h.Ticket.TicketId, Request(source, at));

    private static void AssertNothingRecorded(Harness h)
    {
        Assert.Null(h.Ticket.FirstHumanResponseAtUtc);
        Assert.False(h.Period.FirstResponseBreached);
    }

    // ----- Manual: the legitimate "answered outside the system" path ---------------------------------------------------------------

    [Fact]
    public async Task Manual_RecordedNow_IsAcceptedAndLateIsStillABreach()
    {
        var h = await CreateAsync();

        var result = await Record(h, "Manual", null);

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.Equal(Now, h.Ticket.FirstHumanResponseAtUtc);
        Assert.True(h.Period.FirstResponseBreached);
    }

    [Fact]
    public async Task Manual_ForARealEarlierMomentAfterTheTicketExisted_IsAccepted_AndOnTimeIsNotABreach()
    {
        var h = await CreateAsync();

        var result = await Record(h, "Manual", CreatedAt.AddMinutes(20));   // e.g. a phone call made 2h40 ago

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.Equal(CreatedAt.AddMinutes(20), h.Ticket.FirstHumanResponseAtUtc);
        Assert.False(h.Period.FirstResponseBreached);
    }

    [Fact]
    public async Task Manual_BeforeTheTicketExisted_IsRejected()
    {
        var h = await CreateAsync();

        var result = await Record(h, "Manual", CreatedAt.AddMinutes(-1));

        Assert.Equal(SlaOperationOutcome.InvalidRequest, result.Outcome);
        AssertNothingRecorded(h);
    }

    [Fact]
    public async Task Manual_InTheFuture_IsRejected()
    {
        var h = await CreateAsync();

        Assert.Equal(SlaOperationOutcome.InvalidRequest, (await Record(h, "Manual", Now.AddHours(1))).Outcome);
        AssertNothingRecorded(h);
    }

    // ----- the other sources are not a back door ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GenesysCallAnswer")]
    [InlineData("2")]
    public async Task GenesysCallAnswer_ByAPerson_IsForbidden_SoItCannotBypassTheCreationBound(string source)
    {
        var h = await CreateAsync();

        var result = await Record(h, source, CreatedAt.AddMinutes(-30));   // backdated to before the ticket, to erase the breach

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        AssertNothingRecorded(h);
        Assert.Empty(h.Sla.Audit.Entries);
    }

    [Fact]
    public async Task GenesysCallAnswer_ByTheIntegrationAccount_KeepsTheExampleEAllowance()
    {
        // SLA-Architecture §16 Example E: a call answered before its ticket existed. Only the configured service identity may claim it.
        var h = await CreateAsync(ownerIsServiceIdentity: true);

        var result = await Record(h, "GenesysCallAnswer", CreatedAt.AddMinutes(-2));

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.Equal(CreatedAt.AddMinutes(-2), h.Ticket.FirstHumanResponseAtUtc);
    }

    [Theory]
    [InlineData("HumanAgentMessage")]
    [InlineData("3")]
    public async Task HumanAgentMessage_IsObservedBySystemFromTheTranscript_NotClaimableThroughTheApi(string source)
    {
        var h = await CreateAsync(ownerIsServiceIdentity: true);

        Assert.Equal(SlaOperationOutcome.InvalidRequest, (await Record(h, source, CreatedAt.AddMinutes(-5))).Outcome);
        AssertNothingRecorded(h);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("Manual,GenesysCallAnswer")]
    [InlineData("")]
    [InlineData("Bot")]
    public async Task UndefinedSources_AreRejected_NotTreatedAsASystemSource(string source)
    {
        var h = await CreateAsync();

        var result = await Record(h, source, CreatedAt.AddMinutes(-30));

        Assert.Equal(SlaOperationOutcome.InvalidRequest, result.Outcome);
        AssertNothingRecorded(h);
    }

    // ----- who may record, and what stops the clock ----------------------------------------------------------------------------------

    [Fact]
    public async Task ANonOwnerAgent_CannotRecord()
    {
        var h = await CreateAsync();

        var result = await h.Service.RecordAsync(Guid.NewGuid(), [Roles.CsAgent], h.Ticket.TicketId, Request("Manual", null));

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        AssertNothingRecorded(h);
    }

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    public async Task SupervisoryRoles_CanRecordOnAnyTicket(string role)
    {
        var h = await CreateAsync();

        var result = await h.Service.RecordAsync(Guid.NewGuid(), [role], h.Ticket.TicketId, Request("Manual", null));

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ItIsWriteOnce_ASecondRecordIsRefused_AndTheFirstTimestampStands()
    {
        var h = await CreateAsync();
        await Record(h, "Manual", CreatedAt.AddMinutes(20));

        var second = await Record(h, "Manual", CreatedAt.AddMinutes(40));

        Assert.Equal(SlaOperationOutcome.FirstResponseAlreadyRecorded, second.Outcome);
        Assert.Equal(CreatedAt.AddMinutes(20), h.Ticket.FirstHumanResponseAtUtc);
    }

    // ----- audit history ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAuditAndHistory_RecordWhoWhatSourceWhenItHappenedAndWhenItWasEntered()
    {
        var h = await CreateAsync();

        await Record(h, "Manual", CreatedAt.AddMinutes(20));

        var audit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "RecordFirstResponse");
        Assert.Equal(h.OwnerId, audit.ActorEmployeeId);
        Assert.Contains($"\"firstHumanResponseAtUtc\":\"{CreatedAt.AddMinutes(20):O}\"", audit.AfterValue);
        Assert.Contains("\"source\":\"Manual\"", audit.AfterValue);
        Assert.Contains($"\"recordedAtUtc\":\"{Now:O}\"", audit.AfterValue);       // the backdating is visible: happened 20 min in, entered 3 h in
        Assert.Contains("\"actorIsSystem\":false", audit.AfterValue);

        var history = Assert.Single(h.Sla.StatusHistory.Added, x => x.Dimension == TicketStatusDimension.SlaState);
        Assert.Equal(h.OwnerId, history.ActorEmployeeId);
        Assert.Contains("(Manual)", history.Note);
        Assert.Contains($"entered at {Now:O}", history.Note);
    }
}
