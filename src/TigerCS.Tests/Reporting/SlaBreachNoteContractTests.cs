using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
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
}
