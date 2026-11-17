using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.QueueingIntegration;
using TigerCS.Application.Modules.QueueingIntegration.Abstractions;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Application.Modules.QueueingIntegration.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Ticketing.Integration;
using static TigerCS.Tests.QueueingIntegration.QueueingFixture;

namespace TigerCS.Tests.QueueingIntegration;

/// <summary>
/// Atomicity and concurrency of the Queueing integration, proven on a REAL SQL Server with the real migrations.
///
/// <para>
/// <b>Independent application instances.</b> Every test drives <see cref="InstanceCount"/> separate hosts (separate DI containers, separate scoped DbContexts, therefore
/// separate SqlConnections) over the one database, each with a <b>no-op</b> <see cref="IQueueingEventLock"/>: nothing in this process serialises the requests, so the only
/// things that can keep the data consistent are the database's own guarantees (unique EventId, unique (IntegrationSource, ExternalTicketId), the link's row version,
/// the ticket's row version, and the transaction around each event). Requests are released together through a <see cref="TaskCompletionSource"/> gate - no sleeps.
/// </para>
///
/// <para>
/// <b>Faults</b> are real database faults: a trigger that THROWs, installed on the table written LAST in the event's transaction, i.e. after the ticket / owner rows have
/// already been inserted or updated inside it. What must survive such a fault is nothing; what must follow it is a clean retry of the very same event.
/// </para>
/// </summary>
public sealed class QueueingAtomicitySqlServerTests(QueueingSqlServerFixture f) : IClassFixture<QueueingSqlServerFixture>, IAsyncLifetime
{
    private const int InstanceCount = 4;

    private readonly List<TigerCsApiFactory> _instances = [];
    private readonly List<HttpClient> _clients = [];

    public async Task InitializeAsync()
    {
        if (!f.Enabled)
        {
            return;
        }

        for (var i = 0; i < InstanceCount; i++)
        {
            var instance = new TigerCsApiFactory
            {
                ExtraConfiguration = HostConfiguration(),
                SqlServerConnectionString = f.ConnectionString,
                ExtraServices = services =>
                {
                    foreach (var d in services.Where(d => d.ServiceType == typeof(IQueueingEventLock)).ToList())
                    {
                        services.Remove(d);
                    }

                    services.AddSingleton<IQueueingEventLock, NoLock>();
                }
            };
            instance.Services.GetRequiredService<QueueingIntegrationOptions>().ServiceAccountEmployeeId = f.ServiceAccountId;
            Assert.IsType<NoLock>(instance.Services.GetRequiredService<IQueueingEventLock>());
            var client = instance.CreateClient();
            client.DefaultRequestHeaders.Add(TigerCS.Api.Integrations.QueueingApiKeyDefaults.DefaultHeaderName, ApiKey);
            _instances.Add(instance);
            _clients.Add(client);

            // Warm the pipeline (JIT, DI graph, connection pool) so the later races overlap for real: an invalid event is refused, nothing is stored.
            var (http, _) = await f.PostAsync(new QueueingEventRequest(null, "TicketCreated", null, null), client);
            Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        }
    }

    public Task DisposeAsync()
    {
        foreach (var instance in _instances)
        {
            instance.Dispose();
        }

        return Task.CompletedTask;
    }

    private sealed class NoLock : IQueueingEventLock
    {
        public Task<IDisposable?> TryAcquireAsync(string externalTicketId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDisposable?>(new Nothing());

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(HttpResponseMessage Http, QueueingEventResponse? Body)[]> FireAsync(params (QueueingEventRequest Request, int Instance)[] calls)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(c => Task.Run(async () =>
        {
            await gate.Task;
            return await f.PostAsync(c.Request, _clients[c.Instance % InstanceCount]);
        })).ToArray();
        gate.SetResult();   // everyone is released at the same moment
        return await Task.WhenAll(tasks);
    }

    /// <summary>Runs the retry sweep on every instance at once, repeatedly, until the queue ticket has nothing pending (bounded: a failure to converge fails the test).</summary>
    private async Task SettleAsync(string externalTicketId)
    {
        for (var round = 0; round < 12; round++)
        {
            var pending = await f.ReadAsync(db => db.QueueingEvents.AsNoTracking()
                .CountAsync(e => e.ExternalTicketId == externalTicketId && (e.Status == QueueingEventStatus.Unresolved || e.Status == QueueingEventStatus.Received)));
            if (pending == 0)
            {
                return;
            }

            await f.MakeRetriesDueAsync();
            await Task.WhenAll(_instances.Select(async instance =>
            {
                using var scope = instance.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<QueueingEventProcessor>().RetryDueAsync(100);
            }));
        }

        var left = await f.ReadAsync(db => db.QueueingEvents.AsNoTracking()
            .Where(e => e.ExternalTicketId == externalTicketId && (e.Status == QueueingEventStatus.Unresolved || e.Status == QueueingEventStatus.Received))
            .Select(e => e.EventType + ":" + e.OutcomeCode + ":" + e.OutcomeDetail).ToListAsync());
        Assert.Fail($"Queue ticket {externalTicketId} still has pending events after 12 sweeps: {string.Join(" | ", left)}");
    }

