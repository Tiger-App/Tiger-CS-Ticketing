using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.QueueingIntegration;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Application.Modules.QueueingIntegration.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;
using static TigerCS.Tests.QueueingIntegration.QueueingFixture;

namespace TigerCS.Tests.QueueingIntegration;

/// <summary>
/// The strict one-way mirror: events of one queue ticket are applied strictly in sequence order (an unresolved earlier event holds every later one, other tickets are
/// unaffected), configuration holds retry without a fixed cutoff while unexplained failures are bounded, nothing is skipped, only an audited administrator action restarts a Failed
/// event, and Queueing's terminal-to-active and Cancelled-to-Closed transitions are mirrored without double counting.
/// </summary>
public sealed class QueueingStrictMirrorTests(QueueingFixture f) : IClassFixture<QueueingFixture>
{
    // ------------------------------------------------------------------ strict in-order processing

    [Fact]
    public async Task AnUnresolvedEvent_HoldsEveryLaterEventOfTheSameTicket_AndTheyAreAppliedInOrderOnceItSettles()
    {
        var id = NewTicketId();
        var other = NewTicketId();
        await f.PostAsync(Created(id));
        const string lateUser = "queue-user-mapped-late";

        var assign = f.Assigned(id, 2, lateUser);
        var (_, held) = await f.PostAsync(assign);
        Assert.Equal(QueueingOutcomeCodes.UserNotMapped, held!.Outcome);

        var close = Simple("TicketClosed", id, 3);
        var (http, behind) = await f.PostAsync(close);
        Assert.Equal(QueueingResultStatus.Pending, behind!.Status);
        Assert.Equal(QueueingOutcomeCodes.AwaitingPreviousEvent, behind.Outcome);
        Assert.True(behind.Retryable);

        // Nothing was applied around the held event: the ticket is still open and unowned, and the later event consumed no attempt and has no retry of its own.
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        var heldBehind = (await f.FindEventAsync(close.EventId!))!;
        Assert.Equal(QueueingEventStatus.Unresolved, heldBehind.Status);
        Assert.Equal(0, heldBehind.AttemptCount);
        Assert.Null(heldBehind.NextRetryAtUtc);

        // Another queue ticket keeps flowing normally.
        await f.PostAsync(Created(other));
        await f.PostAsync(f.Assigned(other, 2));
        await f.PostAsync(Simple("TicketClosed", other, 3));
        Assert.Equal(TicketStatus.Closed, (await f.FindTicketAsync(other))!.TicketStatus);

        // The sweep retries only the head. Configure the missing mapping; the head applies and the held event is released right after it, in order.
        await f.MakeRetriesDueAsync();
        await f.ReadAsync(async db =>
        {
            db.QueueingUserMappings.Add(new QueueingUserMapping(lateUser, f.EmployeeA, DateTime.UtcNow));
            await db.SaveChangesAsync();
            return 0;
        });
        await f.RetryAsync();

        Assert.Equal(TicketStatus.Closed, (await f.FindTicketAsync(id))!.TicketStatus);
        Assert.Equal(QueueingEventStatus.Processed, (await f.FindEventAsync(assign.EventId!))!.Status);
        Assert.Equal(QueueingEventStatus.Processed, (await f.FindEventAsync(close.EventId!))!.Status);
        // The assignment really happened before the closure (history intact): the held assignment was applied, not superseded.
        Assert.Equal(QueueingTicketState.Closed, (await f.FindLinkAsync((await f.FindTicketAsync(id))!.TicketId))!.State);
        var assignmentRows = await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticket.TicketId).CountAsync());
        Assert.Equal(1, assignmentRows);
    }

    [Fact]
    public async Task ConfigurationHolds_AreRetriedWithoutAFixedCutoff_AtACappedBackoff()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var assign = f.Assigned(id, 2, "queue-user-never-mapped-forever");
        await f.PostAsync(assign);

        for (var i = 0; i < f.Options.MaxRetryAttempts + 5; i++)
        {
            await f.MakeRetriesDueAsync();
            await f.RetryAsync();
        }

        var stored = (await f.FindEventAsync(assign.EventId!))!;
        Assert.Equal(QueueingEventStatus.Unresolved, stored.Status);               // never "gives up": a missing mapping is fixed by configuring it
        Assert.True(stored.AttemptCount > f.Options.MaxRetryAttempts);
        Assert.NotNull(stored.NextRetryAtUtc);
        Assert.True(stored.NextRetryAtUtc!.Value <= DateTime.UtcNow.AddSeconds(f.Options.MaxRetryDelaySeconds + 5));   // the backoff is capped
        Assert.Equal(QueueingOutcomeCodes.UserNotMapped, stored.OutcomeCode);
    }

    [Fact]
    public async Task AFailedEvent_BlocksLaterEvents_IsNotRestartedByARedelivery_AndOnlyAnAuditedReprocessRestartsIt()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));

        // An event that exhausted its bounded attempts (as an unexplained failure would): stored Failed, directly.
        var assign = f.Assigned(id, 2);
        await f.ReadAsync(async db =>
        {
            var row = new QueueingEvent(assign.EventId!, QueueingEventType.TicketAssigned, id, 2, assign.OccurredAtUtc!.Value, assign.ToCanonicalJson(), DateTime.UtcNow);
            row.MarkUnresolved(QueueingOutcomeCodes.ProcessingFailed, "Unexpected failure (test).", null, DateTime.UtcNow, DateTime.UtcNow, maxAttempts: 1);
            db.QueueingEvents.Add(row);
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(QueueingEventStatus.Failed, (await f.FindEventAsync(assign.EventId!))!.Status);

        // A later event is held behind it - never applied around it, never skipped.
        var close = Simple("TicketClosed", id, 3);
        var (_, behind) = await f.PostAsync(close);
        Assert.Equal(QueueingOutcomeCodes.AwaitingPreviousEvent, behind!.Outcome);
        Assert.Equal(TicketStatus.Open, (await f.FindTicketAsync(id))!.TicketStatus);

        // The sweep never retries a Failed head, and a redelivery of it does not restart it (no uncontrolled loop).
        await f.MakeRetriesDueAsync();
        Assert.Equal(0, await f.RetryAsync());
        var (_, redelivered) = await f.PostAsync(assign);
        Assert.Equal(QueueingResultStatus.Pending, redelivered!.Status);
        Assert.False(redelivered.Retryable);
        Assert.Equal(QueueingEventStatus.Failed, (await f.FindEventAsync(assign.EventId!))!.Status);
        Assert.Equal(1, (await f.FindEventAsync(assign.EventId!))!.AttemptCount);

        // Monitoring names the blocked ticket and raises the alert.
        using (var scope = f.Factory.Services.CreateScope())
        {
            var snapshot = await scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.QueueingIntegration.Abstractions.IQueueingStore>()
                .GetMonitoringSnapshotAsync(DateTime.UtcNow, 50, TimeSpan.FromMinutes(15));
            var blocked = Assert.Single(snapshot.BlockedTickets, b => b.ExternalTicketId == id);
            Assert.Equal(assign.EventId, blocked.HeadEventId);
            Assert.Equal(1, blocked.HeldBehind);
            Assert.True(blocked.Alert);                                             // Failed always alerts
            Assert.True(snapshot.AlertCount >= 1);
        }

        // The audited administrator action restarts it: it applies, and the held event is released right after it, in order.
        var admin = (await f.Factory.SeedEmployeeAsync(TigerCS.Domain.Modules.IdentityAndAccess.Roles.SystemAdministrator)).EmployeeId;
        using (var scope = f.Factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<QueueingEventProcessor>().ReprocessAsync(admin, assign.EventId!);
            Assert.Equal("Failed", result!.PreviousStatus);
            Assert.Equal(nameof(QueueingEventStatus.Processed), result.Status);
        }

        Assert.Equal(TicketStatus.Closed, (await f.FindTicketAsync(id))!.TicketStatus);
        Assert.Equal(QueueingEventStatus.Processed, (await f.FindEventAsync(close.EventId!))!.Status);
        var audit = await f.ReadAsync(db => db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == QueueingAuditActions.EventReprocessed && a.EntityId == assign.EventId).ToListAsync());
        var entry = Assert.Single(audit);
        Assert.Equal(admin, entry.ActorEmployeeId);
        Assert.Contains("Status=Failed", entry.BeforeValue);
    }

    [Fact]
    public async Task AHeldEventWhosePredecessorHasSettled_IsNeverStranded_TheSweepAppliesIt()
    {
        // The release that should have applied it missed it (two nodes racing): it is held "behind" an event that is no longer unresolved. It must still be applied.
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        var close = Simple("TicketClosed", id, 2);
        await f.ReadAsync(async db =>
        {
            var row = new QueueingEvent(close.EventId!, QueueingEventType.TicketClosed, id, 2, close.OccurredAtUtc!.Value, close.ToCanonicalJson(), DateTime.UtcNow);
            row.MarkHeldForOrdering(QueueingOutcomeCodes.AwaitingPreviousEvent, "Held behind sequence 1.", null);
            db.QueueingEvents.Add(row);
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(TicketStatus.Open, (await f.FindTicketAsync(id))!.TicketStatus);

        Assert.True(await f.RetryAsync() >= 1);

        Assert.Equal(TicketStatus.Closed, (await f.FindTicketAsync(id))!.TicketStatus);
        Assert.Equal(QueueingEventStatus.Processed, (await f.FindEventAsync(close.EventId!))!.Status);
    }

    [Fact]
    public async Task Reprocess_IsRefusedForAnEventThatIsNotStuck_AndForAnUnknownEvent()
    {
        var id = NewTicketId();
        var created = Created(id);
        await f.PostAsync(created);
        var admin = (await f.Factory.SeedEmployeeAsync(TigerCS.Domain.Modules.IdentityAndAccess.Roles.SystemAdministrator)).EmployeeId;

        using var scope = f.Factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<QueueingEventProcessor>();
        Assert.Equal("NotReprocessable", (await processor.ReprocessAsync(admin, created.EventId!))!.Status);
        Assert.Null(await processor.ReprocessAsync(admin, "no-such-event"));
    }

    [Fact]
    public void TheFailureClasses_AreExplicit_AndOnlyConfigurationHoldsAreUnbounded()
    {
        Assert.Equal(QueueingFailureClass.Ordering, QueueingOutcomeCodes.Classify(QueueingOutcomeCodes.AwaitingPreviousEvent));
        Assert.Equal(QueueingFailureClass.Configuration, QueueingOutcomeCodes.Classify(QueueingOutcomeCodes.AwaitingTicket));   // waiting for the creation event: retried at a capped backoff, never given up
        foreach (var code in new[]
                 {
                     QueueingOutcomeCodes.CompanyNotMapped, QueueingOutcomeCodes.TicketTypeNotMapped, QueueingOutcomeCodes.RequestTypeUnusable, QueueingOutcomeCodes.UserNotMapped,
                     QueueingOutcomeCodes.UserInactive, QueueingOutcomeCodes.UserNotInDepartment, QueueingOutcomeCodes.ChannelNotConfigured,
                     QueueingOutcomeCodes.ServiceAccountNotConfigured, QueueingOutcomeCodes.PriorityNotConfigured
                 })
        {
            Assert.Equal(QueueingFailureClass.Configuration, QueueingOutcomeCodes.Classify(code));
        }

        Assert.Equal(QueueingFailureClass.Transient, QueueingOutcomeCodes.Classify(QueueingOutcomeCodes.ConcurrentUpdate));
        Assert.Equal(QueueingFailureClass.Unexplained, QueueingOutcomeCodes.Classify(QueueingOutcomeCodes.ProcessingFailed));
        Assert.Equal(QueueingFailureClass.Unexplained, QueueingOutcomeCodes.Classify(QueueingOutcomeCodes.NotEligible));
        Assert.Equal(QueueingFailureClass.Unexplained, QueueingOutcomeCodes.Classify("SOMETHING_NEW"));   // an unknown outcome is bounded, never an endless loop

        var bounded = new QueueingEvent("e", QueueingEventType.TicketAssigned, "t", 2, DateTime.UtcNow, "{}", DateTime.UtcNow);
        bounded.MarkUnresolved(QueueingOutcomeCodes.ProcessingFailed, null, null, DateTime.UtcNow, DateTime.UtcNow, maxAttempts: 2);
        Assert.Equal(QueueingEventStatus.Unresolved, bounded.Status);
        bounded.MarkUnresolved(QueueingOutcomeCodes.ProcessingFailed, null, null, DateTime.UtcNow, DateTime.UtcNow, maxAttempts: 2);
        Assert.Equal(QueueingEventStatus.Failed, bounded.Status);
        Assert.Null(bounded.NextRetryAtUtc);

        var unbounded = new QueueingEvent("e2", QueueingEventType.TicketAssigned, "t", 2, DateTime.UtcNow, "{}", DateTime.UtcNow);
        for (var i = 0; i < 100; i++)
        {
            unbounded.MarkUnresolved(QueueingOutcomeCodes.UserNotMapped, null, null, DateTime.UtcNow, DateTime.UtcNow.AddHours(1), maxAttempts: null);
        }

        Assert.Equal(QueueingEventStatus.Unresolved, unbounded.Status);
        Assert.NotNull(unbounded.NextRetryAtUtc);
    }

    // ------------------------------------------------------------------ terminal -> terminal and terminal -> active

    [Fact]
    public async Task CancelledThenClosed_IsMirrored_AsAnOutcomeChange_OnTheSameTicket_WithoutDoubleCounting()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(Simple("TicketCancelled", id, 2));
        var cancelled = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.Closed, cancelled.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Cancelled, cancelled.ResolutionOutcome);

        var (http, closed) = await f.PostAsync(Simple("TicketClosed", id, 3));

        Assert.Equal(QueueingResultStatus.Processed, closed!.Status);
        Assert.Equal(cancelled.TicketId, closed.TicketId);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);
        Assert.Equal(0, ticket.ReopenCount);                                                       // the same closure cycle: not a reopen
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(QueueingTicketState.Closed, (await f.FindLinkAsync(ticket.TicketId))!.State);
        Assert.Equal(1, await f.CountTicketsAsync(id));

        // History kept: the Cancelled resolution is archived, not deleted; exactly one current Resolved row; the outcome change and who/when are on the trail.
        var resolutions = await f.ReadAsync(db => db.TicketResolutions.AsNoTracking().Where(r => r.TicketId == ticket.TicketId).ToListAsync());
        Assert.Equal(2, resolutions.Count);
        Assert.Equal(ResolutionOutcome.Resolved, Assert.Single(resolutions, r => r.IsCurrent).ResolutionOutcome);
        Assert.Equal(ResolutionOutcome.Cancelled, Assert.Single(resolutions, r => !r.IsCurrent).ResolutionOutcome);
        var changes = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.TicketId == ticket.TicketId && h.Dimension == TicketStatusDimension.ResolutionOutcome).OrderBy(h => h.TicketStatusHistoryId).ToListAsync());
        Assert.Equal([(byte)ResolutionOutcome.Cancelled, (byte)ResolutionOutcome.Resolved], changes.Select(h => h.NewValue).ToArray());

        // Only one closed-notification cycle was queued for the ticket (the outcome change queues none), so nothing collides or is sent twice.
        var closedEvents = await f.ReadAsync(db => db.OutboxMessages.AsNoTracking().CountAsync(m => m.EventType == OutboxEventTypes.TicketClosed && m.Payload.Contains($"\"ticketId\":{ticket.TicketId},")));
        Assert.True(closedEvents <= 1);

        // A repeat of the Closed event with a newer sequence stays rejected (nothing to mirror).
        var (_, again) = await f.PostAsync(Simple("TicketClosed", id, 4));
        Assert.Equal(QueueingResultStatus.Rejected, again!.Status);
    }

    [Fact]
    public async Task AClosedThenCancelledEvent_StaysRejected_BecauseQueueingCannotProduceIt()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(Simple("TicketClosed", id, 2));

        var (_, cancel) = await f.PostAsync(Simple("TicketCancelled", id, 3));

        Assert.Equal(QueueingResultStatus.Rejected, cancel!.Status);
        Assert.Equal((byte)ResolutionOutcome.Resolved, (await f.FindTicketAsync(id))!.ResolutionOutcome);
    }

    [Fact]
    public async Task AReactivation_OpensASeparateSlaCycle_AndKeepsEveryEarlierSlaResultAndResolution()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        await f.PostAsync(Simple("TicketClosed", id, 3));
        var closedTicket = (await f.FindTicketAsync(id))!;
        var before = await f.ReadAsync(db => db.TicketSlaInstances.AsNoTracking().Where(s => s.TicketId == closedTicket.TicketId).ToListAsync());

        var (_, reactivated) = await f.PostAsync(f.Assigned(id, 4, f.QueueUserB, type: "TicketReassigned"));

        Assert.Equal(QueueingResultStatus.Processed, reactivated!.Status);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Equal(f.EmployeeB, ticket.CurrentOwnerEmployeeId);
        var after = await f.ReadAsync(db => db.TicketSlaInstances.AsNoTracking().Where(s => s.TicketId == ticket.TicketId).OrderBy(s => s.TicketSlaInstanceId).ToListAsync());
        Assert.Equal(before.Count + 1, after.Count);                                                // a new cycle, never an overwrite
        foreach (var old in before)
        {
            var kept = Assert.Single(after, s => s.TicketSlaInstanceId == old.TicketSlaInstanceId);
            Assert.Equal(old.ResolutionDueAtUtc, kept.ResolutionDueAtUtc);                         // the original cycle's dates and results are untouched
            Assert.Equal(old.ResolutionBreached, kept.ResolutionBreached);
            Assert.Equal(old.FirstResponseBreached, kept.FirstResponseBreached);
        }

        Assert.Single(after, s => s.PeriodEndAtUtc == null);
        var resolutions = await f.ReadAsync(db => db.TicketResolutions.AsNoTracking().Where(r => r.TicketId == ticket.TicketId).ToListAsync());
        Assert.Single(resolutions);                                                                 // the first closure's resolution is archived, not deleted
        Assert.False(resolutions[0].IsCurrent);
    }

    // ------------------------------------------------------------------ events derived from the queue's current state (post-commit reconciliation)

    private const string ReconciledReason = "Reconciled from Queueing state";

    [Theory]
    [InlineData("queue-user-a")]                    // the last owner, mapped
    [InlineData("queue-user-last-owner-unmapped")]  // the last owner, not mapped to any TigerCS employee
    public async Task ACreation_ThenDirectlyAClosure_ClosesCleanlyAsResolved_WithNoFictionalAssignmentHistory(string lastOwner)
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));

        var close = Simple("TicketClosed", id, 2, reason: ReconciledReason) with { PerformedByUserId = lastOwner };
        var (http, closed) = await f.PostAsync(close);

        Assert.Equal(System.Net.HttpStatusCode.OK, http.StatusCode);
        Assert.Equal(QueueingResultStatus.Processed, closed!.Status);          // an unmapped last owner never holds a closure: it is only the performer
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(QueueingTicketState.Closed, (await f.FindLinkAsync(ticket.TicketId))!.State);
        // No assignment was ever reported, so none is invented: no assignment row, and the ticket never went InProgress.
        Assert.Equal(0, await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().CountAsync(a => a.TicketId == ticket.TicketId)));
        var statuses = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.TicketId == ticket.TicketId && h.Dimension == TicketStatusDimension.TicketStatus).Select(h => h.NewValue).ToListAsync());
        Assert.DoesNotContain((byte)TicketStatus.InProgress, statuses);
        Assert.Single(await f.ReadAsync(db => db.TicketResolutions.AsNoTracking().Where(r => r.TicketId == ticket.TicketId).ToListAsync()));
    }

    [Fact]
    public async Task ACreation_ThenDirectlyACancellation_ClosesAsCancelled_WithNoAssignmentHistory()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));

        var (_, cancelled) = await f.PostAsync(Simple("TicketCancelled", id, 2, reason: ReconciledReason));

        Assert.Equal(QueueingResultStatus.Processed, cancelled!.Status);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Cancelled, ticket.ResolutionOutcome);
        Assert.Equal(0, await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().CountAsync(a => a.TicketId == ticket.TicketId)));
        Assert.Equal(QueueingTicketState.Cancelled, (await f.FindLinkAsync(ticket.TicketId))!.State);
    }

    [Theory]
    [InlineData("TicketAssigned")]
    [InlineData("TicketReassigned")]
    public async Task ACancelledTicket_ThenAnAssignment_IsReactivated_WithASeparateSlaCycle_AndEarlierSlaResultsUntouched(string assignType)
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(Simple("TicketCancelled", id, 2, reason: ReconciledReason));
        var cancelled = (await f.FindTicketAsync(id))!;
        var before = await f.ReadAsync(db => db.TicketSlaInstances.AsNoTracking().Where(s => s.TicketId == cancelled.TicketId).ToListAsync());

        var (_, reactivated) = await f.PostAsync(f.Assigned(id, 3, type: assignType));

        Assert.Equal(QueueingResultStatus.Processed, reactivated!.Status);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Equal(f.EmployeeA, ticket.CurrentOwnerEmployeeId);
        Assert.Null(ticket.ResolutionOutcome);
        var after = await f.ReadAsync(db => db.TicketSlaInstances.AsNoTracking().Where(s => s.TicketId == ticket.TicketId).ToListAsync());
        Assert.Equal(before.Count + 1, after.Count);
        foreach (var old in before)
        {
            var kept = Assert.Single(after, s => s.TicketSlaInstanceId == old.TicketSlaInstanceId);
            Assert.Equal(old.PeriodStartAtUtc, kept.PeriodStartAtUtc);
            Assert.Equal(old.ResolutionDueAtUtc, kept.ResolutionDueAtUtc);
            Assert.Equal(old.ResolutionBreached, kept.ResolutionBreached);
        }

        Assert.Single(after, s => s.PeriodEndAtUtc == null);
        // ...and Queueing can close it again: still one TigerCS ticket.
        var (_, closed) = await f.PostAsync(Simple("TicketClosed", id, 4));
        Assert.Equal(QueueingResultStatus.Processed, closed!.Status);
        Assert.Equal(1, await f.CountTicketsAsync(id));
    }

    [Theory]
    [InlineData("TicketAssigned")]
    [InlineData("TicketReassigned")]
    public async Task AClosedTicket_ThenAnAssignment_IsReactivated(string assignType)
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(Simple("TicketClosed", id, 2, reason: ReconciledReason));

        var (_, reactivated) = await f.PostAsync(f.Assigned(id, 3, f.QueueUserB, type: assignType));

        Assert.Equal(QueueingResultStatus.Processed, reactivated!.Status);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Equal(f.EmployeeB, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(1, await f.CountTicketsAsync(id));
    }

    [Fact]
    public async Task OccurredAtUtc_IsNotAnOrderingAuthority_TheSequenceIs_AndTheHistoryNeverRunsBackwards()
    {
        var id = NewTicketId();
        var created = DateTime.UtcNow.AddMinutes(-10);
        await f.PostAsync(Created(id, at: created));

        // Reconciled events: their OccurredAtUtc is NOT monotonic with EventSequence - seq 2 is an hour BEFORE the ticket was created, seq 3 is earlier still. Applied by sequence.
        var close = Simple("TicketClosed", id, 2, at: created.AddHours(-1), reason: ReconciledReason);
        var (_, closed) = await f.PostAsync(close);
        Assert.Equal(QueueingResultStatus.Processed, closed!.Status);                 // never rejected or superseded because of its time
        var reactivate = f.Assigned(id, 3, at: created.AddHours(-2), type: "TicketReassigned");
        var (_, reactivated) = await f.PostAsync(reactivate with { Reason = ReconciledReason });
        Assert.Equal(QueueingResultStatus.Processed, reactivated!.Status);

        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);                    // seq 3 is the newest state, whatever its time says
        var link = (await f.FindLinkAsync(ticket.TicketId))!;
        Assert.Equal(3, link.LastAppliedSequence);

        // The ticket's own history is stamped monotonically: nothing before the creation, nothing running backwards.
        var history = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.TicketId == ticket.TicketId).OrderBy(h => h.TicketStatusHistoryId).Select(h => h.OccurredAtUtc).ToListAsync());
        Assert.All(history, at => Assert.True(at >= ticket.CreatedAtUtc, $"{at:O} is before the ticket's creation {ticket.CreatedAtUtc:O}"));
        Assert.Equal(history.Order(), history);
        var resolution = Assert.Single(await f.ReadAsync(db => db.TicketResolutions.AsNoTracking().Where(r => r.TicketId == ticket.TicketId).ToListAsync()));
        Assert.True(resolution.ResolvedAtUtc >= ticket.CreatedAtUtc);
        var periods = await f.ReadAsync(db => db.TicketSlaInstances.AsNoTracking().Where(s => s.TicketId == ticket.TicketId).ToListAsync());
        Assert.All(periods, p => Assert.True(p.PeriodStartAtUtc >= ticket.CreatedAtUtc));
        Assert.All(periods.Where(p => p.PeriodEndAtUtc != null), p => Assert.True(p.PeriodEndAtUtc >= p.PeriodStartAtUtc));
    }

    [Fact]
    public async Task OnlyTheExistingFiveMinuteFutureRule_RejectsAnOccurredAtUtc()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));

        var (future, body) = await f.PostAsync(Simple("TicketClosed", id, 2, at: DateTime.UtcNow.AddMinutes(10)));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Contains("OccurredAtUtc", body!.Errors!.Keys);

        // A time older than the previous event's (and older than the ticket) is fine.
        var (old, ok) = await f.PostAsync(Simple("TicketClosed", id, 2, at: DateTime.UtcNow.AddDays(-3), reason: ReconciledReason));
        Assert.Equal(System.Net.HttpStatusCode.OK, old.StatusCode);
        Assert.Equal(QueueingResultStatus.Processed, ok!.Status);
    }

    [Fact]
    public async Task AReconciledAssignmentForAnUnmappedEmployee_IsHeldAndAlerted_LaterEventsWaitBehindIt_OtherTicketsAreUnaffected()
    {
        var id = NewTicketId();
        var other = NewTicketId();
        await f.PostAsync(Created(id));
        var assign = f.Assigned(id, 2, "queue-user-reconciled-unmapped");
        var (_, held) = await f.PostAsync(assign with { Reason = ReconciledReason });
        Assert.Equal(QueueingOutcomeCodes.UserNotMapped, held!.Outcome);
        var close = Simple("TicketClosed", id, 3, reason: ReconciledReason);
        var (_, behind) = await f.PostAsync(close);
        Assert.Equal(QueueingOutcomeCodes.AwaitingPreviousEvent, behind!.Outcome);

        await f.PostAsync(Created(other));
        await f.PostAsync(Simple("TicketCancelled", other, 2, reason: ReconciledReason));
        Assert.Equal(TicketStatus.Closed, (await f.FindTicketAsync(other))!.TicketStatus);          // another ticket flows
        Assert.Equal(TicketStatus.Open, (await f.FindTicketAsync(id))!.TicketStatus);               // this one waits - nothing applied around the held assignment

        using var scope = f.Factory.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.QueueingIntegration.Abstractions.IQueueingStore>()
            .GetMonitoringSnapshotAsync(DateTime.UtcNow.AddMinutes(30), 50, TimeSpan.FromMinutes(15));
        var blocked = Assert.Single(snapshot.BlockedTickets, b => b.ExternalTicketId == id);
        Assert.True(blocked.Alert);                                                                  // blocked for longer than the alert threshold
        Assert.Equal(QueueingOutcomeCodes.UserNotMapped, blocked.HeadOutcomeCode);
        Assert.Equal(1, blocked.HeldBehind);
        Assert.DoesNotContain(snapshot.BlockedTickets, b => b.ExternalTicketId == other);
    }

    // ------------------------------------------------------------------ the mirror cannot be changed from TigerCS

    [Fact]
    public async Task TheAllowPolicy_IsIgnored_AManualChangeIsStillBlocked()
    {
        var id = NewTicketId();
        var (_, created) = await f.PostAsync(Created(id));
        var manager = (await f.Factory.SeedEmployeeAsync(TigerCS.Domain.Modules.IdentityAndAccess.Roles.CsManager)).EmployeeId;
        var ticketId = created!.TicketId!.Value;
        f.Options.ManualChangePolicy = QueueingManualChangePolicy.Allow;
        try
        {
            using var scope = f.Factory.Services.CreateScope();
            var ticket = (await f.FindTicketAsync(id))!;
            var manual = await scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Ticketing.Services.TicketAssignmentAppService>()
                .AssignAsync(manager, [TigerCS.Domain.Modules.IdentityAndAccess.Roles.CsManager], ticketId,
                    new TigerCS.Application.Modules.Ticketing.Dto.AssignTicketRequestDto(f.EmployeeB, ticket.RowVersion));
            Assert.Equal(TigerCS.Application.Modules.Ticketing.Dto.TicketMutationOutcome.QueueManagedTicket, manual.Outcome);
            Assert.Null((await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);
        }
        finally
        {
            f.Options.ManualChangePolicy = QueueingManualChangePolicy.Block;
        }
    }

    [Fact]
    public async Task EverySystemClosurePath_RefusesAQueueManagedTicket_OutsideTheIntegration()
    {
        var id = NewTicketId();
        var (_, created) = await f.PostAsync(Created(id));
        var ticketId = created!.TicketId!.Value;
        var actor = f.ServiceAccountId;

        using var scope = f.Factory.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Ticketing.Services.TicketLifecycleAppService>();
        var blocked = TigerCS.Application.Modules.Ticketing.Dto.TicketMutationOutcome.QueueManagedTicket;

        Assert.Equal(blocked, (await lifecycle.CloseForCustomerInactivityAsync(actor, ticketId, "inactive")).Outcome);
        Assert.Equal(blocked, (await lifecycle.CloseForAiOnlyTimeoutAsync(actor, ticketId, "ai only", "detail")).Outcome);
        Assert.Equal(blocked, (await lifecycle.CloseForAiCompletionAsync(actor, ticketId, "ai done", "detail")).Outcome);
        Assert.Equal(blocked, (await lifecycle.CloseForDocumentDeliveryAsync(
            actor, ticketId, TigerCS.Domain.Modules.Ticketing.ResolutionOutcome.Resolved, "doc", "detail")).Outcome);
        Assert.Equal(TicketStatus.Open, (await f.FindTicketAsync(id))!.TicketStatus);
    }

    private static readonly string[] GuardedNames =
    [
        nameof(Ticket.AssignTo), nameof(Ticket.AssignToAiAgent), nameof(Ticket.ReleaseOwner), nameof(Ticket.TransferToDepartment), nameof(Ticket.ChangeStatus),
        nameof(Ticket.Resolve), nameof(Ticket.CloseForCustomerInactivity), nameof(Ticket.CloseForDocumentDelivery), nameof(Ticket.CloseByExternalSystem),
        nameof(Ticket.CloseForAiCompletion), nameof(Ticket.CloseForAiOnlyTimeout), nameof(Ticket.Close), nameof(Ticket.Reopen), nameof(Ticket.ReturnToQueue),
        nameof(Ticket.ReopenForQueue), nameof(Ticket.ChangeQueueClosureOutcome)
    ];

    public static TheoryData<string> GuardedTicketMutators()
    {
        var data = new TheoryData<string>();
        foreach (var name in GuardedNames)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GuardedTicketMutators))]
    public void EveryLifecycleAndAssignmentMutator_ThrowsForAQueueManagedTicket_UnlessTheIntegrationIsApplyingAnEvent(string mutator)
    {
        var queued = Ticket.CreateUnverified("TG-X-GUARD", 1, 1, 3, "x", DateTime.UtcNow);
        queued.RecordExternalOrigin(TicketIntegrationSources.QueueingSystem, "qt-guard", "A-1");
        var method = typeof(Ticket).GetMethods().Single(m => m.Name == mutator);
        var arguments = method.GetParameters().Select(p => p.ParameterType switch
        {
            var t when t == typeof(Guid) => (object)Guid.NewGuid(),
            var t when t == typeof(int) => 1,
            var t when t == typeof(long?) => null!,
            var t when t == typeof(TicketStatus) => TicketStatus.InProgress,
            var t when t == typeof(ResolutionOutcome) => ResolutionOutcome.Resolved,
            var t when t == typeof(bool) => false,
            _ => throw new InvalidOperationException($"No test argument for {p.ParameterType}.")
        }).ToArray();

        var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(queued, arguments));
        Assert.IsType<QueueManagedTicketMutationException>(thrown.InnerException);

        // ...and the guard is open only inside the integration's scope (the method may then refuse for its own reasons, never because of the guard).
        using (QueueingMutationScope.Open())
        {
            try
            {
                method.Invoke(queued, arguments);
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                Assert.IsNotType<QueueManagedTicketMutationException>(ex.InnerException);
            }
        }

        Assert.False(QueueingMutationScope.IsOpen);
    }

    [Fact]
    public void EveryPublicLifecycleOrAssignmentMutatorOnTicket_IsInTheGuardedList()
    {
        // A new Ticket method that moves the lifecycle or the assignment must either be guarded (add it to GuardedTicketMutators) or be consciously listed as harmless here.
        var guarded = GuardedNames.ToHashSet();
        var reviewedHarmless = new HashSet<string>
        {
            nameof(Ticket.StartInProgressAtCreation),   // creation time only: refuses a persisted ticket
            nameof(Ticket.RestartSlaClockForReopen),    // SLA dimension only, called inside the guarded reopen
            nameof(Ticket.EnsureQueueMutationAllowed)   // the guard itself
        };
        var pattern = new[] { "Assign", "Release", "Transfer", "ChangeStatus", "Resolve", "Close", "Reopen", "ReturnToQueue", "StartInProgress", "Owner", "RestartSla" };
        var candidates = typeof(Ticket).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && pattern.Any(p => m.Name.Contains(p, StringComparison.Ordinal)))
            .Select(m => m.Name).Distinct().ToList();

        var unreviewed = candidates.Where(n => !guarded.Contains(n) && !reviewedHarmless.Contains(n)).ToList();
        Assert.True(unreviewed.Count == 0, "Ticket mutators neither guarded nor reviewed: " + string.Join(", ", unreviewed));
    }

    [Fact]
    public void EveryBackgroundJob_HasBeenReviewedAgainstTheMirror()
    {
        // Each recurring job / hosted service of the application, with why it cannot change a queue-managed ticket's lifecycle or assignment. A new job fails this test
        // until someone reviews it and adds it here - the domain guard (Ticket.EnsureQueueMutationAllowed) is the safety net, this is the review.
        var reviewed = new Dictionary<string, string>
        {
            ["AiOnlyAutoCloseJob"] = "closes only Genesys AI-only tickets; TicketLifecycleAppService.CloseBySystemAsync refuses a queue ticket (QueueManagedTicketGuard) and the domain throws",
            ["CallCenterReconciliationRunLock"] = "a run lock, not a mutator",
            ["CallCenterUnassignedWorkHostedService"] = "schedules CallCenterUnassignedWorkJob",
            ["CallCenterUnassignedWorkJob"] = "reads TicketUnassignedWorkRepository, which excludes queue-managed tickets",
            ["ChatbotInactivityCloseJob"] = "closes website/chatbot conversations via CloseForCustomerInactivityAsync, which refuses a queue ticket",
            ["CollectionsAutoDispatchJob"] = "Collections customer dispatch (messages), no ticket lifecycle or assignment",
            ["CollectionsCrmOwnersRefreshJob"] = "Collections CRM owner data, no tickets",
            ["CollectionsDispatchJob"] = "Collections customer dispatch (messages), no ticket lifecycle or assignment",
            ["CollectionsReceivablesCompanyRetryJob"] = "Collections receivables data, no tickets",
            ["CollectionsReviewRefreshJob"] = "Collections review data, no tickets",
            ["CollectionsSuppressionJob"] = "Collections dispatch suppression, no tickets",
            ["HangfireReviewJobScheduler"] = "schedules Collections review jobs",
            ["InProcessReviewJobScheduler"] = "schedules Collections review jobs",
            ["NoOpReviewJobScheduler"] = "does nothing",
            ["CollectionsReceivablesRefreshJob"] = "Collections receivables data, no tickets",
            ["CollectionsReminderScheduleJob"] = "Collections reminders, no ticket lifecycle",
            ["CsAgentBackfillRunner"] = "lists candidates through TicketUnassignedWorkRepository, which excludes queue-managed tickets",
            ["HangfireSlaDeadlineScheduler"] = "schedules SLA deadline checks, no lifecycle or assignment",
            ["NoOpSlaDeadlineScheduler"] = "does nothing",
            ["OutboxDispatchJob"] = "delivers notifications, never changes a ticket",
            ["QueueingEventRetryJob"] = "the integration's own retry: applies Queueing events inside the IntegrationEventScope",
            ["QueueingPayloadRetentionJob"] = "erases processed event payloads, never a ticket",
            ["SlaDeadlineCheckJob"] = "SLA breach detection, not lifecycle or assignment",
            ["SlaSweepJob"] = "SLA breach detection, not lifecycle or assignment",
            ["StalePhoneAssignmentHostedService"] = "schedules StalePhoneAssignmentJob",
            ["StalePhoneAssignmentJob"] = "reads TicketUnassignedWorkRepository, which excludes queue-managed tickets",
            ["VoiceCallReconciliationHostedService"] = "schedules VoiceCallReconciliationJob",
            ["VoiceCallReconciliationJob"] = "Genesys phone interactions only; queue tickets have none, and the domain guard refuses a queue ticket anyway"
        };

        var actual = typeof(TigerCS.Infrastructure.BackgroundJobs.QueueingEventRetryJob).Assembly.GetTypes()
            .Where(t => t.Namespace == "TigerCS.Infrastructure.BackgroundJobs" && !t.IsNested && !t.IsInterface && !t.IsEnum
                && (t.Name.EndsWith("Job", StringComparison.Ordinal) || t.Name.EndsWith("HostedService", StringComparison.Ordinal)
                    || t.Name.EndsWith("Runner", StringComparison.Ordinal) || t.Name.EndsWith("Scheduler", StringComparison.Ordinal)
                    || t.Name.EndsWith("RunLock", StringComparison.Ordinal)))
            .Select(t => t.Name).ToHashSet();

        Assert.True(actual.Except(reviewed.Keys).Count() == 0, "Background jobs not yet reviewed against the queue mirror: " + string.Join(", ", actual.Except(reviewed.Keys)));
        Assert.True(reviewed.Keys.Except(actual).Count() == 0, "Reviewed jobs that no longer exist (remove them): " + string.Join(", ", reviewed.Keys.Except(actual)));
    }
}
