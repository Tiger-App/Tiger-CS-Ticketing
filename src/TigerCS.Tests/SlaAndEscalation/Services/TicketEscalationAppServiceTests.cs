using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Manual escalation (MVP-API-Contracts.md §5.7, FR-ESC-01, backlog S-11):
/// the authorization matrix, the Level 4 manual-only rule, and the guarantee
/// that escalating changes nothing but the escalation dimension.
/// </summary>
public class TicketEscalationAppServiceTests
{
    private const int TicketDepartmentId = 2;
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(SlaServiceFixture Sla, TicketEscalationAppService Service, Ticket Ticket, Guid OwnerId);

    private static async Task<Harness> CreateAsync(bool assignOwner = true)
    {
        var sla = new SlaServiceFixture();

        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", TicketDepartmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, (byte)PriorityLevel.High, "AC not cooling", CreatedAt);
        await sla.Tickets.AddAsync(ticket);

        var ownerId = Guid.NewGuid();
        if (assignOwner)
        {
            ticket.AssignTo(ownerId);
            ticket.ChangeStatus(TicketStatus.InProgress);
        }

        return new Harness(sla, sla.CreateEscalationService(), ticket, ownerId);
    }

    /// <summary>Puts an employee in a department, so the department-scoped half of the authorization rule has something to read.</summary>
    private static void AssignTo(SlaServiceFixture sla, Guid employeeId, int departmentId) =>
        sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(employeeId, departmentId, isPrimary: true, CreatedAt, assignedByEmployeeId: null));

    private static ManualEscalationRequestDto Request(
        byte level = (byte)EscalationLevel.Level2,
        string triggerType = nameof(EscalationTriggerType.ManualFlag),
        string? note = "Unable to resolve at first contact.") =>
        new(level, triggerType, note, RowVersion: []);

    // -----------------------------------------------------------------
    // Manual escalation succeeds and stays a dimension of its own
    // -----------------------------------------------------------------

    [Fact]
    public async Task Agent_CanRaiseAManualFlagEscalation()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.Equal((byte)EscalationLevel.Level2, result.Response!.Level);
        Assert.Equal(nameof(EscalationTriggerType.ManualFlag), result.Response.TriggerType);
        Assert.Equal(EscalationLevel.Level2, h.Ticket.EscalationLevel);
    }

    /// <summary>
    /// The rule the task states plainly: "an escalated ticket may remain Open
    /// or InProgress". Nothing here closes, resolves, or moves the status.
    /// </summary>
    [Theory]
    [InlineData(true, TicketStatus.InProgress)]
    [InlineData(false, TicketStatus.Open)]
    public async Task Escalating_DoesNotChangeTicketStatus(bool assignOwner, TicketStatus expectedStatus)
    {
        var h = await CreateAsync(assignOwner);

        await h.Service.EscalateAsync(h.OwnerId, [Roles.CsManager], h.Ticket.TicketId, Request());

        Assert.Equal(expectedStatus, h.Ticket.TicketStatus);
        Assert.Equal(EscalationLevel.Level2, h.Ticket.EscalationLevel);
        Assert.Null(h.Ticket.ResolutionOutcome);
    }

    /// <summary>ADR-0011: <c>NotifiedRoles</c> records who should be told, separately from the level itself.</summary>
    [Fact]
    public async Task Escalating_RecordsNotifiedRolesDistinctFromTheLevel()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request(level: (byte)EscalationLevel.Level3));

        Assert.Equal(Roles.GeneralManager, result.Response!.NotifiedRoles);
        Assert.Equal((byte)EscalationLevel.Level3, result.Response.Level);
    }

    // -----------------------------------------------------------------
    // Level 4: manual-only, CS Manager / GM only (MVP-ERD.md §2.17)
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    public async Task Level4_IsPermittedForCsManagerAndGeneralManager(string role)
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            Guid.NewGuid(), [role], h.Ticket.TicketId,
            Request((byte)EscalationLevel.Level4, nameof(EscalationTriggerType.ManualLevel4)));

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.Equal(EscalationLevel.Level4, h.Ticket.EscalationLevel);
    }

    /// <summary>
    /// Every other role is refused — including the ticket's own owner and a
    /// Department Head in its department, neither of whom §2.17's narrow
    /// "CS Manager or GM actor" rule admits.
    /// </summary>
    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task Level4_IsRefusedForEveryOtherRole(string role)
    {
        var h = await CreateAsync();
        AssignTo(h.Sla, h.OwnerId, TicketDepartmentId);

        var result = await h.Service.EscalateAsync(
            h.OwnerId, [role], h.Ticket.TicketId,
            Request((byte)EscalationLevel.Level4, nameof(EscalationTriggerType.ManualLevel4)));

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        Assert.Equal(EscalationLevel.None, h.Ticket.EscalationLevel);
        Assert.Empty(h.Sla.Escalations.All);
    }

    /// <summary>
    /// The bypass this rule needs closing: <c>Level</c> and
    /// <c>TriggerType</c> arrive as separate client-supplied fields, so a
    /// CS Agent sending Level 4 under the ManualFlag trigger would otherwise
    /// reach Level 4 without ever meeting §2.17's role gate.
    /// </summary>
    [Fact]
    public async Task Level4_CannotBeReachedThroughTheManualFlagTrigger()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId,
            Request((byte)EscalationLevel.Level4, nameof(EscalationTriggerType.ManualFlag)));

        Assert.Equal(SlaOperationOutcome.EscalationLevelTriggerMismatch, result.Outcome);
        Assert.Equal(EscalationLevel.None, h.Ticket.EscalationLevel);
        Assert.Empty(h.Sla.Escalations.All);
    }

    /// <summary>And the converse: the ManualLevel4 trigger exists for Level 4 alone, so it cannot be used to slip a lower level past a different check.</summary>
    [Fact]
    public async Task ManualLevel4Trigger_CannotCarryALowerLevel()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            Guid.NewGuid(), [Roles.CsManager], h.Ticket.TicketId,
            Request((byte)EscalationLevel.Level2, nameof(EscalationTriggerType.ManualLevel4)));

        Assert.Equal(SlaOperationOutcome.EscalationLevelTriggerMismatch, result.Outcome);
    }

    // -----------------------------------------------------------------
    // Automatic trigger types are system-only (MVP-API-Contracts.md §5.7)
    // -----------------------------------------------------------------

    /// <summary>
    /// Automatic escalation "is not exposed as a client-callable endpoint,
    /// since it's system-triggered". A caller cannot forge the appearance of
    /// an SLA breach by naming its trigger type.
    /// </summary>
    [Theory]
    [InlineData(nameof(EscalationTriggerType.AutoBreach))]
    [InlineData(nameof(EscalationTriggerType.AutoWindowExpired))]
    [InlineData("NotATriggerType")]
    public async Task AutomaticAndUnknownTriggerTypes_AreRejected(string triggerType)
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            Guid.NewGuid(), [Roles.CsManager], h.Ticket.TicketId, Request(triggerType: triggerType));

        Assert.Equal(SlaOperationOutcome.InvalidRequest, result.Outcome);
        Assert.Empty(h.Sla.Escalations.All);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)5)]
    [InlineData((byte)200)]
    public async Task LevelOutsideTheHierarchy_IsRejected(byte level)
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            Guid.NewGuid(), [Roles.CsManager], h.Ticket.TicketId, Request(level));

        Assert.Equal(SlaOperationOutcome.InvalidRequest, result.Outcome);
    }

    // -----------------------------------------------------------------
    // Levels only rise (ADR-0011)
    // -----------------------------------------------------------------

    [Fact]
    public async Task EscalationLevel_CannotBeLoweredByAManualRequest()
    {
        var h = await CreateAsync();
        await h.Service.EscalateAsync(
            Guid.NewGuid(), [Roles.CsManager], h.Ticket.TicketId,
            Request((byte)EscalationLevel.Level4, nameof(EscalationTriggerType.ManualLevel4)));

        var result = await h.Service.EscalateAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request((byte)EscalationLevel.Level2));

        Assert.Equal(SlaOperationOutcome.EscalationLevelNotAnAdvance, result.Outcome);
        Assert.Equal(EscalationLevel.Level4, h.Ticket.EscalationLevel);
        Assert.Single(h.Sla.Escalations.All);
    }

    // -----------------------------------------------------------------
    // Authorization matrix (MVP-API-Contracts.md §5.7, "Agent and above")
    // -----------------------------------------------------------------

    /// <summary>The CS layer is inherently cross-department (Security-Architecture.md §3), so it needs no membership in the ticket's department.</summary>
    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    public async Task CsLayerAndAbove_MayEscalateWithoutDepartmentMembership(string role)
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(Guid.NewGuid(), [role], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
    }

    /// <summary>Chairman/CEO only receives Level 4; it initiates no escalation at any level.</summary>
    [Theory]
    [InlineData(Roles.ChairmanCeo, 1, nameof(EscalationTriggerType.ManualFlag))]
    [InlineData(Roles.ChairmanCeo, 3, nameof(EscalationTriggerType.ManualFlag))]
    [InlineData(Roles.ReportingUser, 1, nameof(EscalationTriggerType.ManualFlag))]
    public async Task ReadOnlyRoles_MayNotEscalateAtAnyLevel(string role, byte level, string trigger)
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(Guid.NewGuid(), [role], h.Ticket.TicketId, Request(level, trigger));

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        Assert.Empty(h.Sla.Escalations.All);
    }

    /// <summary>Department-side roles are scoped to the ticket's own department.</summary>
    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    public async Task DepartmentRoles_MayEscalateOnlyWithinTheirOwnDepartment(string role)
    {
        var outsider = Guid.NewGuid();

        var inside = await CreateAsync();
        AssignTo(inside.Sla, outsider, TicketDepartmentId);
        if (role == Roles.DepartmentEmployee)
        {
            // A Department Employee acts only on tickets assigned to them (department membership alone shows them nothing).
            var unassignedColleagueView = await inside.Service.EscalateAsync(outsider, [role], inside.Ticket.TicketId, Request());
            Assert.Equal(SlaOperationOutcome.Forbidden, unassignedColleagueView.Outcome);
            inside.Ticket.AssignTo(outsider);
        }

        var permitted = await inside.Service.EscalateAsync(outsider, [role], inside.Ticket.TicketId, Request());

        var outside = await CreateAsync();
        AssignTo(outside.Sla, outsider, TicketDepartmentId + 99);
        var refused = await outside.Service.EscalateAsync(outsider, [role], outside.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, permitted.Outcome);
        Assert.Equal(SlaOperationOutcome.Forbidden, refused.Outcome);
    }

    /// <summary>Solution-Analysis.md §4.1 grants the Reporting User "no ticket actions" — this increment must not weaken that.</summary>
    [Fact]
    public async Task ReportingUser_MayNotEscalate()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(Guid.NewGuid(), [Roles.ReportingUser], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        Assert.Empty(h.Sla.Escalations.All);
    }

    /// <summary>A caller holding no role at all is refused — the role list comes from the caller's own validated claims, so an empty one grants nothing.</summary>
    [Fact]
    public async Task CallerWithNoRoles_MayNotEscalate()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(Guid.NewGuid(), [], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
    }

    /// <summary>
    /// ADR-0024's central override, reaching a module written after it. The
    /// administrator holds none of the roles in <c>SlaRoleSets</c> and no line
    /// of this module names the role — it passes purely through
    /// <c>AuthorizationGate</c>, on the narrowest gate in the module.
    /// </summary>
    [Fact]
    public async Task SystemAdministrator_PassesEvenTheLevel4Gate()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], h.Ticket.TicketId,
            Request((byte)EscalationLevel.Level4, nameof(EscalationTriggerType.ManualLevel4)));

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.DoesNotContain(Roles.SystemAdministrator, SlaRoleSets.ManualLevel4Escalate);
        Assert.DoesNotContain(Roles.SystemAdministrator, SlaRoleSets.ManualEscalate);
    }

    // -----------------------------------------------------------------
    // Business rules that must remain enforced
    // -----------------------------------------------------------------

    [Fact]
    public async Task ClosedTicket_CannotBeEscalated()
    {
        var h = await CreateAsync();
        h.Ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        h.Ticket.Close();

        var result = await h.Service.EscalateAsync(h.OwnerId, [Roles.CsManager], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.TicketClosed, result.Outcome);
        Assert.Empty(h.Sla.Escalations.All);
        Assert.Equal(0, h.Sla.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task UnknownTicket_ReturnsNotFound()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(h.OwnerId, [Roles.CsManager], ticketId: 9999, Request());

        Assert.Equal(SlaOperationOutcome.NotFound, result.Outcome);
    }

    /// <summary>Optimistic concurrency: a lost race on the ticket's RowVersion is reported, not silently overwritten.</summary>
    [Fact]
    public async Task LostConcurrencyRace_IsReportedAsAConflict()
    {
        var h = await CreateAsync();
        h.Sla.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await h.Service.EscalateAsync(h.OwnerId, [Roles.CsManager], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(0, h.Sla.UnitOfWork.TransactionsCommitted);
    }

    // -----------------------------------------------------------------
    // Audit and history (ADR-0018)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Escalating_WritesAnAuditEntryAttributedToTheActor()
    {
        var h = await CreateAsync();
        var actorId = Guid.NewGuid();

        await h.Service.EscalateAsync(actorId, [Roles.CsManager], h.Ticket.TicketId, Request());

        var audit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "EscalateTicket");
        Assert.Equal(actorId, audit.ActorEmployeeId);
        Assert.Equal(h.Ticket.TicketId.ToString(), audit.EntityId);
        Assert.Contains("Level2", audit.AfterValue!, StringComparison.Ordinal);
        Assert.Contains("None", audit.BeforeValue!, StringComparison.Ordinal);
    }

    /// <summary>The escalation dimension's own change reaches the ticket history, attributed to a person rather than the system.</summary>
    [Fact]
    public async Task Escalating_WritesEscalationLevelHistory()
    {
        var h = await CreateAsync();
        var actorId = Guid.NewGuid();

        await h.Service.EscalateAsync(actorId, [Roles.CsManager], h.Ticket.TicketId, Request(note: "Needs department head."));

        var row = Assert.Single(h.Sla.StatusHistory.Added, r => r.Dimension == TicketStatusDimension.EscalationLevel);
        Assert.False(row.ActorIsSystem);
        Assert.Equal(actorId, row.ActorEmployeeId);
        Assert.Equal((byte)EscalationLevel.None, row.OldValue);
        Assert.Equal((byte)EscalationLevel.Level2, row.NewValue);
        Assert.Equal("Needs department head.", row.Note);
    }
}