    private Task<int> CountAsync<T>(Func<TigerCsDbContext, IQueryable<T>> query) where T : class =>
        f.ReadAsync(db => query(db).AsNoTracking().CountAsync());

    private async Task WithFaultAsync(string name, string createTriggerSql, Func<Task> act)
    {
        await f.ExecuteSqlAsync(createTriggerSql);
        try
        {
            await act();
        }
        finally
        {
            await f.ExecuteSqlAsync($"DROP TRIGGER {name}");
        }
    }

    private static string UniquePhone() => "+9715" + Random.Shared.Next(10_000_000, 99_999_999);

    private sealed record Counts(int Tickets, int Intakes, int Links, int Contacts, int Sla, int History, int Outbox, int QueueAudit, int Assignments, int Notes);

    private async Task<Counts> SnapshotAsync() => new(
        await CountAsync(db => db.Tickets), await CountAsync(db => db.IntakeRecords), await CountAsync(db => db.QueueingTicketLinks),
        await CountAsync(db => db.CustomerContactDetails), await CountAsync(db => db.TicketSlaInstances), await CountAsync(db => db.TicketStatusHistoryEntries),
        await CountAsync(db => db.OutboxMessages), await CountAsync(db => db.AuditEntries.Where(a => a.Action.StartsWith("Queueing") || a.Action == "CreateIntakeRecord" || a.Action == "ReturnToQueue")),
        await CountAsync(db => db.TicketAssignments), await CountAsync(db => db.TicketNotes));

    /// <summary>The customer is never emailed, and nothing was even queued for them, whatever the flow: asserted on the outbox, the notifications and every instance's sender.</summary>
    private async Task AssertNoCustomerEmailAsync(params string[] externalTicketIds)
    {
        var ticketIds = await f.ReadAsync(db => db.Tickets.AsNoTracking().Where(t => externalTicketIds.Contains(t.ExternalTicketId!)).Select(t => t.TicketId).ToListAsync());
        var outbox = (await f.Factory.GetOutboxMessagesAsync())
            .Where(m => ticketIds.Any(id => m.Payload.Contains($"\"ticketId\":{id},") || m.Payload.Contains($"\"ticketId\":{id}}}")))
            .ToList();
        Assert.DoesNotContain(outbox, m => m.EventType != OutboxEventTypes.TicketAssignedToAgent);   // only the internal "assigned to you" event may exist

        await _instances[0].RunOutboxDispatchAsync();
        foreach (var id in ticketIds)
        {
            var notifications = await f.Factory.GetNotificationsAsync(id);
            Assert.DoesNotContain(notifications, n => n.RecipientEmployeeId is null);
            Assert.DoesNotContain(notifications, n => n.DeliveryStatus == NotificationDeliveryStatus.Sent && n.RecipientEmployeeId is null);
        }

        foreach (var instance in _instances.Append(f.Factory))
        {
            Assert.DoesNotContain(instance.EmailSender.Recorded, m =>
                m.Audience == TigerCS.Application.Modules.Notifications.Abstractions.EmailAudience.Customer && m.ToAddress == "aisha@example.test");
        }
    }

    // ------------------------------------------------------------------ the unit of work

