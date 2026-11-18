using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Application.Modules.Reporting.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.ActivityTimeline;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;
using static TigerCS.Tests.QueueingIntegration.QueueingFixture;

namespace TigerCS.Tests.QueueingIntegration;

/// <summary>
/// Regression tests from the local UAT of TigerCS ticket 1686 (Created, Assigned, CommentAdded, Recalled, Returned, Reassigned, Closed): how a queue ticket is
/// SHOWN (source, interaction lifecycle, closer, comment author, owner), how its assignments are timed, who is credited for it, and what is (not) sent.
/// </summary>
public sealed class QueueingLifecycleAttributionTests(QueueingFixture f) : IClassFixture<QueueingFixture>
{
    private static readonly DateTime Base = DateTime.UtcNow.AddMinutes(-60);

    /// <summary>Created t0, assigned A t1, comment, recalled, returned t2, reassigned B t3, closed (by B) t4 - every event timestamped in the past, as the queue sends them.</summary>
    private async Task<(string ExternalId, long TicketId)> RunFullLifecycleAsync(string closer = "queue-user-b", string closeType = "TicketClosed")
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id, at: Base));
        await f.PostAsync(f.Assigned(id, 2, f.QueueUserA, Base.AddMinutes(10)));
        await f.PostAsync(new QueueingEventRequest(NewEventId(), "TicketCommentAdded", id, 3, Comment: "Customer asked for the lease copy", PerformedByUserId: f.QueueUserA, OccurredAtUtc: Base.AddMinutes(12)));
        await f.PostAsync(Simple("TicketRecalled", id, 4, Base.AddMinutes(14), reason: "No answer"));
        await f.PostAsync(Simple("TicketReturned", id, 5, Base.AddMinutes(20), reason: "Stepped away"));
        await f.PostAsync(f.Assigned(id, 6, f.QueueUserB, Base.AddMinutes(30), "TicketReassigned"));
        var (http, _) = await f.PostAsync(new QueueingEventRequest(NewEventId(), closeType, id, 7, PerformedByUserId: closer, CounterId: "counter-3", OccurredAtUtc: Base.AddMinutes(40)));
        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        return (id, (await f.FindTicketAsync(id))!.TicketId);
    }

    private async Task<TicketActivityTimelineDto> TimelineAsync(long ticketId)
    {
        var manager = (await f.Factory.SeedEmployeeAsync(Roles.CsManager)).EmployeeId;
        using var scope = f.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<TicketActivityTimelineAppService>().GetAsync(manager, [Roles.CsManager], ticketId)).Response!;
    }

    // ---- 1. ticket source

    [Fact]
    public async Task TheTimeline_NamesTheQueueingSystemAsTheSource_NeverAnEmployee()
    {
        var (_, ticketId) = await RunFullLifecycleAsync();
        var created = (await TimelineAsync(ticketId)).Events.Single(e => e.EventType == "TicketCreated");

        Assert.Contains("Source: Queueing System", created.Description);
        Assert.DoesNotContain("entered by an employee", created.Description);
        Assert.Equal("Queueing System", created.ActorName);
        Assert.Equal(TimelineActorTypes.System, created.ActorType);
    }

    // ---- 2. interaction lifecycle

    [Theory]
    [InlineData("TicketClosed", "QueueTicketClosed")]
    [InlineData("TicketCancelled", "QueueTicketCancelled")]
    public async Task TheWalkInInteraction_EndsAtTheOriginalOccurredAtUtc_OnCloseAndCancel(string closeType, string reason)
    {
        var (_, ticketId) = await RunFullLifecycleAsync(closeType: closeType);

        var interaction = await f.ReadAsync(db => db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticketId));
        Assert.True(interaction.IsEnded);
        Assert.Equal(Base.AddMinutes(40), interaction.EndedAtUtc!.Value, TimeSpan.FromMilliseconds(5));   // the queue's time, not the receipt time
        Assert.Equal(reason, interaction.EndReason);
        Assert.Equal(InteractionContextSource.Ticketing, interaction.Source);
    }

    [Fact]
    public async Task ANeverClosedQueueTicket_KeepsItsInteractionLive()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        var ticketId = (await f.FindTicketAsync(id))!.TicketId;

        Assert.False((await f.ReadAsync(db => db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticketId))).IsEnded);
    }

    [Fact]
    public async Task AQueueTimeline_HasNoGenesysWording_AndShowsTheInteractionEnded()
    {
        var (_, ticketId) = await RunFullLifecycleAsync();
        var timeline = await TimelineAsync(ticketId);

        Assert.DoesNotContain(timeline.Events, e => e.Description.Contains("Genesys", StringComparison.OrdinalIgnoreCase) || (e.TimeNote ?? "").Contains("Genesys", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(timeline.EvidenceGaps, g => g.Contains("Genesys", StringComparison.OrdinalIgnoreCase));
        var closure = timeline.Closures.Single();
        Assert.Equal("Yes", closure.CustomerInteractionEnded);
    }

    // ---- 3. closed by

    [Fact]
    public async Task TheClosure_IsAttributedToTheEmployeeTheQueueNamed_NeverTheIntegrationAccount()
    {
        var (_, ticketId) = await RunFullLifecycleAsync(closer: f.QueueUserB);

        var resolution = await f.ReadAsync(db => db.TicketResolutions.AsNoTracking().SingleAsync(r => r.TicketId == ticketId));
        Assert.Equal(f.EmployeeB, resolution.ResolvingEmployeeId);
        var closing = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking().Where(h => h.TicketId == ticketId && h.NewValue == (byte)TicketStatus.Closed).ToListAsync());
        Assert.All(closing, h => { Assert.Equal(f.EmployeeB, h.ActorEmployeeId); Assert.False(h.ActorIsSystem); });
        Assert.DoesNotContain(await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking().Where(h => h.TicketId == ticketId && h.ActorEmployeeId == f.ServiceAccountId).ToListAsync()), h => h.NewValue == (byte)TicketStatus.Closed);

        var closure = (await TimelineAsync(ticketId)).Closures.Single();
        Assert.Equal(ClosedByTypes.HumanAgent, closure.ClosedByType);
        Assert.NotEqual("Not recorded", closure.ClosedByName);
        Assert.DoesNotContain("integration", closure.ClosedByName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AClosureByAnUnmappedQueueUser_IsAQueueingSystemAction_NotTheIntegrationAccountAndNotAGuess()
    {
        var (_, ticketId) = await RunFullLifecycleAsync(closer: "queue-user-nobody-knows");

        var closing = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking().Where(h => h.TicketId == ticketId && h.NewValue == (byte)TicketStatus.Closed).ToListAsync());
        Assert.All(closing, h => { Assert.Null(h.ActorEmployeeId); Assert.True(h.ActorIsSystem); });
        var closure = (await TimelineAsync(ticketId)).Closures.Single();
        Assert.Equal(ClosedByTypes.SystemAutomation, closure.ClosedByType);
        Assert.Equal("Queueing System", closure.ClosedByName);
    }

    // ---- 5. comment author

    [Fact]
    public async Task ACommentIsAuthoredByTheEmployeeTheQueueNamed_AndTheNoteListCarriesTheirName()
    {
        var (_, ticketId) = await RunFullLifecycleAsync();
        var manager = (await f.Factory.SeedEmployeeAsync(Roles.CsManager)).EmployeeId;

        using var scope = f.Factory.Services.CreateScope();
        var notes = (await scope.ServiceProvider.GetRequiredService<TicketNoteAppService>().ListNotesAsync(manager, [Roles.CsManager], ticketId, 1, 50)).Response!.Items;

        var note = Assert.Single(notes);
        Assert.Equal(f.EmployeeA, note.AuthorEmployeeId);
        Assert.False(string.IsNullOrWhiteSpace(note.AuthorName));
        Assert.DoesNotContain("Queueing System", note.NoteText);   // a mapped author needs no "[Queueing System - ...]" prefix
    }

    // ---- 6. assignment history

    [Fact]
    public async Task AssignmentHistory_EndsEachHolderAtTheQueuesTime_AndLeavesTheReturnGapUnassigned()
    {
        var (_, ticketId) = await RunFullLifecycleAsync();

        var rows = (await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).ToListAsync())).OrderBy(a => a.AssignedAtUtc).ToList();
        Assert.Equal(2, rows.Count);
        var (first, second) = (rows[0], rows[1]);

        Assert.Equal(f.EmployeeA, first.AssignedEmployeeId);
        Assert.Equal(Base.AddMinutes(10), first.AssignedAtUtc, TimeSpan.FromMilliseconds(5));
        Assert.Equal(Base.AddMinutes(20), first.SupersededAtUtc!.Value, TimeSpan.FromMilliseconds(5));   // ended by the Return, at the Return's time

        Assert.Equal(f.EmployeeB, second.AssignedEmployeeId);
        Assert.Equal(Base.AddMinutes(30), second.AssignedAtUtc, TimeSpan.FromMilliseconds(5));
        Assert.Equal(Base.AddMinutes(40), second.SupersededAtUtc!.Value, TimeSpan.FromMilliseconds(5));  // ended by the closure, at the closure's time

        // The ten minutes between the Return (t+20) and the Reassignment (t+30) belong to nobody, and the durations add up exactly.
        Assert.True(second.AssignedAtUtc - first.SupersededAtUtc!.Value == TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), first.SupersededAtUtc.Value - first.AssignedAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(10), second.SupersededAtUtc.Value - second.AssignedAtUtc);
        Assert.DoesNotContain(rows, a => a.IsCurrent);
    }

    // ---- 7. closed ticket owner display

    [Fact]
    public async Task AClosedQueueTicket_NamesTheLastResponsibleEmployee_WithoutAnActiveAssignment()
    {
        var (_, ticketId) = await RunFullLifecycleAsync();
        var manager = (await f.Factory.SeedEmployeeAsync(Roles.CsManager)).EmployeeId;

        using var scope = f.Factory.Services.CreateScope();
        var detail = (await scope.ServiceProvider.GetRequiredService<TicketQueryAppService>().GetDetailAsync(manager, [Roles.CsManager], ticketId)).Response!;

        Assert.Null(detail.CurrentOwnerEmployeeId);
        Assert.Equal(f.EmployeeB, detail.LastResponsibleEmployeeId);
        Assert.False(string.IsNullOrWhiteSpace(detail.LastResponsibleName));
        Assert.Equal(0, await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().CountAsync(a => a.TicketId == ticketId && a.IsCurrent)));
    }

    [Fact]
    public async Task AnOpenQueueTicket_ShowsNoLastResponsibleEmployee()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        var ticketId = (await f.FindTicketAsync(id))!.TicketId;
        var manager = (await f.Factory.SeedEmployeeAsync(Roles.CsManager)).EmployeeId;

        using var scope = f.Factory.Services.CreateScope();
        var detail = (await scope.ServiceProvider.GetRequiredService<TicketQueryAppService>().GetDetailAsync(manager, [Roles.CsManager], ticketId)).Response!;

        Assert.Equal(f.EmployeeA, detail.CurrentOwnerEmployeeId);
        Assert.Null(detail.LastResponsibleEmployeeId);
    }

    // ---- 4. performance attribution

    [Fact]
    public async Task TheFinalEmployee_IsCreditedWithTheClosure_AndNobodyElseIs()
    {
        var (_, ticketId) = await RunFullLifecycleAsync();

        using var scope = f.Factory.Services.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
        async Task<bool> CountedAsync(Guid employee, TicketClosureCategory category) =>
            (await tickets.SearchAsync(new TicketQuery(
                null, null, null, null, null, null, null, null, TicketSortBy.CreatedAtUtc, true, 1, 50,
                ClosureCategory: category, ClosedAssigneeEmployeeId: employee))).Items.Any(t => t.TicketId == ticketId);

        // Employee closed-ticket counts and Human Agent closures: the last holder (B) - not the first (A), not the integration account.
        Assert.True(await CountedAsync(f.EmployeeB, TicketClosureCategory.ClosedByAgent));
        Assert.True(await CountedAsync(f.EmployeeB, TicketClosureCategory.NotReviewed));
        Assert.False(await CountedAsync(f.EmployeeA, TicketClosureCategory.ClosedByAgent));
        Assert.False(await CountedAsync(f.ServiceAccountId, TicketClosureCategory.ClosedByAgent));
        Assert.False(await CountedAsync(f.EmployeeB, TicketClosureCategory.ClosedByAi));
    }

    [Fact]
    public async Task AClosureTheQueueCouldNotAttribute_IsNotCountedAsAHumanAgentClosure()
    {
        var (_, ticketId) = await RunFullLifecycleAsync(closer: "queue-user-nobody-knows");

        using var scope = f.Factory.Services.CreateScope();
        var found = (await scope.ServiceProvider.GetRequiredService<ITicketRepository>().SearchAsync(new TicketQuery(
            null, null, null, null, null, null, null, null, TicketSortBy.CreatedAtUtc, true, 1, 50,
            ClosureCategory: TicketClosureCategory.ClosedByAgent, ClosedAssigneeEmployeeId: f.EmployeeB))).Items;
        Assert.DoesNotContain(found, t => t.TicketId == ticketId);
    }

    [Fact]
    public async Task TeamPerformance_CreditsTheResolutionToTheEmployeeWhoHeldTheTicketAtTheClosure()
    {
        var (_, ticketId) = await RunFullLifecycleAsync();

        using var scope = f.Factory.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<ITeamPerformanceQueryRepository>()
            .GetSlaEventsAsync([f.EmployeeA, f.EmployeeB, f.ServiceAccountId], Base.AddHours(-1), DateTime.UtcNow.AddHours(1));

        Assert.Contains(events.Met, e => e.TicketId == ticketId && e.EmployeeId == f.EmployeeB && e.SlaType == TigerCS.Domain.Modules.SlaAndEscalation.SlaDeadlineType.Resolution);
        Assert.DoesNotContain(events.Met, e => e.TicketId == ticketId && e.EmployeeId != f.EmployeeB);
        Assert.DoesNotContain(events.Breaches, e => e.TicketId == ticketId && e.EmployeeId == f.ServiceAccountId);
    }

    // ---- 8. first response SLA

    [Fact]
    public async Task NoQueueEvent_IsTreatedAsAFirstHumanResponse()
    {
        var (_, ticketId) = await RunFullLifecycleAsync();

        // Assignment, comment, recall, return, reassignment and closure are all service-state events, not proof that anybody replied to the customer.
        var ticket = await f.ReadAsync(db => db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId));
        Assert.Null(ticket.FirstHumanResponseAtUtc);

    }

    // ---- 9. notifications

    /// <summary>The dispatcher takes a batch at a time and the fixture is shared by every test of the class: drain it, so this test's own messages are reached.</summary>
    private async Task DispatchAllAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await f.Factory.RunOutboxDispatchAsync();
        }
    }

    [Fact]
    public async Task TheCustomerGetsNoEmailAndNoSms_WhileTheAssignedEmployeeStillGetsTheStaffEmail()
    {
        const string staffAddress = "agent.a@tiger.test";
        await f.ReadAsync(async db => { (await db.Users.SingleAsync(u => u.Id == f.EmployeeA)).Email = staffAddress; await db.SaveChangesAsync(); return 0; });
        var id = NewTicketId();
        var (_, created) = await f.PostAsync(Created(id));
        var ticketId = created!.TicketId!.Value;
        await f.PostAsync(f.Assigned(id, 2, f.QueueUserA));
        await DispatchAllAsync();   // the staff email goes out while the assignment is still current, as it does live

        Assert.Contains(f.Factory.EmailSender.Recorded, m => m.Audience == EmailAudience.Internal && m.ToAddress == staffAddress);   // the staff assignment email is preserved

        await f.PostAsync(f.Assigned(id, 3, f.QueueUserB, type: "TicketReassigned"));
        await f.PostAsync(new QueueingEventRequest(NewEventId(), "TicketClosed", id, 4, PerformedByUserId: f.QueueUserB, OccurredAtUtc: DateTime.UtcNow.AddMinutes(-1)));
        await DispatchAllAsync();

        var notifications = await f.Factory.GetNotificationsAsync(ticketId);
        Assert.All(notifications, n => Assert.Equal(NotificationChannel.Email, n.Channel));   // email is the only channel TigerCS has: there is no customer SMS path to take
        Assert.DoesNotContain(notifications, n => n.RecipientAddress == "aisha@example.test" && n.DeliveryStatus == NotificationDeliveryStatus.Sent);
        Assert.Contains(notifications, n => n.DeliveryStatus == NotificationDeliveryStatus.Skipped);   // the customer's emails are recorded as skipped (ManagedByExternalSystem), never dropped
        Assert.DoesNotContain(f.Factory.EmailSender.Recorded, m => m.Audience == EmailAudience.Customer && m.ToAddress == "aisha@example.test");

        var sms = f.Factory.Services.GetService<ISmsSender>();
        if (sms is TigerCS.Integrations.Modules.SmsIntegration.FakeSmsSender fake)
        {
            Assert.DoesNotContain(fake.Sent, m => m.Destination.Contains("500000123"));
        }
    }

    // ---- other tickets are untouched

    [Fact]
    public async Task AManuallyCreatedTicket_StillDisplaysTheTigerCsSourceAndItsOwnClosure()
    {
        var timelineSources = new TicketTimelineSources
        {
            TicketId = 1, TicketNumber = "T-1", CreatedAtUtc = DateTime.UtcNow, IntegrationSource = null,
            Interactions = [TicketInteraction.CreateLocal(1, WellKnownChannels.Phone, "0500000000", DateTime.UtcNow, isOriginatingInteraction: true)]
        };

        var created = TicketActivityTimelineBuilder.Build(timelineSources).Events.Single(e => e.EventType == "TicketCreated");
        Assert.Contains("TigerCS (entered by an employee)", created.Description);
    }
}
