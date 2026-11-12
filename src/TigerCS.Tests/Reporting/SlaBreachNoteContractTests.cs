using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Reporting;

/// <summary>
/// The contract between the SLA engine and the Team Performance report. The report recognises a genuine SLA breach by the note the
/// engine writes on the SlaState history row; this pins the exact prefixes AND runs the real processor and the real first-response
/// recorder to prove what each actually writes — so a change to either note breaks a test instead of silently changing the numbers.
/// </summary>
public sealed class SlaBreachNoteContractTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheTwoBreachNotePrefixes_ArePinnedExactly()
    {
        Assert.Equal("FirstResponse SLA breached", SlaBreachNotes.FirstResponsePrefix);
        Assert.Equal("Resolution SLA breached", SlaBreachNotes.ResolutionPrefix);

        var due = new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc);
        Assert.Equal($"FirstResponse SLA breached (due {due:O}).", SlaBreachNotes.For(SlaDeadlineType.FirstResponse, due));
        Assert.Equal($"Resolution SLA breached (due {due:O}).", SlaBreachNotes.For(SlaDeadlineType.Resolution, due));

        Assert.Equal(SlaDeadlineType.FirstResponse, SlaBreachNotes.TypeOf("FirstResponse SLA breached (due 2026-09-01T08:30:00.0000000Z)."));
        Assert.Equal(SlaDeadlineType.Resolution, SlaBreachNotes.TypeOf("Resolution SLA breached (due 2026-09-01T08:30:00.0000000Z)."));

        // Rows that carry the Breached state without being a breach are not breaches.
        Assert.Null(SlaBreachNotes.TypeOf("First human response recorded (Manual) at 2026-09-01T09:00:00.0000000Z; entered at 2026-09-01T09:00:00.0000000Z."));
        Assert.Null(SlaBreachNotes.TypeOf("New Resolution SLA cycle opened on reopen."));
        Assert.Null(SlaBreachNotes.TypeOf("SLA clock started at classification (2026-09-01T09:00:00.0000000Z)."));
        Assert.Null(SlaBreachNotes.TypeOf(null));
        Assert.Null(SlaBreachNotes.TypeOf(string.Empty));
    }

    [Fact]
    public async Task TheRealProcessorWritesAGenuineBreachNote_AndTheRealRecorderWritesANonBreachNote_OnTheSameTicket()
    {
        var tickets = new FakeTicketRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var sla = new SlaServiceFixture(
            tickets, new FakeTicketResolutionRepository(), statusHistory, new FakeUserDepartmentAssignmentRepository(),
            new FakeAuditEntryWriter(), new FakeTicketingUnitOfWork());

        var ticket = Ticket.CreateUnverified("TG-TST-1", departmentId: 1, categoryId: 1, (byte)PriorityLevel.Medium, "Water leak", Now.AddDays(-10));
        var firstResponseDue = Now.AddDays(-9);
        var resolutionDue = Now.AddDays(-2);
        await sla.SlaInstances.AddAsync(
            TicketSlaInstance.OpenInitialPeriod(ticket.TicketId, (byte)PriorityLevel.Medium, ticket.CreatedAtUtc, firstResponseDue, resolutionDue));

        // 1. The scheduler records the First Response breach at the deadline.
        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded,
            await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.FirstResponse, firstResponseDue, Guid.NewGuid()));
        // 2. The Resolution clock breaches too: Breached -> Breached, with its own note.
        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded,
            await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.Resolution, resolutionDue, Guid.NewGuid()));
        // 3. A (late) first response is entered afterwards, on a ticket that is already Breached.
        Assert.True(await sla.FirstHumanResponse.TryRecordAsync(
            ticket, Now.AddDays(-1), Now, Guid.NewGuid(), actorIsSystem: false, FirstResponseSource.Manual, Guid.NewGuid()));

        var rows = statusHistory.Added.Where(h => h.Dimension == TicketStatusDimension.SlaState).ToList();

        var firstResponseBreach = Assert.Single(rows, h => SlaBreachNotes.TypeOf(h.Note) == SlaDeadlineType.FirstResponse);
        Assert.StartsWith("FirstResponse SLA breached (due ", firstResponseBreach.Note);
        Assert.Equal((byte)SlaState.Breached, firstResponseBreach.NewValue);

        var resolutionBreach = Assert.Single(rows, h => SlaBreachNotes.TypeOf(h.Note) == SlaDeadlineType.Resolution);
        Assert.StartsWith("Resolution SLA breached (due ", resolutionBreach.Note);
        // A genuine Breached -> Breached row (the second clock breaching) is a real event and carries a breach note.
        Assert.Equal((byte)SlaState.Breached, resolutionBreach.OldValue);
        Assert.Equal((byte)SlaState.Breached, resolutionBreach.NewValue);

        // The recorder's row ALSO has NewValue = Breached (the old report counted it) but is not a breach.
        var recorded = Assert.Single(rows, h => h.Note!.StartsWith("First human response recorded", StringComparison.Ordinal));
        Assert.Equal((byte)SlaState.Breached, recorded.NewValue);
        Assert.Null(SlaBreachNotes.TypeOf(recorded.Note));

        // Exactly two of the rows the report would see are genuine breaches.
        Assert.Equal(2, rows.Count(h => h.NewValue == (byte)SlaState.Breached && SlaBreachNotes.TypeOf(h.Note) is not null));
        Assert.Equal(3, rows.Count(h => h.NewValue == (byte)SlaState.Breached));
    }

    // ---------------------------------------------------------------
    // The engine records each breach once, however often it is asked
    // ---------------------------------------------------------------

    private static (SlaServiceFixture Sla, FakeTicketStatusHistoryRepository History, FakeAuditEntryWriter Audit, Ticket Ticket, TicketSlaInstance Period) CreateCriticalTicket(DateTime now)
    {
        var tickets = new FakeTicketRepository();
        var history = new FakeTicketStatusHistoryRepository();
        var audit = new FakeAuditEntryWriter();
        var sla = new SlaServiceFixture(
            tickets, new FakeTicketResolutionRepository(), history, new FakeUserDepartmentAssignmentRepository(), audit,
            new FakeTicketingUnitOfWork(), new FakeTimeProvider(now));

        // Critical is a 24/7 tier, so no business calendar is involved: deadlines are plain durations from the start.
        var ticket = Ticket.CreateUnverified("TG-TST-9", departmentId: 1, categoryId: 1, (byte)PriorityLevel.Critical, "Water leak", Now.AddDays(-10));
        tickets.AddAsync(ticket).GetAwaiter().GetResult();
        var period = sla.DueDates.OpenInitialPeriodAsync(ticket, ticket.CreatedAtUtc, actorEmployeeId: null, Guid.NewGuid()).GetAwaiter().GetResult();
        return (sla, history, audit, ticket, period);
    }

    private static List<TicketStatusHistory> BreachRows(FakeTicketStatusHistoryRepository history) =>
        history.Added.Where(h => h.Dimension == TicketStatusDimension.SlaState && h.NewValue == (byte)SlaState.Breached
            && SlaBreachNotes.TypeOf(h.Note) is not null).ToList();

    [Fact]
    public async Task RepeatedSchedulerRuns_AndTheOverlappingSafetySweep_RecordEachBreachExactlyOnce()
    {
        var (sla, history, audit, ticket, period) = CreateCriticalTicket(Now);

        // The scheduled job fires repeatedly for each clock, at the deadline and later; two workers; the safety sweep overlaps them.
        var detection = sla.CreateBreachDetection();
        var outcomes = new List<SlaBreachProcessingOutcome>
        {
            await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.FirstResponse, period.FirstResponseDueAtUtc.AddMinutes(1), Guid.NewGuid()),
            await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.FirstResponse, period.FirstResponseDueAtUtc.AddMinutes(2), Guid.NewGuid()),
            await detection.CheckDeadlineAsync(ticket.TicketId, SlaDeadlineType.FirstResponse),
            await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.Resolution, period.ResolutionDueAtUtc.AddMinutes(1), Guid.NewGuid()),
            await detection.CheckDeadlineAsync(ticket.TicketId, SlaDeadlineType.Resolution),
            await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.Resolution, period.ResolutionDueAtUtc.AddHours(5), Guid.NewGuid())
        };
        await detection.SweepAsync();
        await detection.SweepAsync();

        // Exactly the first call per clock recorded; every other call was an already-processed no-op.
        Assert.Equal(2, outcomes.Count(o => o == SlaBreachProcessingOutcome.BreachRecorded));
        Assert.Equal(4, outcomes.Count(o => o == SlaBreachProcessingOutcome.AlreadyProcessed));

        var rows = BreachRows(history);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => SlaBreachNotes.TypeOf(r.Note) == SlaDeadlineType.FirstResponse);
        Assert.Single(rows, r => SlaBreachNotes.TypeOf(r.Note) == SlaDeadlineType.Resolution);
        Assert.Equal(2, audit.Written.Count(w => w.Action == "DetectSlaBreach"));
        Assert.Single(sla.Escalations.All);                     // one automatic Level 2 escalation, not one per run
    }

    [Fact]
    public async Task AReopenedTicket_EvaluatesItsNewResolutionCycleIndependently_AndNeverRestartsFirstResponse()
    {
        var (sla, history, _, ticket, original) = CreateCriticalTicket(Now);
        var originalFirstResponseDue = original.FirstResponseDueAtUtc;
        var originalResolutionDue = original.ResolutionDueAtUtc;

        // Both clocks breach in the original period.
        await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.FirstResponse, originalFirstResponseDue.AddMinutes(1), Guid.NewGuid());
        await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.Resolution, originalResolutionDue.AddMinutes(1), Guid.NewGuid());

        // Reopened: a new Resolution cycle starts at the reopen moment.
        var reopenedAt = originalResolutionDue.AddDays(1);
        var cycle = await sla.DueDates.StartReopenResolutionCycleAsync(ticket, reopenedAt, actorEmployeeId: null, Guid.NewGuid());
        Assert.NotNull(cycle);

        // First Response is carried verbatim — same deadline, same breach flag — never restarted, never re-armed.
        Assert.Equal(SlaChangeReason.Reopen, cycle!.ChangeReason);
        Assert.Equal(originalFirstResponseDue, cycle.FirstResponseDueAtUtc);
        Assert.True(cycle.FirstResponseBreached);
        // The Resolution clock IS restarted: its own start and its own, later, deadline; its breach flag starts clear.
        Assert.Equal(reopenedAt, cycle.PeriodStartAtUtc);
        Assert.True(cycle.ResolutionDueAtUtc > reopenedAt);
        Assert.False(cycle.ResolutionBreached);
        Assert.True(original.ResolutionBreached);               // the original cycle's result is untouched

        // Asked again after the reopen, First Response is a no-op; the new Resolution cycle breaches on its own.
        Assert.Equal(SlaBreachProcessingOutcome.AlreadyProcessed,
            await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.FirstResponse, reopenedAt.AddDays(30), Guid.NewGuid()));
        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded,
            await sla.BreachProcessor.ProcessDeadlineAsync(ticket, SlaDeadlineType.Resolution, cycle.ResolutionDueAtUtc.AddMinutes(1), Guid.NewGuid()));

        var rows = BreachRows(history);
        Assert.Single(rows, r => SlaBreachNotes.TypeOf(r.Note) == SlaDeadlineType.FirstResponse);
        var resolutionNotes = rows.Where(r => SlaBreachNotes.TypeOf(r.Note) == SlaDeadlineType.Resolution).Select(r => r.Note).ToList();
        Assert.Equal(2, resolutionNotes.Count);                  // one per cycle...
        Assert.Contains(SlaBreachNotes.For(SlaDeadlineType.Resolution, originalResolutionDue), resolutionNotes);
        Assert.Contains(SlaBreachNotes.For(SlaDeadlineType.Resolution, cycle.ResolutionDueAtUtc), resolutionNotes);   // ...each naming its own deadline
        Assert.NotEqual(resolutionNotes[0], resolutionNotes[1]);
    }
}