    [VisibilitySqlServerFact]
    public async Task TheUnitOfWork_JoinsAnAmbientTransaction_SoAnInnerCommitIsNotACommit_AndALoneTransactionStillCommitsAsBefore()
    {
        var rolledBack = "company-uow-" + Guid.NewGuid().ToString("N")[..8];
        var committed = "company-uow-" + Guid.NewGuid().ToString("N")[..8];
        var alone = "company-uow-" + Guid.NewGuid().ToString("N")[..8];

        using (var scope = f.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Ticketing.Abstractions.ITicketingUnitOfWork>();

            await using (var outer = await unitOfWork.BeginTransactionAsync())
            {
                await using (var inner = await unitOfWork.BeginTransactionAsync())      // a service "begins its own" transaction: it joins
                {
                    db.QueueingCompanyMappings.Add(new QueueingCompanyMapping(rolledBack, f.DepartmentId, DateTime.UtcNow));
                    await unitOfWork.SaveChangesAsync();
                    await inner.CommitAsync();                                            // not a commit: only the owner commits
                }

            }                                                                             // the owner never committed: everything the inner scope "committed" is rolled back

            Assert.Equal(0, await CountAsync(x => x.QueueingCompanyMappings.Where(m => m.CompanyId == rolledBack)));

            await using (var outer = await unitOfWork.BeginTransactionAsync())
            {
                await using (var inner = await unitOfWork.BeginTransactionAsync())
                {
                    db.QueueingCompanyMappings.Add(new QueueingCompanyMapping(committed, f.DepartmentId, DateTime.UtcNow));
                    await unitOfWork.SaveChangesAsync();
                    await inner.CommitAsync();
                }

                await outer.CommitAsync();
            }

            Assert.Equal(1, await CountAsync(x => x.QueueingCompanyMappings.Where(m => m.CompanyId == committed)));

            // Unchanged behaviour for every existing caller: a transaction that is not nested commits on its own.
            await using (var only = await unitOfWork.BeginTransactionAsync())
            {
                db.QueueingCompanyMappings.Add(new QueueingCompanyMapping(alone, f.DepartmentId, DateTime.UtcNow));
                await unitOfWork.SaveChangesAsync();
                await only.CommitAsync();
            }

            Assert.Equal(1, await CountAsync(x => x.QueueingCompanyMappings.Where(m => m.CompanyId == alone)));
        }
    }

    // ------------------------------------------------------------------ creation: concurrency

    [VisibilitySqlServerFact]
    public async Task TheSameCreatedEvent_DeliveredByManyIndependentInstancesAtOnce_MakesOneTicketOneLinkOneInboxRow_AndEveryCallerGetsTheSameAnswer()
    {
        var id = NewTicketId();
        var phone = UniquePhone();
        var request = Created(id) with { CustomerPhone = phone };
        var results = await FireAsync(Enumerable.Range(0, InstanceCount * 6).Select(i => (request, i)).ToArray());

        Assert.All(results, r => Assert.True(
            r.Http.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.ServiceUnavailable, r.Http.StatusCode.ToString()));
        Assert.True(results.Count(r => r.Http.StatusCode == HttpStatusCode.Created) <= 1, "at most one caller is told it created the ticket");
        Assert.All(results.Where(r => r.Body is not null), r => Assert.Equal(request.EventId, r.Body!.EventId));

        await SettleAsync(id);

        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(1, await f.CountTicketsAsync(id));
        Assert.Equal(1, await CountAsync(db => db.QueueingTicketLinks.Where(l => l.ExternalTicketId == id)));
        Assert.Equal(1, await CountAsync(db => db.QueueingEvents.Where(e => e.EventId == request.EventId)));
        Assert.Equal(QueueingEventStatus.Processed, (await f.FindEventAsync(request.EventId!))!.Status);
        Assert.Equal(1, await CountAsync(db => db.IntakeRecords.Where(i => i.PhoneNumber == phone)));            // no orphan intake record from a losing attempt
        Assert.Equal(0, await CountAsync(db => db.IntakeRecords.Where(i => i.PhoneNumber == phone && i.LinkedTicketId == null)));
        Assert.Equal(1, await CountAsync(db => db.AuditEntries.Where(a => a.Action == "QueueingTicketCreated" && a.EntityId == ticket.TicketId.ToString())));

        // Consistent answers: every response that names a ticket names THE ticket, and a later redelivery is a plain Duplicate of the same one.
        Assert.All(results.Where(r => r.Body?.TicketId is not null), r => Assert.Equal(ticket.TicketId, r.Body!.TicketId));
        var after = await FireAsync(Enumerable.Range(0, InstanceCount).Select(i => (request, i)).ToArray());
        Assert.All(after, r =>
        {
            Assert.Equal(HttpStatusCode.OK, r.Http.StatusCode);
            Assert.Equal(QueueingResultStatus.Duplicate, r.Body!.Status);
            Assert.Equal(ticket.TicketId, r.Body.TicketId);
        });

        await AssertNoCustomerEmailAsync(id);
    }

