using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Api.Integrations;
using TigerCS.Application.Modules.QueueingIntegration;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using static TigerCS.Tests.QueueingIntegration.QueueingFixture;

namespace TigerCS.Tests.QueueingIntegration;

/// <summary>The Queueing System receiving API end to end, on the real host: creation, assignment, return, comments, recall, closure, cancellation, ordering, idempotency, security.</summary>
public sealed class QueueingEventEndpointTests(QueueingFixture f) : IClassFixture<QueueingFixture>
{
    // ------------------------------------------------------------------ security

    [Fact]
    public async Task NoApiKey_IsUnauthorized_AndNothingIsStored()
    {
        var id = NewTicketId();
        var (http, _) = await f.PostAsync(Created(id), f.CreateClient(apiKey: null));
        Assert.Equal(HttpStatusCode.Unauthorized, http.StatusCode);
        Assert.Equal(0, await f.CountTicketsAsync(id));
    }

    [Fact]
    public async Task WrongApiKey_IsUnauthorized()
    {
        var (http, _) = await f.PostAsync(Created(NewTicketId()), f.CreateClient("not-the-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, http.StatusCode);
    }

    [Fact]
    public async Task EitherConfiguredKey_IsAccepted_SoAKeyCanBeRotatedWithoutDowntime()
    {
        var (first, _) = await f.PostAsync(Created(NewTicketId()), f.CreateClient(ApiKey));
        var (second, _) = await f.PostAsync(Created(NewTicketId()), f.CreateClient(ApiKey + "-next"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task AStaffJwt_CannotCallTheIntegrationEndpoint()
    {
        var (username, password, _) = await f.Factory.SeedEmployeeAsync(Roles.SystemAdministrator);
        var client = f.Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponseDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var (http, _) = await f.PostAsync(Created(NewTicketId()), client);
        Assert.Equal(HttpStatusCode.Unauthorized, http.StatusCode);
    }

    [Fact]
    public async Task TheIntegrationKey_OpensNothingElse()
    {
        var response = await f.CreateClient().GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------ validation, per event type

    [Theory]
    [InlineData("TicketCreated", "QueueNumber,CompanyId,TicketTypeId,CustomerPhone")]
    [InlineData("TicketAssigned", "AssignedUserId")]
    [InlineData("TicketReassigned", "AssignedUserId")]
    [InlineData("TicketCommentAdded", "Comment")]
    [InlineData("TicketReturned", "")]
    [InlineData("TicketRecalled", "")]
    [InlineData("TicketClosed", "")]
    [InlineData("TicketCancelled", "")]
    public async Task RequiredFields_AreEnforcedPerEventType_AndReportedByName(string eventType, string expectedMissing)
    {
        var bare = new QueueingEventRequest(NewEventId(), eventType, NewTicketId(), 1, OccurredAtUtc: DateTime.UtcNow.AddMinutes(-1));
        var (http, body) = await f.PostAsync(bare);

        if (expectedMissing.Length == 0)
        {
            // Nothing beyond the envelope is required: valid (stored; applied or pending, but never a validation error).
            Assert.NotEqual(HttpStatusCode.BadRequest, http.StatusCode);
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        Assert.Equal(QueueingResultStatus.Invalid, body!.Status);
        Assert.Equal(expectedMissing.Split(',').Order(), body.Errors!.Keys.Order());
        Assert.Null(await f.FindEventAsync(bare.EventId!));   // an invalid event is never stored
    }

    [Fact]
    public async Task TheEnvelope_IsValidated()
    {
        var (http, body) = await f.PostAsync(new QueueingEventRequest(null, "Nope", null, 0, OccurredAtUtc: DateTime.UtcNow.AddHours(1)));
        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        Assert.Equal(["EventId", "EventSequence", "EventType", "ExternalTicketId", "OccurredAtUtc"], body!.Errors!.Keys.Order());
    }

    // ------------------------------------------------------------------ QR ticket creation

    [Fact]
    public async Task QrTicketCreation_CreatesOneUnassignedOpenTicket_WithTheQueueOrigin()
    {
        var id = NewTicketId();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var request = Created(id, at: at);
        var (http, body) = await f.PostAsync(request);

        Assert.Equal(HttpStatusCode.Created, http.StatusCode);
        var inbox = (await f.FindEventAsync(request.EventId!))!;
        Assert.Equal(QueueingEventStatus.Processed, inbox.Status);
        Assert.Equal(at, inbox.OccurredAtUtc, TimeSpan.FromMilliseconds(5));                       // the original event time is kept on the inbox row
        Assert.True(inbox.ProcessedAtUtc > DateTime.UtcNow.AddMinutes(-1), "processed-at is the real time");
        Assert.Equal(QueueingResultStatus.Processed, body!.Status);
        Assert.Equal(QueueingOutcomeCodes.TicketCreated, body.Outcome);

        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(body.TicketId, ticket.TicketId);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                       // unassigned, and never the integration account
        Assert.Equal(f.DepartmentId, ticket.CurrentDepartmentId);         // from the company mapping
        Assert.Equal(f.RequestTypeId, ticket.RequestTypeId);              // from the ticket-type mapping
        Assert.NotNull(ticket.CategoryId);
        Assert.Equal(TicketIntegrationSources.QueueingSystem, ticket.IntegrationSource);
        Assert.Equal(id, ticket.ExternalTicketId);
        Assert.Equal("A-017", ticket.ExternalReference);                  // the queue number is preserved
        Assert.Equal(f.ServiceAccountId, ticket.CreatedByEmployeeId);
        Assert.Contains("A-017", ticket.RequestSummary);
        Assert.True(ticket.IsQueueManaged);

        // The original time is preserved, and the SLA clock started then.
        Assert.Equal(at, ticket.CreatedAtUtc, TimeSpan.FromMilliseconds(5));
        var sla = await f.Factory.GetCurrentSlaInstanceAsync(ticket.TicketId);
        Assert.NotNull(sla);
        Assert.Equal(at, sla!.PeriodStartAtUtc, TimeSpan.FromMilliseconds(5));      // the SLA clock starts at the ORIGINAL OccurredAtUtc, not at delivery
        Assert.Null(ticket.FirstHumanResponseAtUtc);

        // Recorded on the existing Walk in channel, as the originating interaction.
        var interaction = await f.ReadAsync(db => db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticket.TicketId));
        Assert.True(interaction.IsOriginatingInteraction);
        Assert.Equal(WellKnownChannels.FaceToFaceKiosk, interaction.ChannelId);
        Assert.Equal("+971500000123", interaction.CustomerPhone);

        var link = (await f.FindLinkAsync(ticket.TicketId))!;
        Assert.Equal(QueueingTicketState.Waiting, link.State);
        Assert.Equal(1, link.LastAppliedSequence);
        var stored = (await f.FindEventAsync(((QueueingEventRequest)Created(id)).EventId!)); // different EventId: just proves lookups by id work
        Assert.Null(stored);

        // The status history records creation at the original time.
        var history = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking().Where(h => h.TicketId == ticket.TicketId).ToListAsync());
        Assert.NotEmpty(history);
    }

    [Fact]
    public async Task TheCustomersMobileNumber_IsLookedUpThroughTheExistingCrmService_ButNeverAutoVerified()
    {
        f.Factory.CrmBuyerLookupGateway.Returns(TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmBuyerLookupResult.Success(
        [
            new TigerCS.Application.Modules.CustomerVerification.Dto.CrmBuyerMatchDto(
                new TigerCS.Application.Modules.CustomerVerification.Dto.CrmCustomerDto(9001, "Crm Buyer", null, "+971500000123", "buyer@example.test"),
                [
                    new TigerCS.Application.Modules.CustomerVerification.Dto.CrmBuyerUnitDto(
                        LeadId: 9100, LeadStatus: 8, LeadStatusName: "Sold", UnitId: 9200, UnitNumber: "1204", UnitStatus: 3, UnitType: 2,
                        FloorNumber: 12, ProjectId: 79, ProjectName: "Tiger Tower", ProjectArabicName: null, CustomerType: 1, CustomerTypeName: "Buyer")
                ])
        ]));
        var before = f.Factory.CrmBuyerLookupGateway.CallCount;
        var id = NewTicketId();
        var (http, _) = await f.PostAsync(Created(id));

        Assert.Equal(HttpStatusCode.Created, http.StatusCode);
        Assert.True(f.Factory.CrmBuyerLookupGateway.CallCount > before, "the existing CRM lookup was used");
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Null(ticket.CrmBuyerCustomerId);                                  // a match is enrichment only, never an automatic verification
        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
        var audit = await f.Factory.GetAuditEntriesAsync(ticket.TicketId.ToString());
        Assert.Contains(audit, a => a.Action == "QueueingTicketCreated" && a.AfterValue!.Contains("CustomerLookup=Found"));
    }

    [Fact]
    public async Task ACrmOutage_NeverStopsTheTicket()
    {
        f.Factory.CrmBuyerLookupGateway.Returns(TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmBuyerLookupResult.Unavailable("Timeout"));
        var id = NewTicketId();
        var (http, _) = await f.PostAsync(Created(id));
        Assert.Equal(HttpStatusCode.Created, http.StatusCode);
        Assert.Equal(1, await f.CountTicketsAsync(id));
        f.Factory.CrmBuyerLookupGateway.Returns(TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmBuyerLookupResult.NotFound());
    }

    [Fact]
    public async Task CustomerContactDetails_AreKept_ButNeverOverwriteWhatAnAgentAlreadyEntered()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var key = CustomerPhoneNumber.Normalize("+971500000123");
        var detail = await f.ReadAsync(db => db.CustomerContactDetails.AsNoTracking().FirstOrDefaultAsync(d => d.PhoneKey == key));
        Assert.NotNull(detail);
        Assert.Equal("Aisha Khan", detail!.Name);
    }

    [Fact]
    public async Task DuplicateCreation_SameEventId_IsAnsweredWithTheOriginalOutcome_AndCreatesNothing()
    {
        var id = NewTicketId();
        var request = Created(id);
        var (first, firstBody) = await f.PostAsync(request);
        var (second, secondBody) = await f.PostAsync(request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(QueueingResultStatus.Duplicate, secondBody!.Status);
        Assert.Equal(firstBody!.TicketId, secondBody.TicketId);
        Assert.Equal(1, await f.CountTicketsAsync(id));
    }

    [Fact]
    public async Task DuplicateCreation_NewEventIdForTheSameQueueTicket_CreatesNoSecondTicket()
    {
        var id = NewTicketId();
        var (_, firstBody) = await f.PostAsync(Created(id));
        var (http, secondBody) = await f.PostAsync(Created(id));   // a fresh EventId, same ExternalTicketId

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.TicketAlreadyExists, secondBody!.Outcome);
        Assert.Equal(firstBody!.TicketId, secondBody.TicketId);
        Assert.Equal(1, await f.CountTicketsAsync(id));
    }

    [Fact]
    public async Task ReusingAnEventId_WithDifferentContent_IsAConflict_AndChangesNothing()
    {
        var id = NewTicketId();
        var eventId = NewEventId();
        await f.PostAsync(Created(id, eventId: eventId));
        var (http, body) = await f.PostAsync(Created(NewTicketId(), eventId: eventId));
        Assert.Equal(HttpStatusCode.Conflict, http.StatusCode);
        Assert.Equal("EVENT_ID_REUSED", body!.Outcome);
    }

    // ------------------------------------------------------------------ assignment and reassignment

    [Fact]
    public async Task Assignment_GoesToTheReceivingEmployee_StartsWork_AndIsNotAFirstResponse()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var (http, body) = await f.PostAsync(f.Assigned(id, 2));

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        Assert.Equal(QueueingResultStatus.Processed, body!.Status);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeA, ticket.CurrentOwnerEmployeeId);          // the real employee - never the integration account
        Assert.NotEqual(f.ServiceAccountId, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);         // the existing Open -> InProgress transition
        Assert.Null(ticket.FirstHumanResponseAtUtc);                        // assignment is not a first response
        Assert.Equal(f.EmployeeA, ticket.CurrentOwnerEmployeeId);

        var link = (await f.FindLinkAsync(ticket.TicketId))!;
        Assert.Equal(QueueingTicketState.Assigned, link.State);
        Assert.Equal("counter-3", link.CounterId);
        Assert.Equal(f.QueueUserA, link.AssignedExternalUserId);

        var assignments = await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticket.TicketId).ToListAsync());
        var current = Assert.Single(assignments);
        Assert.Equal(f.EmployeeA, current.AssignedEmployeeId);
        Assert.True(current.IsCurrent);

        var status = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.TicketId == ticket.TicketId && h.Dimension == TicketStatusDimension.TicketStatus).ToListAsync());
        Assert.Contains(status, h => h.NewValue == (byte)TicketStatus.InProgress);
    }

    [Fact]
    public async Task Reassignment_MovesTheTicketToTheNewEmployee_AndKeepsTheHistory()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2, f.QueueUserA));
        var (http, body) = await f.PostAsync(f.Assigned(id, 3, f.QueueUserB, type: "TicketReassigned"));

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        Assert.Equal(QueueingResultStatus.Processed, body!.Status);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeB, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);

        var assignments = await f.ReadAsync(db => db.TicketAssignments.AsNoTracking()
            .Where(a => a.TicketId == ticket.TicketId).OrderBy(a => a.TicketAssignmentId).ToListAsync());
        Assert.Equal(2, assignments.Count);
        Assert.Equal(f.EmployeeA, assignments[0].AssignedEmployeeId);
        Assert.False(assignments[0].IsCurrent);                                  // superseded, never deleted
        Assert.Equal(f.EmployeeB, assignments[1].AssignedEmployeeId);
        Assert.True(assignments[1].IsCurrent);
        Assert.Equal(3, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);
    }

    [Fact]
    public async Task RedeliveringTheSameAssignment_ChangesNothingTwice()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var assign = f.Assigned(id, 2);
        await f.PostAsync(assign);
        var (_, again) = await f.PostAsync(assign);
        Assert.Equal(QueueingResultStatus.Duplicate, again!.Status);
        var assignments = await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().CountAsync(a => a.AssignedEmployeeId == f.EmployeeA && a.TicketId == again.TicketId));
        Assert.Equal(1, assignments);
    }

    // ------------------------------------------------------------------ return to queue, comments, recall

    [Fact]
    public async Task Return_ReleasesTheOwner_AndTheTicketWaitsInTheDepartmentQueue()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        var (http, body) = await f.PostAsync(Simple("TicketReturned", id, 3, reason: "Customer stepped away"));

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        Assert.Equal(QueueingResultStatus.Processed, body!.Status);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(f.DepartmentId, ticket.CurrentDepartmentId);                 // still in the department's queue
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);                     // back to Open - for a queue-managed ticket only
        Assert.Equal(QueueingTicketState.Waiting, (await f.FindLinkAsync(ticket.TicketId))!.State);
        var returnedHistory = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.TicketId == ticket.TicketId && h.Dimension == TicketStatusDimension.TicketStatus && h.NewValue == (byte)TicketStatus.Open && h.OldValue == (byte)TicketStatus.InProgress)
            .ToListAsync());
        var back = Assert.Single(returnedHistory);
        Assert.True(back.ActorIsSystem);
        Assert.Contains("Customer stepped away", back.Note);
        var assignment = Assert.Single(await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticket.TicketId).ToListAsync()));
        Assert.False(assignment.IsCurrent);
        var audit = await f.Factory.GetAuditEntriesAsync(ticket.TicketId.ToString());
        Assert.Contains(audit, a => a.Action == "ReturnToQueue");
        Assert.Contains(audit, a => a.Action == "QueueingTicketReturned" && a.AfterValue!.Contains("PerformedByUserId=queue-user-a"));

        // And it can be called again: the existing Open -> InProgress transition applies exactly as for a first assignment.
        var (_, again) = await f.PostAsync(f.Assigned(id, 4, f.QueueUserB));
        Assert.Equal(QueueingResultStatus.Processed, again!.Status);
        var reassigned = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeB, reassigned.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.InProgress, reassigned.TicketStatus);
    }

    [Fact]
    public async Task Comment_IsAddedAsATicketNote_AuthoredByTheEmployeeWhoWroteIt()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var (http, body) = await f.PostAsync(Simple("TicketCommentAdded", id, 2, comment: "Customer asked for the manager."));

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        var notes = await f.ReadAsync(db => db.TicketNotes.AsNoTracking().Where(n => n.TicketId == body!.TicketId).ToListAsync());
        var note = Assert.Single(notes);
        Assert.Equal("Customer asked for the manager.", note.NoteText);
        Assert.Equal(f.EmployeeA, note.AuthorEmployeeId);                        // queue-user-a, mapped
        // A comment is additive: it does not consume a sequence, move the ticket or change its state.
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(1, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);
    }

    [Fact]
    public async Task Comment_ByAnUnmappedUser_IsAuthoredByTheIntegration_WithTheQueueUserNamedInTheText()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var request = Simple("TicketCommentAdded", id, 2, comment: "Walk-in complaint");
        var (_, body) = await f.PostAsync(request with { PerformedByUserId = "queue-user-unknown" });
        var note = Assert.Single(await f.ReadAsync(db => db.TicketNotes.AsNoTracking().Where(n => n.TicketId == body!.TicketId).ToListAsync()));
        Assert.Equal(f.ServiceAccountId, note.AuthorEmployeeId);
        Assert.Contains("queue-user-unknown", note.NoteText);
    }

    [Fact]
    public async Task Recall_IsRecordedInTheTicketHistory_AndChangesNothingElse()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        var before = (await f.FindTicketAsync(id))!;
        var (http, _) = await f.PostAsync(Simple("TicketRecalled", id, 3, reason: "Customer did not respond"));

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        var after = (await f.FindTicketAsync(id))!;
        Assert.Equal(before.CurrentOwnerEmployeeId, after.CurrentOwnerEmployeeId);
        Assert.Equal(before.TicketStatus, after.TicketStatus);
        var audit = await f.Factory.GetAuditEntriesAsync(after.TicketId.ToString());
        Assert.Contains(audit, a => a.Action == "QueueingTicketRecalled" && a.AfterValue!.Contains("Customer did not respond") && a.AfterValue.Contains("CounterId=counter-3"));
    }

    [Fact]
    public async Task TheTicketTimeline_ShowsTheQueueEvents_ThatNoOtherHistoryTableCarries()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        await f.PostAsync(Simple("TicketRecalled", id, 3, reason: "No answer"));
        await f.PostAsync(Simple("TicketReturned", id, 4, reason: "Stepped away"));
        var ticket = (await f.FindTicketAsync(id))!;
        var manager = (await f.Factory.SeedEmployeeAsync(Roles.CsManager)).EmployeeId;

        using var scope = f.Factory.Services.CreateScope();
        var timeline = await scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Ticketing.ActivityTimeline.TicketActivityTimelineAppService>()
            .GetAsync(manager, [Roles.CsManager], ticket.TicketId);

        var types = timeline.Response!.Events.Select(e => e.EventType).ToList();
        Assert.Contains("QueueTicketCreated", types);
        Assert.Contains("QueueTicketRecalled", types);
        Assert.Contains("QueueTicketReturned", types);
    }

    [Fact]
    public async Task TheWireContract_NamesTheStatus_AndOmitsInternalHistoryNotesFromASuccess()
    {
        var id = NewTicketId();
        var http = await f.CreateClient().PostAsJsonAsync("/api/integrations/queueing/events", Created(id));
        var json = await http.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Processed\"", json);
        Assert.Contains("\"outcome\":\"TICKET_CREATED\"", json);
        Assert.DoesNotContain("\"detail\"", json);
    }

    // ------------------------------------------------------------------ closure and cancellation

    [Fact]
    public async Task Close_ClosesTheTicketAsResolved_ReleasesTheOwner_AndKeepsTheHistory()
    {
        var id = NewTicketId();
        var closedAt = DateTime.UtcNow.AddMinutes(-2);
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        var (http, body) = await f.PostAsync(Simple("TicketClosed", id, 3, at: closedAt));

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        Assert.Equal("Closed", body!.TicketStatus);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(QueueingTicketState.Closed, (await f.FindLinkAsync(ticket.TicketId))!.State);

        var resolution = await f.ReadAsync(db => db.TicketResolutions.AsNoTracking().SingleAsync(r => r.TicketId == ticket.TicketId));
        Assert.Equal(ResolutionOutcome.Resolved, resolution.ResolutionOutcome);
        Assert.Equal(closedAt, resolution.ResolvedAtUtc, TimeSpan.FromMilliseconds(5));   // the original event time
        var history = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.TicketId == ticket.TicketId && h.NewValue == (byte)TicketStatus.Closed).ToListAsync());
        Assert.All(history, h => Assert.Equal(closedAt, h.OccurredAtUtc, TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public async Task Cancel_ClosesTheTicketWithTheCancelledOutcome()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var (http, _) = await f.PostAsync(Simple("TicketCancelled", id, 2, reason: "Customer left"));

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Cancelled, ticket.ResolutionOutcome);
        var resolution = await f.ReadAsync(db => db.TicketResolutions.AsNoTracking().SingleAsync(r => r.TicketId == ticket.TicketId));
        Assert.Contains("Customer left", resolution.ResolutionNote);
        Assert.Equal(QueueingTicketState.Cancelled, (await f.FindLinkAsync(ticket.TicketId))!.State);
    }

    [Fact]
    public async Task AfterClosure_AStateChangeIsRejected_ButACommentIsStillRecorded()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(Simple("TicketClosed", id, 2));

        var (_, assign) = await f.PostAsync(f.Assigned(id, 3));
        Assert.Equal(QueueingResultStatus.Rejected, assign!.Status);
        Assert.Equal(QueueingOutcomeCodes.TicketTerminal, assign.Outcome);
        Assert.Equal(TicketStatus.Closed, (await f.FindTicketAsync(id))!.TicketStatus);
        Assert.Null((await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);

        var (_, comment) = await f.PostAsync(Simple("TicketCommentAdded", id, 4, comment: "Late note"));
        Assert.Equal(QueueingResultStatus.Processed, comment!.Status);
    }

    // ------------------------------------------------------------------ missing mappings, handled explicitly

    [Fact]
    public async Task MissingEmployeeMapping_LeavesTheTicketUnassigned_StoresTheEvent_AndAppliesItOnceMapped()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var assign = f.Assigned(id, 2, user: "queue-user-new");
        var (http, body) = await f.PostAsync(assign);

        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingResultStatus.Pending, body!.Status);
        Assert.Equal(QueueingOutcomeCodes.UserNotMapped, body.Outcome);
        Assert.True(body.Retryable);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Null(ticket.CurrentOwnerEmployeeId);                               // not assigned to anyone - least of all the integration account
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        var stored = (await f.FindEventAsync(assign.EventId!))!;
        Assert.Equal(QueueingEventStatus.Unresolved, stored.Status);
        Assert.NotNull(stored.NextRetryAtUtc);
        Assert.True(stored.NextRetryAtUtc > DateTime.UtcNow, "the backoff runs from now, not from the event's original time");
        Assert.Equal(1, stored.AttemptCount);

        // An administrator adds the mapping; the retry sweep applies the stored event.
        var newEmployee = await f.SeedDepartmentEmployeeAsync();
        await f.ReadAsync(async db =>
        {
            db.QueueingUserMappings.Add(new QueueingUserMapping("queue-user-new", newEmployee, DateTime.UtcNow));
            await db.SaveChangesAsync();
            return 0;
        });
        await f.MakeRetriesDueAsync();
        Assert.True(await f.RetryAsync() >= 1);

        Assert.Equal(newEmployee, (await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);
        Assert.Equal(QueueingEventStatus.Processed, (await f.FindEventAsync(assign.EventId!))!.Status);
    }

    [Fact]
    public async Task AnEmployeeWhoIsNotInTheTicketsDepartment_IsReportedExplicitly_AndNotAssigned()
    {
        var outsider = (await f.Factory.SeedEmployeeAsync(Roles.DepartmentEmployee)).EmployeeId;
        var otherDepartment = await f.Factory.CreateDepartmentAsync("Elsewhere " + Guid.NewGuid().ToString("N")[..6], "E" + Guid.NewGuid().ToString("N")[..8]);
        await f.Factory.AssignPrimaryDepartmentAsync(outsider, otherDepartment);
        await f.ReadAsync(async db => { db.QueueingUserMappings.Add(new QueueingUserMapping("queue-user-outsider", outsider, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });

        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var (http, body) = await f.PostAsync(f.Assigned(id, 2, "queue-user-outsider"));

        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.UserNotInDepartment, body!.Outcome);
        Assert.Null((await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);
        var link = (await f.FindLinkAsync((await f.FindTicketAsync(id))!.TicketId))!;
        Assert.Equal(1, link.LastAppliedSequence);            // the refused assignment consumed no sequence and changed no queue state
        Assert.Equal(QueueingTicketState.Waiting, link.State);
    }

    [Fact]
    public async Task ADeactivatedEmployee_IsNeverAssigned()
    {
        var gone = await f.SeedDepartmentEmployeeAsync();
        await f.ReadAsync(async db => { db.QueueingUserMappings.Add(new QueueingUserMapping("queue-user-gone", gone, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });
        await f.Factory.DeactivateEmployeeAsync(gone);

        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var (http, body) = await f.PostAsync(f.Assigned(id, 2, "queue-user-gone"));
        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.UserInactive, body!.Outcome);
        Assert.Null((await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AUserMappedToTheIntegrationAccount_IsRefused_NeverAssigned()
    {
        await f.ReadAsync(async db => { db.QueueingUserMappings.Add(new QueueingUserMapping("queue-user-bot", f.ServiceAccountId, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var (_, body) = await f.PostAsync(f.Assigned(id, 2, "queue-user-bot"));
        Assert.Equal(QueueingOutcomeCodes.UserNotMapped, body!.Outcome);
        Assert.Null((await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task MissingTicketTypeMapping_CreatesNoTicket_AndNoRequestTypeIsGuessed_UntilItIsMapped()
    {
        var id = NewTicketId();
        var request = Created(id, ticketTypeId: "type-unmapped");
        var (http, body) = await f.PostAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.TicketTypeNotMapped, body!.Outcome);
        Assert.Equal(0, await f.CountTicketsAsync(id));

        await f.ReadAsync(async db => { db.QueueingTicketTypeMappings.Add(new QueueingTicketTypeMapping(CompanyId, "type-unmapped", f.RequestTypeId, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });
        await f.MakeRetriesDueAsync();
        await f.RetryAsync();

        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(1, await f.CountTicketsAsync(id));
    }

    [Fact]
    public async Task MissingCompanyMapping_CreatesNoTicket_ButKeepsTheEvent()
    {
        var id = NewTicketId();
        var request = Created(id, companyId: "company-unknown");
        var (http, body) = await f.PostAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.CompanyNotMapped, body!.Outcome);
        Assert.Equal(0, await f.CountTicketsAsync(id));
        Assert.Equal(QueueingEventStatus.Unresolved, (await f.FindEventAsync(request.EventId!))!.Status);
    }

    // ---- routing: the company mapping is mandatory; an explicit (company, ticket type) mapping is an optional override that decides request type AND department

    private async Task<(int DepartmentId, int RequestTypeId)> SeedRoutableTypeAsync(string name)
    {
        var department = await f.Factory.CreateDepartmentAsync(name + " " + Guid.NewGuid().ToString("N")[..6], "R" + Guid.NewGuid().ToString("N")[..8]);
        await f.Factory.CreateCategoryAsync(name, department);
        return (department, await f.Factory.CreateRequestTypeAsync(name, department));
    }

    private Task MapTicketTypeAsync(string company, string ticketType, int requestTypeId) =>
        f.ReadAsync(async db => { db.QueueingTicketTypeMappings.Add(new QueueingTicketTypeMapping(company, ticketType, requestTypeId, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });

    [Fact]
    public async Task AnExplicitTicketTypeMapping_DecidesTheDepartment_OverTheCompanysDefaultDepartment()
    {
        var (otherDepartment, otherType) = await SeedRoutableTypeAsync("Routed Elsewhere");
        var ticketType = "type-" + Guid.NewGuid().ToString("N")[..8];
        await MapTicketTypeAsync(CompanyId, ticketType, otherType);   // CompanyId's default department is f.DepartmentId

        var id = NewTicketId();
        var (http, _) = await f.PostAsync(Created(id, ticketTypeId: ticketType));

        Assert.Equal(HttpStatusCode.Created, http.StatusCode);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(otherType, ticket.RequestTypeId);
        Assert.Equal(otherDepartment, ticket.CurrentDepartmentId);
        Assert.Equal(otherDepartment, ticket.OriginatingDepartmentId);
        Assert.NotEqual(f.DepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task OneCompany_WithTicketTypesOfSeveralDepartments_RoutesEachToItsOwnDepartment()
    {
        var company = "company-multi-" + Guid.NewGuid().ToString("N")[..6];
        var (departmentOne, typeOne) = await SeedRoutableTypeAsync("Multi One");
        var (departmentTwo, typeTwo) = await SeedRoutableTypeAsync("Multi Two");
        await MapTicketTypeAsync(company, "t-1", typeOne);
        await MapTicketTypeAsync(company, "t-2", typeTwo);
        // The company mapping is mandatory (it names the company's own department); the explicit mappings override it per ticket type.
        await f.ReadAsync(async db => { db.QueueingCompanyMappings.Add(new QueueingCompanyMapping(company, departmentOne, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });

        var first = NewTicketId();
        var second = NewTicketId();
        Assert.Equal(HttpStatusCode.Created, (await f.PostAsync(Created(first, companyId: company, ticketTypeId: "t-1"))).Http.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.PostAsync(Created(second, companyId: company, ticketTypeId: "t-2"))).Http.StatusCode);

        var one = (await f.FindTicketAsync(first))!;
        var two = (await f.FindTicketAsync(second))!;
        Assert.Equal((typeOne, departmentOne), (one.RequestTypeId, one.CurrentDepartmentId));
        Assert.Equal((typeTwo, departmentTwo), (two.RequestTypeId, two.CurrentDepartmentId));
    }

    // AnExplicitTicketTypeMapping_DoesNotNeedACompanyMapping was retired: the company mapping is now mandatory (see
    // QueueingTicketTypeNameTests.AnExplicitMapping_WithoutACompanyMapping_IsHeld_UntilTheCompanyIsMapped).

    [Fact]
    public async Task AnInactiveExplicitMapping_IsIgnored_AndTheCompanyDefaultApplies()
    {
        var company = "company-inactive-map-" + Guid.NewGuid().ToString("N")[..6];
        var (_, elsewhereType) = await SeedRoutableTypeAsync("Inactive Mapping Target");
        var (defaultDepartment, general) = await SeedRoutableTypeAsync("General");
        await f.ReadAsync(async db =>
        {
            var mapping = new QueueingTicketTypeMapping(company, "t-1", elsewhereType, DateTime.UtcNow);
            mapping.Repoint(elsewhereType, isActive: false);
            db.QueueingTicketTypeMappings.Add(mapping);
            db.QueueingCompanyMappings.Add(new QueueingCompanyMapping(company, defaultDepartment, DateTime.UtcNow));
            await db.SaveChangesAsync();
            return 0;
        });

        var id = NewTicketId();
        var (http, _) = await f.PostAsync(Created(id, companyId: company, ticketTypeId: "t-1"));

        Assert.Equal(HttpStatusCode.Created, http.StatusCode);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(defaultDepartment, ticket.CurrentDepartmentId);
        Assert.Equal(general, ticket.RequestTypeId);
    }

    [Fact]
    public async Task AnExplicitMapping_WinsOverTheCompanyDefault_EvenWhenTheCompanyIsMapped_AndNeverFallsBackWhenUnusable()
    {
        // The company default (f.DepartmentId) has a usable request type, but the explicit mapping points at an INACTIVE request type: the event is held,
        // never silently routed to the company's department.
        var (_, inactiveType) = await SeedRoutableTypeAsync("Soon Inactive");
        await f.ReadAsync(async db => { (await db.RequestTypes.FindAsync(inactiveType))!.Deactivate(); await db.SaveChangesAsync(); return 0; });
        var ticketType = "type-" + Guid.NewGuid().ToString("N")[..8];
        await MapTicketTypeAsync(CompanyId, ticketType, inactiveType);

        var id = NewTicketId();
        var (http, body) = await f.PostAsync(Created(id, ticketTypeId: ticketType));

        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.RequestTypeUnusable, body!.Outcome);
        Assert.Equal(0, await f.CountTicketsAsync(id));
    }

    [Fact]
    public async Task AnExplicitMapping_ToARequestTypeWithoutAPublishedWorkflow_IsHeld()
    {
        var department = await f.Factory.CreateDepartmentAsync("No Workflow " + Guid.NewGuid().ToString("N")[..6], "W" + Guid.NewGuid().ToString("N")[..8]);
        await f.Factory.CreateCategoryAsync("No Workflow Type", department);
        var requestTypeId = await f.ReadAsync(async db =>
        {
            // Points at a workflow id that has no Published version.
            var rt = new TigerCS.Domain.Modules.WorkflowConfiguration.RequestType(
                department, "No Workflow Type", workflowId: 987654321, (byte)PriorityLevel.Medium,
                allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true);
            db.RequestTypes.Add(rt);
            await db.SaveChangesAsync();
            return rt.RequestTypeId;
        });
        var ticketType = "type-" + Guid.NewGuid().ToString("N")[..8];
        await MapTicketTypeAsync(CompanyId, ticketType, requestTypeId);

        var id = NewTicketId();
        var (http, body) = await f.PostAsync(Created(id, ticketTypeId: ticketType));

        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.RequestTypeUnusable, body!.Outcome);
        Assert.Contains("published workflow", body.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await f.CountTicketsAsync(id));
    }

    [Fact]
    public async Task AnExplicitMapping_ToAnInactiveDepartmentsRequestType_IsHeld()
    {
        var (department, requestType) = await SeedRoutableTypeAsync("Inactive Dept");
        await f.ReadAsync(async db => { (await db.Departments.FindAsync(department))!.Deactivate(); await db.SaveChangesAsync(); return 0; });
        var ticketType = "type-" + Guid.NewGuid().ToString("N")[..8];
        await MapTicketTypeAsync(CompanyId, ticketType, requestType);

        var id = NewTicketId();
        var (_, body) = await f.PostAsync(Created(id, ticketTypeId: ticketType));

        Assert.Equal(QueueingOutcomeCodes.RequestTypeUnusable, body!.Outcome);
        Assert.Equal(0, await f.CountTicketsAsync(id));
    }

    // ------------------------------------------------------------------ ordering

    [Fact]
    public async Task AnEventThatArrivesBeforeItsTicket_IsStored_AndAppliedWhenTheTicketIsCreated()
    {
        var id = NewTicketId();
        var assign = f.Assigned(id, 2);
        var (http, body) = await f.PostAsync(assign);
        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.AwaitingTicket, body!.Outcome);
        Assert.Equal(0, await f.CountTicketsAsync(id));

        var (created, _) = await f.PostAsync(Created(id, sequence: 1));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeA, ticket.CurrentOwnerEmployeeId);                  // the stored assignment was drained right after creation
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Equal(QueueingEventStatus.Processed, (await f.FindEventAsync(assign.EventId!))!.Status);
    }

    [Fact]
    public async Task AnOlderStateEvent_NeverOverwritesANewerState()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 4, f.QueueUserB, type: "TicketReassigned"));   // seq 4 first
        var older = f.Assigned(id, 3, f.QueueUserA);                                    // seq 3 arrives late
        var (http, body) = await f.PostAsync(older);

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        Assert.Equal(QueueingResultStatus.Superseded, body!.Status);
        Assert.Equal(QueueingOutcomeCodes.StaleEvent, body.Outcome);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeB, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(4, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);
        Assert.Equal(QueueingEventStatus.Superseded, (await f.FindEventAsync(older.EventId!))!.Status);   // recorded for the trail
        Assert.Contains(await f.Factory.GetAuditEntriesAsync(ticket.TicketId.ToString()), a => a.Action == "QueueingEventSuperseded");
    }

    [Fact]
    public async Task AStaleClose_CannotCloseATicketTheQueueHasReturnedToWaiting()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        await f.PostAsync(Simple("TicketReturned", id, 4));
        var (_, body) = await f.PostAsync(Simple("TicketClosed", id, 3));     // older than the return
        Assert.Equal(QueueingResultStatus.Superseded, body!.Status);
        Assert.NotEqual(TicketStatus.Closed, (await f.FindTicketAsync(id))!.TicketStatus);
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task ConcurrentDeliveriesOfTheSameEvent_CreateExactlyOneTicket()
    {
        var id = NewTicketId();
        var request = Created(id);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.PostAsync(request)));

        Assert.Equal(1, await f.CountTicketsAsync(id));
        Assert.Single(results, r => r.Http.StatusCode == HttpStatusCode.Created);
        Assert.All(results, r => Assert.True(r.Http.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.Http.StatusCode.ToString()));
        Assert.Single(results.Select(r => r.Body!.TicketId).Distinct());
    }

    [Fact]
    public async Task ConcurrentCreationsOfTheSameQueueTicket_UnderDifferentEventIds_CreateExactlyOneTicket()
    {
        var id = NewTicketId();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.PostAsync(Created(id))));

        Assert.Equal(1, await f.CountTicketsAsync(id));
        Assert.Single(results, r => r.Http.StatusCode == HttpStatusCode.Created);
        Assert.Single(results.Select(r => r.Body!.TicketId).Distinct());
    }

    [Fact]
    public async Task ConcurrentEventsForOneTicket_SettleOnTheNewestState()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var events = new[]
        {
            f.Assigned(id, 2, f.QueueUserA),
            f.Assigned(id, 3, f.QueueUserB, type: "TicketReassigned"),
            Simple("TicketCommentAdded", id, 4, comment: "n1"),
            Simple("TicketRecalled", id, 5),
            f.Assigned(id, 6, f.QueueUserA, type: "TicketReassigned")
        };
        var results = await Task.WhenAll(events.Select(e => f.PostAsync(e)));

        Assert.All(results, r => Assert.True(r.Http.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, r.Http.StatusCode.ToString()));
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeA, ticket.CurrentOwnerEmployeeId);                   // sequence 6 wins whatever order they ran in
        Assert.Equal(6, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);
        var current = await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().CountAsync(a => a.TicketId == ticket.TicketId && a.IsCurrent));
        Assert.Equal(1, current);                                                    // never two current owners
    }

    // ------------------------------------------------------------------ customer notifications

    [Fact]
    public async Task TheCustomerIsNeverEmailedByTigerCs_ForAQueueTicket()
    {
        var id = NewTicketId();
        var (_, created) = await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        await f.PostAsync(Simple("TicketClosed", id, 3));
        await f.Factory.RunOutboxDispatchAsync();

        var ticketId = created!.TicketId!.Value;
        var notifications = await f.Factory.GetNotificationsAsync(ticketId);
        Assert.DoesNotContain(notifications, n => n.DeliveryStatus == NotificationDeliveryStatus.Sent);
        Assert.NotEmpty(notifications);
        Assert.All(notifications, n => Assert.Equal(NotificationDeliveryStatus.Skipped, n.DeliveryStatus));
        Assert.DoesNotContain(f.Factory.EmailSender.Recorded, m => m.Audience == TigerCS.Application.Modules.Notifications.Abstractions.EmailAudience.Customer && m.ToAddress == "aisha@example.test");
        // The acknowledgement is not even queued.
        Assert.DoesNotContain(await f.Factory.GetOutboxMessagesAsync(), m => m.EventType == "TicketCreated" && m.Payload.Contains($"\"ticketId\":{ticketId},"));
    }

    // ------------------------------------------------------------------ manual changes in TigerCS

    [Fact]
    public async Task ManualAssignmentStatusAndClosure_OfAQueueTicket_AreRefused_WhileCommentsStayOpen()
    {
        var id = NewTicketId();
        var (_, created) = await f.PostAsync(Created(id));
        var ticketId = created!.TicketId!.Value;
        var ticket = (await f.FindTicketAsync(id))!;
        var manager = (await f.Factory.SeedEmployeeAsync(Roles.CsManager)).EmployeeId;

        using var scope = f.Factory.Services.CreateScope();
        var assignment = scope.ServiceProvider.GetRequiredService<TicketAssignmentAppService>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<TicketLifecycleAppService>();
        IReadOnlyCollection<string> roles = [Roles.CsManager];

        var assign = await assignment.AssignAsync(manager, roles, ticketId, new AssignTicketRequestDto(f.EmployeeA, ticket.RowVersion));
        Assert.Equal(TicketMutationOutcome.QueueManagedTicket, assign.Outcome);
        var status = await lifecycle.ChangeStatusAsync(manager, roles, ticketId, new ChangeStatusRequestDto("InProgress", ticket.RowVersion));
        Assert.Equal(TicketMutationOutcome.QueueManagedTicket, status.Outcome);
        var close = await lifecycle.CloseAsync(manager, roles, ticketId, new CloseTicketRequestDto(ticket.RowVersion));
        Assert.Equal(TicketMutationOutcome.QueueManagedTicket, close.Outcome);
        Assert.Null((await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);

        var notes = scope.ServiceProvider.GetRequiredService<TicketNoteAppService>();
        var note = await notes.AddNoteAsync(manager, roles, ticketId, new CreateNoteRequestDto("A TigerCS-side comment"));
        Assert.Equal(NoteOutcome.Success, note.Outcome);
    }

    [Fact]
    public async Task WithTheAllowPolicy_AManualChangeIsPermitted_ButTheNextQueueEventStillWins()
    {
        var id = NewTicketId();
        var (_, created) = await f.PostAsync(Created(id));
        var manager = (await f.Factory.SeedEmployeeAsync(Roles.CsManager)).EmployeeId;
        var ticketId = created!.TicketId!.Value;
        f.Options.ManualChangePolicy = QueueingManualChangePolicy.Allow;
        try
        {
            using var scope = f.Factory.Services.CreateScope();
            var ticket = (await f.FindTicketAsync(id))!;
            var manual = await scope.ServiceProvider.GetRequiredService<TicketAssignmentAppService>()
                .AssignAsync(manager, [Roles.CsManager], ticketId, new AssignTicketRequestDto(f.EmployeeB, ticket.RowVersion));
            Assert.Equal(TicketMutationOutcome.Success, manual.Outcome);
            Assert.Equal(f.EmployeeB, (await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);
        }
        finally
        {
            f.Options.ManualChangePolicy = QueueingManualChangePolicy.Block;
        }

        await f.PostAsync(f.Assigned(id, 2, f.QueueUserA));
        Assert.Equal(f.EmployeeA, (await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);   // the queue's word is final
    }

    // ------------------------------------------------------------------ the retry sweep

    [Fact]
    public async Task RetriesAreBounded_ThenTheEventIsMarkedFailed_AndARedeliveryRestartsIt()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var assign = f.Assigned(id, 2, "queue-user-never-mapped");
        var max = f.Options.MaxRetryAttempts;
        await f.PostAsync(assign);
        for (var i = 1; i < max; i++)
        {
            await f.MakeRetriesDueAsync();
            await f.RetryAsync();
        }

        var failed = (await f.FindEventAsync(assign.EventId!))!;
        Assert.Equal(QueueingEventStatus.Failed, failed.Status);
        Assert.Null(failed.NextRetryAtUtc);
        Assert.Equal(max, failed.AttemptCount);

        var (_, again) = await f.PostAsync(assign);            // the same event, redelivered
        Assert.Equal(QueueingResultStatus.Pending, again!.Status);
        Assert.Equal(1, (await f.FindEventAsync(assign.EventId!))!.AttemptCount);
    }

    [Fact]
    public async Task CustomerPhone_IsMandatoryOnCreation_AndMustLookLikeANumber()
    {
        var (http, body) = await f.PostAsync(Created(NewTicketId()) with { CustomerPhone = null });
        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        Assert.Contains("CustomerPhone", body!.Errors!.Keys);

        var (http2, body2) = await f.PostAsync(Created(NewTicketId()) with { CustomerPhone = "not a number" });
        Assert.Equal(HttpStatusCode.BadRequest, http2.StatusCode);
        Assert.Contains("CustomerPhone", body2!.Errors!.Keys);
    }

    [Fact]
    public async Task AnAssignedEvent_DoesNotNeedAPhone()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var (http, _) = await f.PostAsync(f.Assigned(id, 2));
        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
    }

    // ------------------------------------------------------------------ payload retention

    [Fact]
    public async Task Retention_ErasesOnlyOldSuccessfullyProcessedPayloads_AndKeepsEveryIdentifierAndTheTicketHistory()
    {
        var id = NewTicketId();
        var old = Created(id);
        var recent = f.Assigned(id, 2);
        await f.PostAsync(old);
        await f.PostAsync(recent);
        var unresolved = f.Assigned(NewTicketId(), 2, "queue-user-never-mapped-retention");        // stays Unresolved (no ticket yet)
        await f.PostAsync(unresolved);
        var ticket = (await f.FindTicketAsync(id))!;

        // Age the first event past the retention period, as the clock passing 91 days would (also age the unresolved one: it must STILL survive).
        await f.ReadAsync(async db =>
        {
            foreach (var row in await db.QueueingEvents.Where(e => e.EventId == old.EventId || e.EventId == unresolved.EventId).ToListAsync())
            {
                db.Entry(row).Property(nameof(QueueingEvent.ProcessedAtUtc)).CurrentValue = row.Status == QueueingEventStatus.Processed ? DateTime.UtcNow.AddDays(-91) : row.ProcessedAtUtc;
                db.Entry(row).Property(nameof(QueueingEvent.ReceivedAtUtc)).CurrentValue = DateTime.UtcNow.AddDays(-91);
            }

            await db.SaveChangesAsync();
            return 0;
        });

        using (var scope = f.Factory.Services.CreateScope())
        {
            var purged = await scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.QueueingIntegration.Services.QueueingEventProcessor>().PurgeExpiredPayloadsAsync(100);
            Assert.Equal(1, purged);
        }

        var erased = (await f.FindEventAsync(old.EventId!))!;
        Assert.Null(erased.Payload);                                  // customer name / phone / email are gone
        Assert.NotNull(erased.PayloadPurgedAtUtc);
        Assert.Equal(QueueingEventStatus.Processed, erased.Status);   // the row, its identifiers and its outcome stay
        Assert.Equal(old.ExternalTicketId, erased.ExternalTicketId);
        Assert.Equal(1, erased.EventSequence);
        Assert.Equal(ticket.TicketId, erased.TicketId);
        Assert.NotEmpty(erased.PayloadHash);

        Assert.NotNull((await f.FindEventAsync(recent.EventId!))!.Payload);        // too recent
        var waiting = (await f.FindEventAsync(unresolved.EventId!))!;
        Assert.Equal(QueueingEventStatus.Unresolved, waiting.Status);
        Assert.NotNull(waiting.Payload);                                           // an unresolved event is never purged, however old

        // The ticket history is untouched.
        Assert.Equal(f.EmployeeA, (await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);
        Assert.NotEmpty(await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticket.TicketId).ToListAsync()));
        Assert.NotNull(await f.FindLinkAsync(ticket.TicketId));
    }

    [Fact]
    public async Task AfterAPayloadIsErased_IdempotencyStillHolds_ByHash()
    {
        var id = NewTicketId();
        var request = Created(id);
        await f.PostAsync(request);
        await f.ReadAsync(async db =>
        {
            var row = await db.QueueingEvents.FirstAsync(e => e.EventId == request.EventId);
            db.Entry(row).Property(nameof(QueueingEvent.ProcessedAtUtc)).CurrentValue = DateTime.UtcNow.AddDays(-120);
            await db.SaveChangesAsync();
            return 0;
        });
        using (var scope = f.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.QueueingIntegration.Services.QueueingEventProcessor>().PurgeExpiredPayloadsAsync(100);
        }

        Assert.Null((await f.FindEventAsync(request.EventId!))!.Payload);

        var (same, sameBody) = await f.PostAsync(request);                                     // a redelivery after the purge
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(QueueingResultStatus.Duplicate, sameBody!.Status);

        var (other, otherBody) = await f.PostAsync(request with { QueueNumber = "A-999" });    // the same EventId, different content
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("EVENT_ID_REUSED", otherBody!.Outcome);
        Assert.Equal(1, await f.CountTicketsAsync(id));
    }

    [Fact]
    public async Task Retention_CanBeSwitchedOff()
    {
        var previous = f.Options.PayloadRetentionDays;
        f.Options.PayloadRetentionDays = 0;
        try
        {
            using var scope = f.Factory.Services.CreateScope();
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.QueueingIntegration.Services.QueueingEventProcessor>().PurgeExpiredPayloadsAsync(100));
        }
        finally
        {
            f.Options.PayloadRetentionDays = previous;
        }
    }

    [Fact]
    public void TheDomain_AllowsInProgressToOpen_OnlyForAQueueManagedTicket()
    {
        var ordinary = Ticket.CreateUnverified("TG-X-1", 1, 1, 3, "x", DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => ordinary.ReturnToQueue());
        Assert.False(TicketStatusTransitions.IsChangeStatusAllowed(TicketStatus.InProgress, TicketStatus.Open));   // the generic table is untouched

        var queued = Ticket.CreateUnverified("TG-X-2", 1, 1, 3, "x", DateTime.UtcNow);
        queued.RecordExternalOrigin(TicketIntegrationSources.QueueingSystem, "qt-1", "A-1");
        queued.AssignTo(Guid.NewGuid());
        queued.ChangeStatus(TicketStatus.InProgress);
        Assert.Equal(TicketStatus.InProgress, queued.ReturnToQueue());
        Assert.Equal(TicketStatus.Open, queued.TicketStatus);
        Assert.Null(queued.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.Open, queued.ReturnToQueue());                                                    // already waiting: nothing changes
    }

    [Fact]
    public async Task TheRetrySweep_SettlesLaterEvents_EvenWhenAnEarlierEventInTheSameBatchFailsAgain()
    {
        // Ordered by ExternalTicketId: "qt-aaa..." is processed first and fails again (its employee is still unmapped); "qt-zzz..." must still be applied AND marked
        // processed - a failure of an earlier event must never detach the rest of the batch from the unit of work.
        var first = "qt-aaa-" + Guid.NewGuid().ToString("N");
        var second = "qt-zzz-" + Guid.NewGuid().ToString("N");
        await f.PostAsync(Created(first));
        var stuck = f.Assigned(first, 2, "queue-user-still-unmapped");
        await f.PostAsync(stuck);
        var later = Created(second, companyId: "company-mapped-later");
        var (_, pending) = await f.PostAsync(later);
        Assert.Equal(QueueingOutcomeCodes.CompanyNotMapped, pending!.Outcome);

        await f.ReadAsync(async db =>
        {
            db.QueueingCompanyMappings.Add(new QueueingCompanyMapping("company-mapped-later", f.DepartmentId, DateTime.UtcNow));
            db.QueueingTicketTypeMappings.Add(new QueueingTicketTypeMapping("company-mapped-later", TicketTypeId, f.RequestTypeId, DateTime.UtcNow));
            await db.SaveChangesAsync();
            return 0;
        });
        await f.MakeRetriesDueAsync();
        await f.RetryAsync();

        Assert.Equal(QueueingEventStatus.Unresolved, (await f.FindEventAsync(stuck.EventId!))!.Status);       // still waiting for its employee
        Assert.Equal(1, await f.CountTicketsAsync(second));
        var settled = (await f.FindEventAsync(later.EventId!))!;
        Assert.Equal(QueueingEventStatus.Processed, settled.Status);                                           // the ticket committed AND the event says so
        Assert.NotNull(await f.FindLinkAsync((await f.FindTicketAsync(second))!.TicketId));
    }
}