/// <summary>The assignment-end diagnostic ships to people who run it against Production: it must stay read-only and never offer a backfill.</summary>
public sealed class AssignmentEndTimesDiagnosticScriptTests
{
    private static string Script([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "Diagnose_TigerCS_AssignmentEndTimes_ReadOnly.sql")));

    [Fact]
    public void TheDiagnostic_NeverWrites_AndNeverBackfills()
    {
        var sql = System.Text.RegularExpressions.Regex.Replace(Script(), @"/\*.*?\*/|--[^\r\n]*", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(
            @"\b(INSERT\s|UPDATE\s|DELETE\s|MERGE\s|TRUNCATE\s|ALTER\s|DROP\s|CREATE\s|EXEC(UTE)?\s)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), sql);
        Assert.Contains("READ UNCOMMITTED", sql, StringComparison.Ordinal);
        Assert.Contains("SupersededAtUtc", sql, StringComparison.Ordinal);
        foreach (var cls in new[] { "CURRENT", "ENDED_STAMPED", "REPLACED_EXACT", "ENDED_ON_CLOSURE", "RELEASED_END_UNKNOWN" })
        {
            Assert.Contains($"'{cls}'", sql, StringComparison.Ordinal);
        }

        // The breach filter uses the same two prefixes the report does.
        Assert.Contains(TigerCS.Application.Modules.SlaAndEscalation.Services.SlaBreachNotes.FirstResponsePrefix + "%", sql, StringComparison.Ordinal);
        Assert.Contains(TigerCS.Application.Modules.SlaAndEscalation.Services.SlaBreachNotes.ResolutionPrefix + "%", sql, StringComparison.Ordinal);
    }
}