    [VisibilitySqlServerFact]
    public async Task DifferentEventIdsForOneQueueTicket_CreatedAtOnceByIndependentInstances_MakeExactlyOneTicket()
    {
        var id = NewTicketId();
        var phone = UniquePhone();
        var requests = Enumerable.Range(0, InstanceCount * 4).Select(_ => Created(id) with { CustomerPhone = phone }).ToList();
        await FireAsync(requests.Select((r, i) => (r, i)).ToArray());
        await SettleAsync(id);

        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(1, await f.CountTicketsAsync(id));
        Assert.Equal(1, await CountAsync(db => db.QueueingTicketLinks.Where(l => l.TicketId == ticket.TicketId)));
        Assert.Equal(requests.Count, await CountAsync(db => db.QueueingEvents.Where(e => e.ExternalTicketId == id && e.Status == QueueingEventStatus.Processed)));
        Assert.Equal(1, await CountAsync(db => db.QueueingEvents.Where(e => e.ExternalTicketId == id && e.OutcomeCode == QueueingOutcomeCodes.TicketCreated)));
        Assert.Equal(requests.Count - 1, await CountAsync(db => db.QueueingEvents.Where(e => e.ExternalTicketId == id && e.OutcomeCode == QueueingOutcomeCodes.TicketAlreadyExists)));
        Assert.Equal(0, await CountAsync(db => db.IntakeRecords.Where(i => i.PhoneNumber == phone && i.LinkedTicketId == null)));   // every losing attempt rolled its intake record back
        Assert.Equal(1, await CountAsync(db => db.AuditEntries.Where(a => a.Action == "QueueingTicketCreated" && a.EntityId == ticket.TicketId.ToString())));
        await AssertNoCustomerEmailAsync(id);
    }

    // ------------------------------------------------------------------ creation: rollback and recovery

    [VisibilitySqlServerFact]
    public async Task ACreationThatFailsAfterTheTicketInsert_LeavesNothing_AndTheSameEventThenAppliesOnceOnRetry_ForEachKindOfLateFailure()
    {
        // 1) the queue-state row (written last, with the ticket's final save); 2) the integration's own audit entry (written just before it).
        var faults = new[]
        {
            ("trg_Fault_Link", "CREATE TRIGGER trg_Fault_Link ON QueueingTicketLinks AFTER INSERT AS BEGIN THROW 50001, 'forced failure', 1; END"),
            ("trg_Fault_Audit", "CREATE TRIGGER trg_Fault_Audit ON AuditEntries AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE Action = N'QueueingTicketCreated') THROW 50002, 'forced failure', 1; END"),
            ("trg_Fault_Contact", "CREATE TRIGGER trg_Fault_Contact ON CustomerContactDetails AFTER INSERT AS BEGIN THROW 50003, 'forced failure', 1; END")
        };

        foreach (var (name, sql) in faults)
        {
            var id = NewTicketId();
            var phone = UniquePhone();
            var request = Created(id) with { CustomerPhone = phone };
            var before = await SnapshotAsync();

            await WithFaultAsync(name, sql, async () =>
            {
                var results = await FireAsync(Enumerable.Range(0, InstanceCount).Select(i => (request, i)).ToArray());
                Assert.All(results, r => Assert.True(r.Http.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.ServiceUnavailable, $"{name}: {r.Http.StatusCode}"));
            });

            // Nothing survived: no ticket, intake record, queue state, contact, SLA period, history, outbox row, creation audit or intake audit.
            Assert.Equal(0, await f.CountTicketsAsync(id));
            Assert.Equal(0, await CountAsync(db => db.IntakeRecords.Where(i => i.PhoneNumber == phone)));
            Assert.Equal(before, await SnapshotAsync());
            var pending = (await f.FindEventAsync(request.EventId!))!;
            Assert.Equal(QueueingEventStatus.Unresolved, pending.Status);                  // kept, retryable
            Assert.NotNull(pending.NextRetryAtUtc);
            Assert.NotNull(pending.Payload);

            // The fault is gone: the very same event is redelivered by every instance at once and recovers to exactly one complete ticket.
            var recovered = await FireAsync(Enumerable.Range(0, InstanceCount).Select(i => (request, i)).ToArray());
            await SettleAsync(id);
            var ticket = (await f.FindTicketAsync(id))!;
            Assert.Equal(1, await f.CountTicketsAsync(id));
            Assert.NotNull(await f.FindLinkAsync(ticket.TicketId));
            Assert.Equal(QueueingEventStatus.Processed, (await f.FindEventAsync(request.EventId!))!.Status);
            Assert.Equal(1, await CountAsync(db => db.IntakeRecords.Where(i => i.PhoneNumber == phone && i.LinkedTicketId == ticket.TicketId)));
            Assert.Equal(1, await CountAsync(db => db.AuditEntries.Where(a => a.Action == "QueueingTicketCreated" && a.EntityId == ticket.TicketId.ToString())));
            Assert.Equal(1, await CountAsync(db => db.TicketSlaInstances.Where(s => s.TicketId == ticket.TicketId)));
            Assert.All(recovered.Where(r => r.Body?.TicketId is not null), r => Assert.Equal(ticket.TicketId, r.Body!.TicketId));
            await AssertNoCustomerEmailAsync(id);
        }
    }

    // ------------------------------------------------------------------ assignment: rollback and recovery

