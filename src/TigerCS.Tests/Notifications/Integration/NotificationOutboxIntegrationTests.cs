using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Notifications.Integration;

/// <summary>
/// The whole flow against the real host and the real EF-backed writer,
/// dispatcher, handler and email adapter: create a ticket through the real
/// intake → CRM lookup → verification → ticket sequence, then run the
/// dispatcher exactly as the recurring Hangfire job would.
/// </summary>
public class NotificationOutboxIntegrationTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public NotificationOutboxIntegrationTests(TigerCsApiFactory factory)
    {
        _factory = factory;
        _factory.EmailSender.Clear();
    }

    /// <summary>CRM-CONTACT-2001's channel is an email address; CRM-CONTACT-2002's is a phone number. Both fixtures are real MockCrmGateway data, not invented for this test.</summary>
    private const int EmailContactIndex = 0;
    private const int PhoneContactIndex = 1;

    private async Task<(HttpClient Client, Guid EmployeeId)> CreateClientAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return (client, employeeId);
    }

    private async Task<TicketResponseDto> CreateTicketAsync(HttpClient client, int contactIndex = EmailContactIndex)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        // Resolved directly via the CRM unit-lookup endpoints (unchanged),
        // not customer lookup, so this test can pick a specific contact by
        // index (email vs phone channel) rather than whichever one the
        // intake's own phone number happens to match.
        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();

        var createResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake!.IntakeRecordId, unit!.UnitReferenceId, contacts![contactIndex].ContactReferenceId, categoryId,
                (byte)PriorityLevel.High, "AC unit not cooling"));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<TicketResponseDto>())!;
        await _factory.RecordHandlerAsync(created.TicketId, client);
        return created;
    }

    [Fact]
    public async Task CreatingATicket_CommitsAPendingOutboxMessageAndItsIdempotencyRecord()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client);

        var messages = await _factory.GetOutboxMessagesAsync();
        var message = Assert.Single(messages, m =>
            m.Payload.Contains($"\"ticketId\":{ticket.TicketId}", StringComparison.Ordinal));

        Assert.Equal(OutboxEventTypes.TicketCreated, message.EventType);
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(0, message.Attempts);
        Assert.Null(message.ProcessedAtUtc);

        // MVP-ERD.md §2.23's required FK — reserved in the same transaction,
        // which is what makes the unique index a real dedup guarantee.
        Assert.NotEqual(0, message.IdempotencyRecordId);

        var records = await _factory.GetIdempotencyRecordsAsync(IdempotencyScopes.OutboxDispatch);
        Assert.Contains(records, r =>
            r.IdempotencyKey == OutboxEventTypes.IdempotencyKeyFor(
                OutboxEventTypes.TicketCreated, ticket.TicketId, OutboxEventTypes.TicketCreatedVersion));

        // Nothing is sent from the request handler (NFR-REL-01).
        Assert.DoesNotContain(_factory.EmailSender.Recorded, e => e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatcherPass_SendsTheAcknowledgementAndMarksEverythingTerminal()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client);

        await _factory.RunOutboxDispatchAsync();

        var delivered = Assert.Single(
            _factory.EmailSender.Recorded, e => e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));
        Assert.Equal("ahmed.alfarsi@example.com", delivered.ToAddress);
        Assert.Contains(ticket.TicketNumber, delivered.Body, StringComparison.Ordinal);

        var notification = Assert.Single(await _factory.GetNotificationsAsync(ticket.TicketId));
        Assert.Equal(NotificationDeliveryStatus.Sent, notification.DeliveryStatus);
        Assert.Equal(NotificationType.Acknowledgement, notification.NotificationType);
        Assert.Equal("ahmed.alfarsi@example.com", notification.RecipientAddress);

        var stored = await _factory.GetTicketAsync(ticket.TicketId);
        Assert.NotNull(stored!.AcknowledgementSentAtUtc);

        var message = Assert.Single(
            await _factory.GetOutboxMessagesAsync(),
            m => m.Payload.Contains($"\"ticketId\":{ticket.TicketId}", StringComparison.Ordinal));
        Assert.Equal(OutboxMessageStatus.Processed, message.Status);
        Assert.Equal(1, message.Attempts);
    }

    /// <summary>Duplicate processing does not send twice, proven through the real dispatcher rather than a fake.</summary>
    [Fact]
    public async Task RunningTheDispatcherTwice_SendsOnlyOnce()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client);

        await _factory.RunOutboxDispatchAsync();
        await _factory.RunOutboxDispatchAsync();
        await _factory.RunOutboxDispatchAsync();

        Assert.Single(_factory.EmailSender.Recorded, e => e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));
        Assert.Single(await _factory.GetNotificationsAsync(ticket.TicketId));
    }

    /// <summary>
    /// The reported CRITICAL gap, end to end. CRM-CONTACT-2002's contact
    /// channel is a phone number, which MVP-Data-Dictionary.md §2.8 stores in
    /// the same untyped field an email address would occupy. No recipient is
    /// guessed, nothing is sent, and the failure is retained and visible.
    /// </summary>
    [Fact]
    public async Task TicketWhoseVerifiedContactIsAPhoneNumber_SkipsWithoutSending()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client, PhoneContactIndex);

        var result = await _factory.RunOutboxDispatchAsync();

        // A phone-only customer is an expected, successful outcome — the
        // message is processed and the notification recorded as Skipped,
        // never dead-lettered (which would trip the operator alert).
        Assert.True(result.Processed >= 1);
        Assert.Equal(0, result.DeadLettered);
        Assert.DoesNotContain(_factory.EmailSender.Recorded, e => e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));

        var notification = Assert.Single(await _factory.GetNotificationsAsync(ticket.TicketId));
        Assert.Equal(NotificationDeliveryStatus.Skipped, notification.DeliveryStatus);
        Assert.Null(notification.RecipientAddress);

        var message = Assert.Single(
            await _factory.GetOutboxMessagesAsync(),
            m => m.Payload.Contains($"\"ticketId\":{ticket.TicketId}", StringComparison.Ordinal));
        Assert.Equal(OutboxMessageStatus.Processed, message.Status);
        Assert.Null(message.LastError);

        var stored = await _factory.GetTicketAsync(ticket.TicketId);
        Assert.Null(stored!.AcknowledgementSentAtUtc);
    }

    /// <summary>
    /// FR-SLA-05 / ISSUE-019 end to end: after a successful acknowledgement,
    /// the SLA endpoint — the contract surface First Response compliance is
    /// actually reported from — still shows no first response.
    /// </summary>
    [Fact]
    public async Task AcknowledgementDelivery_DoesNotSatisfyFirstResponseOnTheSlaEndpoint()
    {
        var (client, agentId) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client);
        await _factory.AssignOwnerAsync(ticket.TicketId, agentId); // a CS Agent reads the SLA of a ticket assigned to them

        var before = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla"))
            .Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();
        Assert.Null(before!.FirstHumanResponseAtUtc);

        await _factory.RunOutboxDispatchAsync();

        var stored = await _factory.GetTicketAsync(ticket.TicketId);
        Assert.NotNull(stored!.AcknowledgementSentAtUtc);

        var after = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla"))
            .Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();

        Assert.Null(after!.FirstHumanResponseAtUtc);
        Assert.Equal(nameof(SlaState.Running), after.SlaState);
        Assert.False(after.FirstResponseBreached);
    }

    /// <summary>
    /// And the acknowledged ticket still breaches its First Response deadline
    /// when nobody answers — the strongest form of the rule. If the
    /// acknowledgement had leaked into the first-response field, this deadline
    /// would report as met instead.
    /// </summary>
    [Fact]
    public async Task AcknowledgedButUnansweredTicket_StillBreachesFirstResponse()
    {
        var (client, agentId) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client);
        await _factory.AssignOwnerAsync(ticket.TicketId, agentId); // a CS Agent reads the SLA of a ticket assigned to them

        await _factory.RunOutboxDispatchAsync();
        Assert.NotNull((await _factory.GetTicketAsync(ticket.TicketId))!.AcknowledgementSentAtUtc);

        // Put the deadline in the past without waiting for it.
        await _factory.ForceSlaDueDatesAsync(
            ticket.TicketId, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddHours(8));

        var outcome = await _factory.RunDeadlineCheckAsync(ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, outcome);

        var sla = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/sla"))
            .Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();
        Assert.True(sla!.FirstResponseBreached);
    }

    /// <summary>The five approved audit events, correlation-tracked (FR-NOT-05 / ADR-0014).</summary>
    [Fact]
    public async Task Delivery_WritesTheQueuedAndSucceededAuditEventsUnderOneCorrelationId()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(client);

        await _factory.RunOutboxDispatchAsync();

        var message = Assert.Single(
            await _factory.GetOutboxMessagesAsync(),
            m => m.Payload.Contains($"\"ticketId\":{ticket.TicketId}", StringComparison.Ordinal));

        var audit = await _factory.GetNotificationAuditAsync();
        var forThisMessage = audit.Where(a => a.CorrelationId == message.CorrelationId).ToList();

        Assert.Contains(forThisMessage, a => a.Action == NotificationAuditActions.NotificationQueued);
        Assert.Contains(forThisMessage, a => a.Action == NotificationAuditActions.NotificationDeliverySucceeded);

        // No customer data in an audit row (Security-Architecture.md §11).
        Assert.DoesNotContain(forThisMessage, a =>
            (a.AfterValue ?? string.Empty).Contains("ahmed.alfarsi@example.com", StringComparison.OrdinalIgnoreCase)
            || (a.AfterValue ?? string.Empty).Contains("AC unit not cooling", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// No notification-management API surface was added — the merged
    /// MVP-API-Contracts.md contains none, and MVP-Traceability-Matrix.md §5
    /// item 1 records the gap as unresolved because closing it "changes an API
    /// surface". A regression here would mean an unapproved endpoint shipped.
    /// </summary>
    [Theory]
    [InlineData("/api/notifications")]
    [InlineData("/api/notifications/dead-letter")]
    [InlineData("/api/outbox")]
    [InlineData("/api/outbox/dead-letter")]
    public async Task NoNotificationManagementEndpointsExist(string path)
    {
        var (client, _) = await CreateClientAsync(Roles.SystemAdministrator);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
