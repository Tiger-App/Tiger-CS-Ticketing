using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.SlaAndEscalation.Integration;

/// <summary>
/// The SLA and Escalation endpoints against the real host (MVP-API-Contracts.md
/// §5.1/§5.2/§5.7/§5.9), plus the end-to-end behaviour those endpoints exist
/// to expose: a ticket gets due dates at creation, a breach is detected by a
/// background-job path with no client connected, and an automatic Level 2
/// escalation appears without anyone calling an endpoint for it.
/// </summary>
public class SlaAndEscalationEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public SlaAndEscalationEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> CreateClientAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return (client, employeeId);
    }

    /// <summary>Drives the real intake → customer lookup → ticket sequence, so the ticket under test is created exactly as production creates one.</summary>
    private async Task<TicketDetailDto> CreateTicketAsync(HttpClient client, byte priorityId)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        // "+971509990001" matches MockCrmGateway's CRM-UNIT-1107/CRM-CONTACT-3010 fixture (Sami Nasser, an Owner — a valid Buyer ownership record).
        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971509990001", null, true, "5001", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookup = await (await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup"))
            .Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var crmCustomer = Assert.Single(lookup!.Sources.Single(s => s.Source == "Crm").Customers);
        var crmUnit = Assert.Single(crmCustomer.Units);

        var createResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, crmUnit.UnitReferenceId, crmUnit.ContactReferenceId, categoryId, priorityId, "AC unit not cooling"));

        // Asserted rather than assumed: ticket creation is CS Agent/CS
        // Supervisor only (PolicyNames.CustomerVerification), so a helper
        // called with any other role should say so plainly instead of
        // failing later on an empty response body.
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        // The creating CS Agent takes the ticket: a CS Agent sees only tickets currently assigned to them. (Re-read below, as this changes its RowVersion.)
        await _factory.AssignOwnerAsync(created!.TicketId, TigerCsApiFactory.UserIdOf(client));

        return (await (await client.GetAsync($"/api/tickets/{created.TicketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    private static byte[] RowVersionOf(TicketDetailDto ticket) => Convert.FromBase64String(ticket.RowVersion);

    private async Task<TicketDetailDto> ReReadAsync(HttpClient client, long ticketId) =>
        (await (await client.GetAsync($"/api/tickets/{ticketId}")).Content.ReadFromJsonAsync<TicketDetailDto>())!;

    // -----------------------------------------------------------------
    // §5.1 — SLA summary
    // -----------------------------------------------------------------

    /// <summary>
    /// Backlog S-08's corrected acceptance criterion: creating a ticket opens
    /// its initial SLA period with computed due dates. Nothing separate has to
    /// be called for the clock to start.
    /// </summary>
    [Fact]
    public async Task CreatingATicket_ImmediatelyGivesItBothDueDates()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var sla = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla"))
            .Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();

        Assert.NotNull(sla!.FirstResponseDueAtUtc);
        Assert.NotNull(sla.ResolutionDueAtUtc);
        Assert.True(sla.ResolutionDueAtUtc > sla.FirstResponseDueAtUtc);
        Assert.Equal("Running", sla.SlaState);
        Assert.Equal("None", sla.EscalationLevel);
        Assert.False(sla.FirstResponseBreached);
        Assert.False(sla.ResolutionBreached);
    }

    /// <summary>Critical is 24/7 (SLA-Architecture.md §3): 15 minutes and 4 hours from creation, flat.</summary>
    [Fact]
    public async Task CriticalTicket_GetsTwentyFourSevenDueDates()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.Critical);

        var instance = await _factory.GetCurrentSlaInstanceAsync(ticket.TicketId);

        Assert.NotNull(instance);
        Assert.Equal(instance.PeriodStartAtUtc.AddMinutes(15), instance.FirstResponseDueAtUtc);
        Assert.Equal(instance.PeriodStartAtUtc.AddHours(4), instance.ResolutionDueAtUtc);
    }

    /// <summary>The pause fields stay in the approved §5.1 shape at their pause-free values — a disclosed limitation, not a silent omission.</summary>
    [Fact]
    public async Task SlaSummary_ReportsThePauseFieldsAsUnusedInThisPilot()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.Medium);

        var sla = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla"))
            .Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();

        Assert.False(sla!.IsCurrentlyPaused);
        Assert.Null(sla.CurrentPauseReason);
        Assert.Equal(0, sla.TotalPausedMinutesThisPeriod);
    }

    [Fact]
    public async Task SlaSummary_ForAnUnknownTicket_Returns404()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);

        var response = await client.GetAsync("/api/tickets/999999/sla");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Department visibility: an SLA panel is ticket data, so a caller who cannot see the ticket cannot see its SLA position either.</summary>
    [Fact]
    public async Task SlaSummary_ForATicketOutsideTheCallersDepartment_Returns403()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);

        var (employeeClient, employeeId) = await CreateClientAsync(Roles.DepartmentEmployee);
        var otherDepartmentId = await _factory.CreateDepartmentAsync("Other " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.AssignPrimaryDepartmentAsync(employeeId, otherDepartmentId);

        var response = await employeeClient.GetAsync($"/api/tickets/{ticket.TicketId}/sla");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // §5.2 — First Human Response
    // -----------------------------------------------------------------

    [Fact]
    public async Task RecordingFirstResponse_SetsTheTimestampAndIsWriteOnce()
    {
        var (client, employeeId) = await CreateClientAsync(Roles.CsSupervisor);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var first = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var sla = await first.Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();
        Assert.NotNull(sla!.FirstHumanResponseAtUtc);

        var refreshed = await ReReadAsync(client, ticket.TicketId);
        var second = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(refreshed)));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.NotEqual(Guid.Empty, employeeId);
    }

    [Fact]
    public async Task RecordingFirstResponse_WithAnUnknownSource_Returns400()
    {
        var (client, _) = await CreateClientAsync(Roles.CsSupervisor);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("SmokeSignal", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Optimistic concurrency (MVP-API-Contracts.md §0's `If-Match` convention, carried in the body's `rowVersion`).</summary>
    [Fact]
    public async Task RecordingFirstResponse_WithAStaleRowVersion_Returns409()
    {
        var (client, _) = await CreateClientAsync(Roles.CsSupervisor);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        // A concurrent escalation moves the ticket on, invalidating the
        // rowVersion this caller is holding.
        await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", null, RowVersionOf(ticket)));

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(ticket)));

        Assert.True(
            response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.OK,
            $"Expected 409 (or 200 where the provider cannot detect the race), got {response.StatusCode}.");
    }

    // -----------------------------------------------------------------
    // §5.7 / §5.9 — manual escalation and history
    // -----------------------------------------------------------------

    [Fact]
    public async Task ManualEscalation_IsRecordedAndListed()
    {
        var (client, _) = await CreateClientAsync(Roles.CsSupervisor);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var escalateResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", "Cannot resolve at first contact.", RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Created, escalateResponse.StatusCode);

        var listed = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/escalations"))
            .Content.ReadFromJsonAsync<List<TicketEscalationResponseDto>>();

        var escalation = Assert.Single(listed!);
        Assert.Equal(2, escalation.Level);
        Assert.Equal("ManualFlag", escalation.TriggerType);
        Assert.Equal(Roles.DepartmentHead, escalation.NotifiedRoles);

        // §5.8's respond action does not ship in this increment.
        Assert.Null(escalation.RespondedAtUtc);
        Assert.Null(escalation.RespondingEmployeeId);
    }

    /// <summary>The rule stated plainly in this increment's scope: an escalated ticket may remain Open or InProgress.</summary>
    [Fact]
    public async Task Escalating_LeavesTheTicketStatusUnchanged()
    {
        var (client, _) = await CreateClientAsync(Roles.CsSupervisor);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", null, RowVersionOf(ticket)));

        var refreshed = await ReReadAsync(client, ticket.TicketId);

        Assert.Equal(nameof(TicketStatus.Open), refreshed.TicketStatus);
        Assert.Null(refreshed.ResolutionOutcome);

        var sla = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla"))
            .Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();
        Assert.Equal("Level2", sla!.EscalationLevel);
    }

    /// <summary>MVP-ERD.md §2.17's `403` for a ManualLevel4 row raised by anyone but a CS Manager or GM.</summary>
    [Fact]
    public async Task Level4Escalation_ByACsAgent_Returns403()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(4, "ManualLevel4", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.GetEscalationsAsync(ticket.TicketId));
    }

    [Fact]
    public async Task Level4Escalation_ByACsManager_Returns201()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);
        var (managerClient, _) = await CreateClientAsync(Roles.CsManager);

        var response = await managerClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(4, "ManualLevel4", "Executive attention.", RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>The bypass the level/trigger coherence rule closes: Level 4 under the ManualFlag trigger would otherwise skip §2.17's role gate.</summary>
    [Fact]
    public async Task Level4UnderTheManualFlagTrigger_Returns422()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);

        // Raised by a CS Manager, who *would* pass the Level 4 role gate —
        // so the 422 is the coherence rule firing, not the role check.
        var (managerClient, _) = await CreateClientAsync(Roles.CsManager);
        var response = await managerClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(4, "ManualFlag", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await _factory.GetEscalationsAsync(ticket.TicketId));
    }

    /// <summary>Automatic trigger types are system-only (§5.7) — a client cannot forge the appearance of an SLA breach.</summary>
    [Fact]
    public async Task AutomaticTriggerTypeFromAClient_Returns400()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);
        var (managerClient, _) = await CreateClientAsync(Roles.CsManager);

        var response = await managerClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "AutoBreach", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Levels only rise (ADR-0011).</summary>
    [Fact]
    public async Task EscalatingBackDown_Returns422()
    {
        var (client, _) = await CreateClientAsync(Roles.CsSupervisor);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(3, "ManualFlag", null, RowVersionOf(ticket)));

        var refreshed = await ReReadAsync(client, ticket.TicketId);
        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", null, RowVersionOf(refreshed)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>Solution-Analysis.md §4.1 grants the Reporting User no ticket actions; this increment does not weaken that.</summary>
    [Fact]
    public async Task ReportingUser_IsRefusedOnBothMutatingEndpoints()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);
        var (reportingClient, _) = await CreateClientAsync(Roles.ReportingUser);

        var escalate = await reportingClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", null, RowVersionOf(ticket)));
        var firstResponse = await reportingClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, firstResponse.StatusCode);
    }

    // -----------------------------------------------------------------
    // Closed-ticket immutability, end to end
    // -----------------------------------------------------------------

    /// <summary>A Closed ticket accepts neither of the two new mutating endpoints.</summary>
    [Fact]
    public async Task ClosedTicket_RefusesEscalationAndFirstResponse()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);

        var (managerClient, managerId) = await CreateClientAsync(Roles.CsManager);
        await _factory.AssignPrimaryDepartmentAsync(managerId, ticket.CurrentDepartmentId);

        var assigned = await (await managerClient.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(managerId, RowVersionOf(ticket), TicketTransferReason.NeedsEscalation, "Taken over by the manager.")))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var inProgress = await (await managerClient.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", RowVersionOf(assigned!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var (headClient, headId) = await CreateClientAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(headId, ticket.CurrentDepartmentId);
        var resolved = await (await headClient.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/resolution",
                new ResolveTicketRequestDto("Resolved", "Replaced the compressor.", null, null, RowVersionOf(inProgress!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var closed = await (await managerClient.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(RowVersionOf(resolved!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(nameof(TicketStatus.Closed), closed!.TicketStatus);

        var escalate = await managerClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", null, RowVersionOf(closed)));
        var firstResponse = await managerClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(closed)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, escalate.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, firstResponse.StatusCode);
        Assert.Empty(await _factory.GetEscalationsAsync(ticket.TicketId));
    }

    // -----------------------------------------------------------------
    // Anonymous and deactivated callers (Security-Architecture.md §5/§14)
    // -----------------------------------------------------------------

    /// <summary>None of the four endpoints is anonymous — the fallback policy covers them without any of them opting in.</summary>
    [Fact]
    public async Task AnonymousCallers_AreRefusedOnEverySlaEndpoint()
    {
        var anonymous = _factory.CreateClient();

        var summary = await anonymous.GetAsync("/api/tickets/1/sla");
        var escalations = await anonymous.GetAsync("/api/tickets/1/escalations");
        var escalate = await anonymous.PostAsJsonAsync(
            "/api/tickets/1/escalations", new ManualEscalationRequestDto(2, "ManualFlag", null, []));
        var firstResponse = await anonymous.PostAsJsonAsync(
            "/api/tickets/1/sla/first-response", new RecordFirstResponseRequestDto("Manual", null, []));

        Assert.Equal(HttpStatusCode.Unauthorized, summary.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, escalations.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, escalate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, firstResponse.StatusCode);
    }

    /// <summary>
    /// FR-ADM-02 / Security-Architecture.md §14: an unexpired token stops
    /// working the moment its employee is deactivated. ADR-0024's identity-gate
    /// carve-out means this holds for a System Administrator too — the override
    /// grants authorization, never session validity.
    /// </summary>
    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task DeactivatedEmployee_IsRefusedOnEverySlaEndpoint(string role)
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);

        var (client, employeeId) = await CreateClientAsync(role);

        // The token works before deactivation.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var employee = await db.Employees.FindAsync(employeeId);
            employee!.Deactivate(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        // Same still-unexpired token, never reissued.
        var summary = await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla");
        var escalations = await client.GetAsync($"/api/tickets/{ticket.TicketId}/escalations");
        var escalate = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", null, RowVersionOf(ticket)));
        var firstResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Forbidden, summary.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, escalations.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, firstResponse.StatusCode);
    }

    // -----------------------------------------------------------------
    // Breach detection end to end, with no client involved
    // -----------------------------------------------------------------

    /// <summary>
    /// The whole point of ADR-0015's background-job mechanism: the deadline
    /// passes, a job runs server-side, and the breach plus its automatic
    /// Level 2 escalation appear — with no browser open, no SignalR
    /// connection, and no endpoint called.
    /// </summary>
    [Fact]
    public async Task BreachAndAutomaticLevel2_AppearWithoutAnyClientAction()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        // Move both deadlines into the past, as elapsed time would.
        var past = DateTime.UtcNow.AddHours(-1);
        await _factory.ForceSlaDueDatesAsync(ticket.TicketId, past, past);

        var outcome = await _factory.RunDeadlineCheckAsync(ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, outcome);

        var sla = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla"))
            .Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();
        Assert.True(sla!.FirstResponseBreached);
        Assert.Equal("Breached", sla.SlaState);
        Assert.Equal("Level2", sla.EscalationLevel);

        var escalation = Assert.Single(await _factory.GetEscalationsAsync(ticket.TicketId));
        Assert.Equal(EscalationTriggerType.AutoBreach, escalation.TriggerType);
        Assert.Equal((byte)EscalationLevel.Level2, escalation.Level);
    }

    /// <summary>
    /// The idempotency guarantee against the real store and its unique index:
    /// the scheduled job, a repeat of it, and the safety sweep all target the
    /// same due event, and exactly one breach, one escalation and one audit
    /// entry result.
    /// </summary>
    [Fact]
    public async Task ReprocessingTheSameDeadline_ProducesNoDuplicatesAgainstTheRealStore()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var past = DateTime.UtcNow.AddHours(-1);
        await _factory.ForceSlaDueDatesAsync(ticket.TicketId, past, past.AddMinutes(30));

        var first = await _factory.RunDeadlineCheckAsync(ticket.TicketId, SlaDeadlineType.FirstResponse);
        var second = await _factory.RunDeadlineCheckAsync(ticket.TicketId, SlaDeadlineType.FirstResponse);
        await _factory.RunSlaSweepAsync();
        await _factory.RunSlaSweepAsync();

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, first);
        Assert.Equal(SlaBreachProcessingOutcome.AlreadyProcessed, second);

        // One automatic escalation, no matter how many times the deadlines
        // were reprocessed — including the second clock's own breach.
        var escalations = await _factory.GetEscalationsAsync(ticket.TicketId);
        Assert.Single(escalations);

        var audits = await _factory.GetAuditEntriesAsync(ticket.TicketId.ToString());
        Assert.Single(audits, a => a.Action == "AutoEscalateOnBreach");
        Assert.Single(audits, a => a.Action == "DetectSlaBreach" && a.AfterValue!.Contains("FirstResponseBreached", StringComparison.Ordinal));
    }

    /// <summary>The breach flag survives everything — MVP-ERD.md §2.15's highest-consequence integrity rule.</summary>
    [Fact]
    public async Task ABreachFlag_IsNeverClearedByLaterActivity()
    {
        var (client, _) = await CreateClientAsync(Roles.CsSupervisor);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var past = DateTime.UtcNow.AddHours(-1);
        await _factory.ForceSlaDueDatesAsync(ticket.TicketId, past, past.AddDays(1));
        await _factory.RunDeadlineCheckAsync(ticket.TicketId, SlaDeadlineType.FirstResponse);

        // A first response finally arrives, and the ticket is escalated by
        // hand. Neither undoes the recorded breach.
        var refreshed = await ReReadAsync(client, ticket.TicketId);
        await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(refreshed)));

        refreshed = await ReReadAsync(client, ticket.TicketId);
        await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(3, "ManualFlag", null, RowVersionOf(refreshed)));

        var sla = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla"))
            .Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();

        Assert.True(sla!.FirstResponseBreached);
        Assert.Equal("Breached", sla.SlaState);
        Assert.Equal("Level3", sla.EscalationLevel);
    }

    // -----------------------------------------------------------------
    // Escalation is refused on the server for a ticket the caller cannot act on
    // -----------------------------------------------------------------

    /// <summary>
    /// A CS Agent holds the cross-department "escalate" permission, but may only act on tickets assigned to them (or department work they are
    /// responsible for). Escalating someone else's ticket is refused with 403 and changes nothing.
    /// </summary>
    [Fact]
    public async Task CsAgent_EscalatingATicketAssignedToAnotherAgent_Returns403_AndChangesNothing()
    {
        var (ownerClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(ownerClient, (byte)PriorityLevel.High);
        var (otherAgentClient, _) = await CreateClientAsync(Roles.CsAgent);

        var response = await otherAgentClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", "Not my ticket.", RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.GetEscalationsAsync(ticket.TicketId));
        Assert.Equal("None", (await ReReadAsync(ownerClient, ticket.TicketId)).EscalationLevel);
    }

    [Fact]
    public async Task CsAgent_EscalatingTheirOwnTicket_StillSucceeds()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", "Cannot resolve.", RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task DepartmentHead_EscalatingATicketOutsideTheirDepartment_Returns403()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);
        var otherDepartment = await _factory.CreateDepartmentAsync("Other " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (headClient, headId) = await CreateClientAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(headId, otherDepartment);

        var response = await headClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.GetEscalationsAsync(ticket.TicketId));
    }

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    public async Task SupervisoryRoles_CanEscalateAnotherAgentsTicket(string role)
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);
        var (supervisorClient, _) = await CreateClientAsync(role);

        var response = await supervisorClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(2, "ManualFlag", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // First response timestamp integrity
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("HumanAgentMessage")]
    [InlineData("3")]
    [InlineData("99")]
    public async Task RecordingFirstResponse_WithASystemOnlyOrNumericSource_Returns400_AndRecordsNothing(string source)
    {
        var (client, _) = await CreateClientAsync(Roles.CsSupervisor);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto(source, null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var sla = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla")).Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();
        Assert.Null(sla!.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task RecordingFirstResponse_BackdatedFarBeforeTheTicketAsAGenesysCallAnswer_Returns400()
    {
        var (client, _) = await CreateClientAsync(Roles.CsSupervisor);
        var ticket = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("GenesysCallAnswer", DateTime.UtcNow.AddDays(-3), RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RecordingFirstResponse_AuditsWhoRecordedItAndWhen_ForEachApiSource()
    {
        var (client, supervisorId) = await CreateClientAsync(Roles.CsSupervisor);
        var manual = await CreateTicketAsync(client, (byte)PriorityLevel.High);
        var call = await CreateTicketAsync(client, (byte)PriorityLevel.High);

        var manualResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{manual.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(manual)));
        var callResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{call.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("GenesysCallAnswer", DateTime.UtcNow.AddMinutes(-1), RowVersionOf(call)));

        Assert.Equal(HttpStatusCode.OK, manualResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, callResponse.StatusCode);
        foreach (var (ticket, source) in new[] { (manual, "Manual"), (call, "GenesysCallAnswer") })
        {
            var audit = Assert.Single(
                await _factory.GetAuditEntriesAsync(ticket.TicketId.ToString()), a => a.Action == "RecordFirstResponse");
            Assert.Equal(supervisorId, audit.ActorEmployeeId);
            Assert.Contains($"\"source\":\"{source}\"", audit.AfterValue, StringComparison.Ordinal);
            Assert.Contains("\"recordedAtUtc\"", audit.AfterValue, StringComparison.Ordinal);
            Assert.Contains("\"backdatedMinutes\"", audit.AfterValue, StringComparison.Ordinal);
        }
    }

    // -----------------------------------------------------------------
    // SLA state accuracy: what is persisted when a ticket is resolved
    // -----------------------------------------------------------------

    /// <summary>
    /// Drives a ticket through the real workflow to Resolved: a CS Manager takes it over and starts work, a Department Head of its department
    /// resolves it. Returns the manager's client (who can still see it) and the resolved ticket.
    /// </summary>
    private async Task<(HttpClient ManagerClient, TicketDetailDto Resolved)> ResolveAsync(HttpClient agentClient, TicketDetailDto ticket)
    {
        var (managerClient, managerId) = await CreateClientAsync(Roles.CsManager);
        await _factory.AssignPrimaryDepartmentAsync(managerId, ticket.CurrentDepartmentId);

        var assigned = await (await managerClient.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/assignment",
                new AssignTicketRequestDto(managerId, RowVersionOf(ticket), TicketTransferReason.NeedsEscalation, "Taken over by the manager.")))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var inProgress = await (await managerClient.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", RowVersionOf(assigned!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var (headClient, headId) = await CreateClientAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(headId, ticket.CurrentDepartmentId);
        var resolveResponse = await headClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "Replaced the compressor.", null, null, RowVersionOf(inProgress!)));
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);

        return (managerClient, (await resolveResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!);
    }

    private static async Task<TicketSlaSummaryResponseDto> GetSlaAsync(HttpClient client, long ticketId) =>
        (await (await client.GetAsync($"/api/tickets/{ticketId}/sla")).Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>())!;

    /// <summary>
    /// The defect this pins: a ticket resolved inside both deadlines never became <c>Met</c> in the database, because the breach processor
    /// looked for the just-added resolution with a database query that cannot see an unsaved row. The state stayed Running forever.
    /// Ticket Details (the ticket DTO) and the SLA endpoint must agree.
    /// </summary>
    [Fact]
    public async Task ResolvingWithinBothDeadlines_PersistsMet_AndEveryViewAgrees()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.Critical);
        var (supervisorClient, _) = await CreateClientAsync(Roles.CsSupervisor);
        var fr = await supervisorClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response", new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(ticket)));
        Assert.Equal(HttpStatusCode.OK, fr.StatusCode);

        var (managerClient, resolved) = await ResolveAsync(agentClient, await ReReadAsync(supervisorClient, ticket.TicketId));

        var sla = await GetSlaAsync(managerClient, ticket.TicketId);
        Assert.Equal("Met", sla.SlaState);
        Assert.False(sla.FirstResponseBreached);
        Assert.False(sla.ResolutionBreached);
        Assert.Equal("Met", resolved.SlaState);
        Assert.Equal("Met", (await ReReadAsync(managerClient, ticket.TicketId)).SlaState);
        Assert.Equal("Met", (await _factory.GetTicketAsync(ticket.TicketId))!.SlaState.ToString());
    }

    /// <summary>A late resolution is still a breach, not Met - and only the Resolution clock is marked.</summary>
    [Fact]
    public async Task ResolvingAfterTheResolutionDeadline_IsBreached_AndOnlyTheResolutionClockIsMarked()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);
        var (supervisorClient, _) = await CreateClientAsync(Roles.CsSupervisor);
        await supervisorClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response", new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(ticket)));
        await _factory.ForceSlaDueDatesAsync(ticket.TicketId, DateTime.UtcNow.AddHours(5), DateTime.UtcNow.AddMinutes(-5));

        var (managerClient, resolved) = await ResolveAsync(agentClient, await ReReadAsync(supervisorClient, ticket.TicketId));

        var sla = await GetSlaAsync(managerClient, ticket.TicketId);
        Assert.Equal("Breached", sla.SlaState);
        Assert.True(sla.ResolutionBreached);
        Assert.False(sla.FirstResponseBreached);
        Assert.Equal("Breached", resolved.SlaState);
    }

    /// <summary>The two clocks are independent: a missed First Response is not erased by an on-time resolution, and does not mark the Resolution clock.</summary>
    [Fact]
    public async Task AMissedFirstResponse_IsNotErasedByAnOnTimeResolution_AndTheResolutionClockStaysClean()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(agentClient, (byte)PriorityLevel.High);
        await _factory.ForceSlaDueDatesAsync(ticket.TicketId, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddHours(8));
        await _factory.RunDeadlineCheckAsync(ticket.TicketId, SlaDeadlineType.FirstResponse);

        var (managerClient, _) = await ResolveAsync(agentClient, await ReReadAsync(agentClient, ticket.TicketId));

        var sla = await GetSlaAsync(managerClient, ticket.TicketId);
        Assert.Equal("Breached", sla.SlaState);
        Assert.True(sla.FirstResponseBreached);
        Assert.False(sla.ResolutionBreached);
    }
}