    [VisibilitySqlServerFact]
    public async Task AnAssignmentThatFailsAtItsSecondStep_RollsBackTheOwnerToo_AndTheSameEventAppliesOnceOnRetry()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id), _clients[0]);
        var ticket = (await f.FindTicketAsync(id))!;
        var before = await SnapshotAsync();
        var assign = f.Assigned(id, 2);

        // Step 1 (the owner + the assignment row) is saved first; step 2 (Open -> InProgress + the queue state) then fails. The old code had already committed step 1.
        await WithFaultAsync("trg_Fault_LinkUpdate", "CREATE TRIGGER trg_Fault_LinkUpdate ON QueueingTicketLinks AFTER UPDATE AS BEGIN THROW 50001, 'forced failure', 1; END", async () =>
        {
            var results = await FireAsync(Enumerable.Range(0, InstanceCount).Select(i => (assign, i)).ToArray());
            Assert.All(results, r => Assert.True(r.Http.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.ServiceUnavailable, r.Http.StatusCode.ToString()));
        });

        var untouched = (await f.FindTicketAsync(id))!;
        Assert.Null(untouched.CurrentOwnerEmployeeId);                                  // no owner
        Assert.Equal(TicketStatus.Open, untouched.TicketStatus);                        // not InProgress
        Assert.Equal(ticket.RowVersion, untouched.RowVersion);                          // the ticket row was not written at all
        Assert.Equal(0, await CountAsync(db => db.TicketAssignments.Where(a => a.TicketId == ticket.TicketId)));
        var link = (await f.FindLinkAsync(ticket.TicketId))!;
        Assert.Equal(1, link.LastAppliedSequence);
        Assert.Equal(QueueingTicketState.Waiting, link.State);
        Assert.Equal(before, await SnapshotAsync());                                    // no history, audit, outbox or assignment rows
        Assert.Equal(QueueingEventStatus.Unresolved, (await f.FindEventAsync(assign.EventId!))!.Status);

        await FireAsync(Enumerable.Range(0, InstanceCount).Select(i => (assign, i)).ToArray());
        await SettleAsync(id);

        var done = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeA, done.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.InProgress, done.TicketStatus);
        Assert.Equal(1, await CountAsync(db => db.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent)));
        Assert.Equal(1, await CountAsync(db => db.TicketAssignments.Where(a => a.TicketId == ticket.TicketId)));
        Assert.Equal(2, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);
        Assert.Equal(1, await CountAsync(db => db.QueueingEvents.Where(e => e.EventId == assign.EventId && e.Status == QueueingEventStatus.Processed)));
        await AssertNoCustomerEmailAsync(id);
    }

    [VisibilitySqlServerFact]
    public async Task AReturnAndAClosureThatFailAfterTheirTicketWrites_RollBackEverything_AndRecoverOnRetry()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id), _clients[0]);
        await f.PostAsync(f.Assigned(id, 2), _clients[1]);
        var ticket = (await f.FindTicketAsync(id))!;
        var before = await SnapshotAsync();
        var returned = Simple("TicketReturned", id, 3, reason: "stepped away");

        await WithFaultAsync("trg_Fault_LinkUpdate", "CREATE TRIGGER trg_Fault_LinkUpdate ON QueueingTicketLinks AFTER UPDATE AS BEGIN THROW 50001, 'forced failure', 1; END", async () =>
        {
            await FireAsync(Enumerable.Range(0, InstanceCount).Select(i => (returned, i)).ToArray());
        });

        var still = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeA, still.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.InProgress, still.TicketStatus);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(2, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);

        await FireAsync(Enumerable.Range(0, InstanceCount).Select(i => (returned, i)).ToArray());
        await SettleAsync(id);
        var open = (await f.FindTicketAsync(id))!;
        Assert.Null(open.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.Open, open.TicketStatus);
        Assert.Equal(1, await CountAsync(db => db.AuditEntries.Where(a => a.Action == "ReturnToQueue" && a.EntityId == ticket.TicketId.ToString())));

        // The closure (a different transaction shape: resolution row, SLA settlement, superseded assignment).
        await f.PostAsync(f.Assigned(id, 4), _clients[2]);
        var closed = Simple("TicketClosed", id, 5);
        var beforeClose = await SnapshotAsync();
        await WithFaultAsync("trg_Fault_LinkUpdate", "CREATE TRIGGER trg_Fault_LinkUpdate ON QueueingTicketLinks AFTER UPDATE AS BEGIN THROW 50001, 'forced failure', 1; END", async () =>
        {
            await FireAsync(Enumerable.Range(0, InstanceCount).Select(i => (closed, i)).ToArray());
        });
        Assert.Equal(TicketStatus.InProgress, (await f.FindTicketAsync(id))!.TicketStatus);
        Assert.Equal(0, await CountAsync(db => db.TicketResolutions.Where(r => r.TicketId == ticket.TicketId)));
        Assert.Equal(beforeClose, await SnapshotAsync());

        await FireAsync(Enumerable.Range(0, InstanceCount).Select(i => (closed, i)).ToArray());
        await SettleAsync(id);
        Assert.Equal(TicketStatus.Closed, (await f.FindTicketAsync(id))!.TicketStatus);
        Assert.Equal(1, await CountAsync(db => db.TicketResolutions.Where(r => r.TicketId == ticket.TicketId)));
        Assert.Equal(0, await CountAsync(db => db.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent)));
        await AssertNoCustomerEmailAsync(id);
    }

    [VisibilitySqlServerFact]
    public async Task ACommentAndARecallThatFail_LeaveNoNoteOrHistory_AndRecoverOnRetry()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id), _clients[0]);
        var ticket = (await f.FindTicketAsync(id))!;
        var before = await SnapshotAsync();
        var comment = Simple("TicketCommentAdded", id, 2, comment: "needs a form");
        var recall = Simple("TicketRecalled", id, 3);

        await WithFaultAsync("trg_Fault_Audit", "CREATE TRIGGER trg_Fault_Audit ON AuditEntries AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE Action IN (N'QueueingCommentAdded', N'QueueingTicketRecalled')) THROW 50002, 'forced failure', 1; END", async () =>
        {
            await FireAsync((comment, 0), (comment, 1), (recall, 2), (recall, 3));
        });
        Assert.Equal(before, await SnapshotAsync());                                   // no note, no audit row

        await FireAsync((comment, 0), (comment, 1), (recall, 2), (recall, 3));
        await SettleAsync(id);
        Assert.Equal(before.Notes + 1, await CountAsync(db => db.TicketNotes));        // exactly one note, however many deliveries
        Assert.Equal(1, await CountAsync(db => db.AuditEntries.Where(a => a.Action == "QueueingTicketRecalled" && a.EntityId == ticket.TicketId.ToString())));
        await AssertNoCustomerEmailAsync(id);
    }

    // ------------------------------------------------------------------ state events: concurrency, order, duplicates

    [VisibilitySqlServerFact]
    public async Task ConcurrentAssignReturnAndClose_FromIndependentInstances_EndInAValidState_WithNoLostUpdate_AndTheSequenceRulesRespected()
    {
        // Scenario A: the newest event is an assignment.
        var a = NewTicketId();
        await f.PostAsync(Created(a), _clients[0]);
        await FireAsync(
            (f.Assigned(a, 2, f.QueueUserA), 0), (f.Assigned(a, 3, f.QueueUserB, type: "TicketReassigned"), 1), (Simple("TicketReturned", a, 4), 2),
            (f.Assigned(a, 5, f.QueueUserA, type: "TicketReassigned"), 3), (Simple("TicketCommentAdded", a, 6, comment: "note"), 0));
        await SettleAsync(a);
        var ticketA = (await f.FindTicketAsync(a))!;
        Assert.Equal(f.EmployeeA, ticketA.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.InProgress, ticketA.TicketStatus);
        Assert.Equal(5, (await f.FindLinkAsync(ticketA.TicketId))!.LastAppliedSequence);
        await AssertConsistentOwnershipAsync(ticketA.TicketId);

        // Scenario B: the newest state event is a return.
        var b = NewTicketId();
        await f.PostAsync(Created(b), _clients[0]);
        await FireAsync(
            (f.Assigned(b, 2, f.QueueUserA), 3), (f.Assigned(b, 3, f.QueueUserB, type: "TicketReassigned"), 2), (Simple("TicketReturned", b, 4), 1), (Simple("TicketRecalled", b, 5), 0));
        await SettleAsync(b);
        var ticketB = (await f.FindTicketAsync(b))!;
        Assert.Null(ticketB.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.Open, ticketB.TicketStatus);
        Assert.Equal(4, (await f.FindLinkAsync(ticketB.TicketId))!.LastAppliedSequence);
        await AssertConsistentOwnershipAsync(ticketB.TicketId);

        // Scenario C: the newest is the closure - older events that lose the race are superseded, never applied over it.
        var c = NewTicketId();
        await f.PostAsync(Created(c), _clients[0]);
        var events = new[]
        {
            f.Assigned(c, 2, f.QueueUserA), f.Assigned(c, 3, f.QueueUserB, type: "TicketReassigned"), Simple("TicketReturned", c, 4),
            f.Assigned(c, 5, f.QueueUserA), Simple("TicketClosed", c, 6)
        };
        await FireAsync(events.Select((e, i) => (e, i)).ToArray());
        await SettleAsync(c);
        var ticketC = (await f.FindTicketAsync(c))!;
        Assert.Equal(TicketStatus.Closed, ticketC.TicketStatus);
        Assert.Null(ticketC.CurrentOwnerEmployeeId);
        var linkC = (await f.FindLinkAsync(ticketC.TicketId))!;
        Assert.Equal(6, linkC.LastAppliedSequence);
        Assert.Equal(QueueingTicketState.Closed, linkC.State);
        Assert.Equal(1, await CountAsync(db => db.TicketResolutions.Where(r => r.TicketId == ticketC.TicketId)));
        Assert.Equal(0, await CountAsync(db => db.TicketAssignments.Where(x => x.TicketId == ticketC.TicketId && x.IsCurrent)));
        var statuses = await f.ReadAsync(db => db.QueueingEvents.AsNoTracking().Where(e => e.ExternalTicketId == c).Select(e => e.Status).ToListAsync());
        Assert.All(statuses, s => Assert.True(s is QueueingEventStatus.Processed or QueueingEventStatus.Superseded or QueueingEventStatus.Rejected, s.ToString()));

        await AssertNoCustomerEmailAsync(a, b, c);
    }

    private async Task AssertConsistentOwnershipAsync(long ticketId)
    {
        var ticket = await f.ReadAsync(db => db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId));
        var current = await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().Where(x => x.TicketId == ticketId && x.IsCurrent).ToListAsync());
        Assert.True(current.Count <= 1, $"{current.Count} current assignments");
        Assert.Equal(ticket.CurrentOwnerEmployeeId, current.SingleOrDefault()?.AssignedEmployeeId);       // the ticket and its assignment row never disagree
        Assert.Equal(ticket.CurrentOwnerEmployeeId is null ? TicketStatus.Open : TicketStatus.InProgress, ticket.TicketStatus);
    }

    [VisibilitySqlServerFact]
    public async Task OutOfOrderEvents_WaitForTheirTicket_StaleOnesAreSuperseded_AndDuplicatesChangeNothing()
    {
        var id = NewTicketId();
        var assign = f.Assigned(id, 2, f.QueueUserA);
        var reassign = f.Assigned(id, 3, f.QueueUserB, type: "TicketReassigned");

        // The assignment events arrive BEFORE the ticket exists, from several instances (and twice): kept pending, nothing guessed.
        var early = await FireAsync((assign, 0), (assign, 1), (reassign, 2), (reassign, 3));
        Assert.All(early, r => Assert.Equal(HttpStatusCode.Accepted, r.Http.StatusCode));
        Assert.All(early, r => Assert.Equal(QueueingOutcomeCodes.AwaitingTicket, r.Body!.Outcome));
        Assert.Equal(0, await f.CountTicketsAsync(id));
        Assert.Equal(2, await CountAsync(db => db.QueueingEvents.Where(e => e.ExternalTicketId == id && e.Status == QueueingEventStatus.Unresolved)));

        // TicketCreated arrives concurrently with yet more duplicates: the waiting events are applied after it, newest state wins.
        var created = Created(id);
        await FireAsync((created, 0), (assign, 1), (created, 2), (reassign, 3));
        await SettleAsync(id);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(1, await f.CountTicketsAsync(id));
        Assert.Equal(f.EmployeeB, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Equal(3, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);
        Assert.Equal(1, await CountAsync(db => db.QueueingEvents.Where(e => e.EventId == assign.EventId)));
        await AssertConsistentOwnershipAsync(ticket.TicketId);

        // A stale event (lower sequence than the applied state) arrives late: superseded, and it changes nothing - redelivered it is a plain duplicate.
        var stale = f.Assigned(id, 2, f.QueueUserA);
        var (http, body) = await f.PostAsync(stale, _clients[2]);
        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        Assert.Equal(QueueingResultStatus.Superseded, body!.Status);
        Assert.Equal(QueueingOutcomeCodes.StaleEvent, body.Outcome);
        Assert.Equal(f.EmployeeB, (await f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);
        var (_, again) = await f.PostAsync(stale, _clients[3]);
        Assert.Equal(QueueingResultStatus.Duplicate, again!.Status);
        Assert.Equal(1, await CountAsync(db => db.TicketAssignments.Where(x => x.TicketId == ticket.TicketId && x.IsCurrent)));
        // A (superseded) then B when the two waiting events were applied in order, or just B when the newer one won the race and the older became stale - never more, however many duplicates.
        Assert.InRange(await CountAsync(db => db.TicketAssignments.Where(x => x.TicketId == ticket.TicketId)), 1, 2);

        await AssertNoCustomerEmailAsync(id);
    }

    // ------------------------------------------------------------------ retention on the real schema

    [VisibilitySqlServerFact]
    public async Task Retention_ErasesSettledPayloads_OfEveryKind_ThroughTheRealJob_KeepsTheirIdentityAndHash_AndNeverTouchesAPendingEvent()
    {
        var id = NewTicketId();
        var created = Created(id);
        var reassigned = f.Assigned(id, 4, f.QueueUserB, type: "TicketReassigned");
        var stale = f.Assigned(id, 3, f.QueueUserA);                                       // superseded once 4 is applied
        var closed = Simple("TicketClosed", id, 5);
        var afterClose = f.Assigned(id, 6, f.QueueUserA);                                  // rejected: the ticket is closed
        await f.PostAsync(created, _clients[0]);
        await f.PostAsync(reassigned, _clients[0]);
        await f.PostAsync(stale, _clients[1]);
        await f.PostAsync(closed, _clients[2]);
        await f.PostAsync(afterClose, _clients[3]);
        var waiting = f.Assigned(NewTicketId(), 2, f.QueueUserA);                           // no ticket yet: pending
        await f.PostAsync(waiting, _clients[0]);
        var failedAfterAttempts = f.Assigned(id + "-x", 2, "queue-user-never-mapped-retention-sql");
        await f.PostAsync(failedAfterAttempts, _clients[0]);

        var settled = new[] { created, reassigned, stale, closed, afterClose };
        var statuses = await Task.WhenAll(settled.Select(async e => (await f.FindEventAsync(e.EventId!))!.Status));
        Assert.Equal([QueueingEventStatus.Processed, QueueingEventStatus.Processed, QueueingEventStatus.Superseded, QueueingEventStatus.Processed, QueueingEventStatus.Rejected], statuses);

        // Age everything past the retention window (the pending ones too: they must still survive).
        await f.ExecuteSqlAsync("UPDATE QueueingEvents SET ProcessedAtUtc = DATEADD(day, -91, ProcessedAtUtc), ReceivedAtUtc = DATEADD(day, -91, ReceivedAtUtc) WHERE ExternalTicketId LIKE N'qt-%'");
        var snapshots = new Dictionary<string, QueueingEvent>();
        foreach (var e in settled.Append(waiting).Append(failedAfterAttempts))
        {
            snapshots[e.EventId!] = (await f.FindEventAsync(e.EventId!))!;
        }

        using (var scope = _instances[0].Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<QueueingPayloadRetentionJob>().RunAsync(CancellationToken.None);
        }

        foreach (var e in settled)
        {
            var before = snapshots[e.EventId!];
            var after = (await f.FindEventAsync(e.EventId!))!;
            Assert.Null(after.Payload);                                                    // customer name / phone / email are gone
            Assert.NotNull(after.PayloadPurgedAtUtc);
            Assert.Equal(before.PayloadHash, after.PayloadHash);                           // the hash is kept
            Assert.Equal(64, after.PayloadHash.Length);
            Assert.Equal((before.EventId, before.EventType, before.ExternalTicketId, before.EventSequence, before.OccurredAtUtc, before.Status, before.OutcomeCode, before.TicketId, before.ReceivedAtUtc, before.ProcessedAtUtc),
                (after.EventId, after.EventType, after.ExternalTicketId, after.EventSequence, after.OccurredAtUtc, after.Status, after.OutcomeCode, after.TicketId, after.ReceivedAtUtc, after.ProcessedAtUtc));
        }

        var stillPending = (await f.FindEventAsync(waiting.EventId!))!;
        Assert.Equal(QueueingEventStatus.Unresolved, stillPending.Status);
        Assert.NotNull(stillPending.Payload);                                               // a pending event keeps the payload it needs for its retry
        Assert.Null(stillPending.PayloadPurgedAtUtc);

        // Idempotency survives the purge on the real database: identical content is a Duplicate, different content under the same EventId is EVENT_ID_REUSED.
        foreach (var e in settled)
        {
            var (same, sameBody) = await f.PostAsync(e, _clients[1]);
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal(QueueingResultStatus.Duplicate, sameBody!.Status);

            var (changed, changedBody) = await f.PostAsync(e with { Comment = "changed after the purge" }, _clients[2]);
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal("EVENT_ID_REUSED", changedBody!.Outcome);
        }

        Assert.Equal(1, await f.CountTicketsAsync(id));
        var closedTicket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.Closed, closedTicket.TicketStatus);                             // the ticket history is untouched
        Assert.Equal(1, await CountAsync(db => db.TicketResolutions.Where(r => r.TicketId == closedTicket.TicketId)));
    }
}
