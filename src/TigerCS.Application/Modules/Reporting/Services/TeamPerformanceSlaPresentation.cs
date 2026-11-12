using System.Globalization;
using TigerCS.Application.Modules.Reporting.Abstractions;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.Reporting.Services;

/// <summary>
/// Turns the facts the SLA engine recorded into what a manager reads on the
/// SLA details view: status, readable durations, the applied policy in
/// words, and an explanation of why each evaluation is counted as it is.
///
/// <para>
/// Nothing here decides an SLA outcome. The status of an evaluation comes
/// from the repository (the engine's own recorded breach, or the on-time
/// action, or a still-open clock); this class only measures how far past the
/// due moment a recorded breach is and describes it. The rule texts restate
/// <c>docs/architecture/SLA-Architecture.md</c> and the code that implements
/// it, with the live configuration (priority policies, business calendar)
/// read from the database rather than copied.
/// </para>
/// </summary>
internal static class TeamPerformanceSlaPresentation
{
    private const string FirstResponseLabel = "First Response";
    private const string ResolutionLabel = "Resolution";
    private const string BreachedStateLabel = "Breached state recorded";

    public static TeamPerformanceSlaRecordDto ToRecord(
        TeamPerformanceSlaEvaluationRow row, TeamPerformanceEmployeeRow employee, DateTime nowUtc)
    {
        var status = row.Kind switch
        {
            TeamPerformanceSlaEvaluationKind.Breached => TeamPerformanceSlaStatuses.Breached,
            TeamPerformanceSlaEvaluationKind.Met => TeamPerformanceSlaStatuses.Met,
            _ => TeamPerformanceSlaStatuses.Pending
        };

        var slaType = row.SlaType switch
        {
            SlaDeadlineType.FirstResponse => FirstResponseLabel,
            SlaDeadlineType.Resolution => ResolutionLabel,
            _ => BreachedStateLabel
        };

        int? breachMinutes = null;
        string? breachText = null;
        var ongoing = false;
        if (row.Kind == TeamPerformanceSlaEvaluationKind.Breached && row.DueAtUtc is { } due)
        {
            ongoing = row.ActualAtUtc is null;
            var end = row.ActualAtUtc ?? nowUtc;
            breachMinutes = Math.Max(0, (int)Math.Floor((end - due).TotalMinutes));
            breachText = FormatMinutes(breachMinutes.Value) + (ongoing ? " (ongoing)" : string.Empty);
        }

        return new TeamPerformanceSlaRecordDto(
            row.TicketId,
            row.TicketNumber,
            row.DepartmentName,
            slaType,
            row.TicketCreatedAtUtc,
            row.SlaStartedAtUtc,
            row.TargetMinutes,
            TargetText(row.TargetMinutes, row.ClockBasis),
            row.DueAtUtc,
            row.ActualAtUtc,
            status,
            row.EventAtUtc,
            breachMinutes,
            breachText,
            ongoing,
            employee.EmployeeId,
            employee.DisplayName,
            PolicyText(row),
            Explain(row, employee, slaType, nowUtc),
            row.AdditionalBreachNotes.Count == 0
                ? null
                : $"Also recorded on this ticket in the period (not counted again): {string.Join("; ", row.AdditionalBreachNotes)}");
    }

    // ---------------------------------------------------------------
    // Words
    // ---------------------------------------------------------------

    /// <summary>"2 d 3 h 20 m" — whole days, hours and minutes, omitting leading zero parts; "0 m" for nothing.</summary>
    public static string FormatMinutes(int minutes)
    {
        if (minutes <= 0)
        {
            return "0 m";
        }

        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var mins = minutes % 60;
        var parts = new List<string>(3);
        if (days > 0) parts.Add($"{days} d");
        if (hours > 0) parts.Add($"{hours} h");
        if (mins > 0) parts.Add($"{mins} m");
        return string.Join(' ', parts);
    }

    public static string BasisLabel(SlaClockBasis? basis) => basis switch
    {
        SlaClockBasis.TwentyFourSeven => "24/7 calendar time",
        SlaClockBasis.BusinessHours => "Business hours",
        _ => "Basis not recorded"
    };

    private static string TargetText(int? minutes, SlaClockBasis? basis) =>
        minutes is null
            ? "Not recorded"
            : minutes == 0
                ? $"Immediate · {BasisLabel(basis)}"
                : $"{FormatMinutes(minutes.Value)} · {BasisLabel(basis)}";

    private static string PolicyText(TeamPerformanceSlaEvaluationRow row)
    {
        if (row.PriorityId is null)
        {
            return "No SLA period found for this ticket";
        }

        var source = row.RequestTypeSlaPolicyId is { } id
            ? $"request-type SLA #{id}{(string.IsNullOrWhiteSpace(row.RequestTypeName) ? string.Empty : $" ({row.RequestTypeName})")}"
            : "priority SLA policy";

        var text = $"{row.PriorityName ?? $"Priority {row.PriorityId}"} priority · {source}";
        if (row.SlaType is not null)
        {
            text += $" · {TargetText(row.TargetMinutes, row.ClockBasis)}";
        }

        if (row.PausedMinutes > 0)
        {
            text += $" · {FormatMinutes(row.PausedMinutes)} paused (already added to the due time)";
        }

        if (!string.IsNullOrWhiteSpace(row.RequestTypeSlaNote))
        {
            text += $" · request-type SLA not applied: {row.RequestTypeSlaNote}";
        }

        return text;
    }

    private static string Utc(DateTime? value) =>
        value is { } v ? v.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "—";

    private static string Explain(TeamPerformanceSlaEvaluationRow row, TeamPerformanceEmployeeRow employee, string slaType, DateTime nowUtc)
    {
        var action = row.SlaType == SlaDeadlineType.FirstResponse ? "first human response" : "resolution";

        switch (row.Kind)
        {
            case TeamPerformanceSlaEvaluationKind.Breached when row.SlaType is null:
                return $"Counted as a breach because the ticket's SLA state was recorded as Breached at {Utc(row.EventAtUtc)} "
                    + $"(\"{row.BreachNote}\"). {employee.DisplayName} held the ticket at that moment (latest assignment at or before it).";

            case TeamPerformanceSlaEvaluationKind.Breached:
                var outcome = row.ActualAtUtc is { } actual
                    ? $"The {action} was recorded at {Utc(actual)}, after the due time."
                    : $"No {action} has been recorded yet.";
                return $"The SLA engine recorded this {slaType} breach at {Utc(row.EventAtUtc)} (due {Utc(row.DueAtUtc)}). {outcome} "
                    + $"{employee.DisplayName} held the ticket at the moment of the breach (latest assignment at or before it).";

            case TeamPerformanceSlaEvaluationKind.Met:
                return $"The {action} was recorded at {Utc(row.ActualAtUtc)}, on or before the due time {Utc(row.DueAtUtc)}, with no breach recorded. "
                    + $"{employee.DisplayName} held the ticket at that moment.";

            default:
                var pending = $"Clock still running: no {action} recorded yet. Due {Utc(row.DueAtUtc)}; {employee.DisplayName} holds the ticket now.";
                if (row.ClockPausedNow)
                {
                    pending += " The Resolution clock is paused (Pending Customer) and will be extended on resume.";
                }
                else if (row.DueAtUtc is { } dueAt && dueAt <= nowUtc)
                {
                    pending += " The due time has passed; the breach is recorded by the SLA engine's next check.";
                }

                return pending;
        }
    }

    // ---------------------------------------------------------------
    // Sorting
    // ---------------------------------------------------------------

    /// <summary>
    /// Sorts by one of <see cref="TeamPerformanceSlaSortKeys.All"/>. Missing
    /// values sort last in either direction; ties break on ticket (newest
    /// first), then SLA type, so paging is stable.
    /// </summary>
    public static List<TeamPerformanceSlaRecordDto> Sort(IReadOnlyList<TeamPerformanceSlaRecordDto> records, string sortBy, bool descending)
    {
        IOrderedEnumerable<TeamPerformanceSlaRecordDto> Primary<TKey>(Func<TeamPerformanceSlaRecordDto, TKey?> key) where TKey : struct, IComparable<TKey> =>
            descending
                ? records.OrderBy(r => key(r) is null).ThenByDescending(r => key(r))
                : records.OrderBy(r => key(r) is null).ThenBy(r => key(r));

        IOrderedEnumerable<TeamPerformanceSlaRecordDto> PrimaryText(Func<TeamPerformanceSlaRecordDto, string?> key) =>
            descending
                ? records.OrderBy(r => string.IsNullOrEmpty(key(r))).ThenByDescending(r => key(r) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : records.OrderBy(r => string.IsNullOrEmpty(key(r))).ThenBy(r => key(r) ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var ordered = sortBy switch
        {
            TeamPerformanceSlaSortKeys.Ticket => PrimaryText(r => r.TicketNumber),
            TeamPerformanceSlaSortKeys.Department => PrimaryText(r => r.DepartmentName),
            TeamPerformanceSlaSortKeys.SlaType => PrimaryText(r => r.SlaType),
            TeamPerformanceSlaSortKeys.Created => Primary<DateTime>(r => r.TicketCreatedAtUtc),
            TeamPerformanceSlaSortKeys.Started => Primary<DateTime>(r => r.SlaStartedAtUtc),
            TeamPerformanceSlaSortKeys.Target => Primary<int>(r => r.TargetMinutes),
            TeamPerformanceSlaSortKeys.Due => Primary<DateTime>(r => r.DueAtUtc),
            TeamPerformanceSlaSortKeys.Actual => Primary<DateTime>(r => r.ActualAtUtc),
            TeamPerformanceSlaSortKeys.Status => Primary<int>(r => StatusOrder(r.Status)),
            TeamPerformanceSlaSortKeys.Breach => Primary<int>(r => r.BreachMinutes),
            TeamPerformanceSlaSortKeys.Policy => PrimaryText(r => r.Policy),
            _ => Primary<DateTime>(r => r.EventAtUtc ?? r.DueAtUtc)
        };

        return ordered.ThenByDescending(r => r.TicketId).ThenBy(r => r.SlaType, StringComparer.Ordinal).ToList();
    }

    private static int StatusOrder(string status) => status switch
    {
        TeamPerformanceSlaStatuses.Breached => 0,
        TeamPerformanceSlaStatuses.Pending => 1,
        _ => 2
    };

    // ---------------------------------------------------------------
    // Rules
    // ---------------------------------------------------------------

    public static TeamPerformanceSlaRulesDto BuildRules(TeamPerformanceSlaConfiguration configuration)
    {
        var calendar = configuration.Calendar;
        var businessHours = calendar is null
            ? "No active business calendar is configured, so a Business-hours policy cannot currently be computed."
            : $"Business-hours clocks count only time inside {calendar.StartLocal:HH\\:mm}–{calendar.EndLocal:HH\\:mm} ({calendar.TimeZone}) on working days "
              + $"({string.Join(", ", calendar.WorkingDays)}); every other day — weekends and the {calendar.HolidayCount} configured holiday(s) — is skipped.";

        var rules = new List<TeamPerformanceSlaRuleDto>
        {
            new("What is evaluated",
                "Every classified ticket carries two independent SLA clocks: First Response and Resolution. Each clock is one evaluation: Met (the action happened in time), "
                + "Breached (it was late, or the due time passed without it) or Pending (still running)."),
            new("When the timer starts",
                "At ticket creation. The clock is never restarted by a later classification: when the request type is classified the governing policy is re-applied to the running period, "
                + "measured from the original start. A ticket created without a priority starts its SLA when it is classified. A request-type SLA whose trigger is not \"ticket created\" "
                + "is not enforced; the priority policy applies instead and the SLA Policy column says so."),
            new("When the timer stops",
                "First Response stops at the first human response. Resolution stops when the ticket is Resolved (the resolution time) — closing the ticket is not the stop event."),
            new("What counts as a first response",
                "Only a human one: the answer timestamp of an inbound Genesys call, the first human-agent message of a conversation, or an agent explicitly recording it. "
                + "The automated acknowledgement email and AI / virtual-agent replies never count."),
            new("Business hours, weekends and holidays",
                "The clock basis is configured per priority (table below). 24/7 uses plain calendar time — no business window, weekend or holiday is excluded. "
                + "Business hours counts only time inside the working window. " + businessHours),
            new("Pausing",
                "First Response never pauses. Resolution pauses while a non-Critical ticket is Pending Customer (or the retired Pending Third Party) and resumes when work resumes; "
                + "the due time is moved later by the paused time, so the Due column already includes it. Critical never pauses. A request type may set its own Pending Customer flag."),
            new("Reassignment",
                "Reassigning or transferring a ticket does not restart, pause or change either clock or its due time. It only changes who is accountable for the outcome."),
            new("Reopened tickets",
                "Reopening ends the current SLA period and starts a new Resolution cycle from the reopen moment with a freshly computed deadline. "
                + "First Response is carried over unchanged (same due time, same breach flag) and is never restarted or re-flagged. A priority upgrade or approved downgrade opens a new period the same way. "
                + "Earlier periods and their results are kept."),
            new("How a breach is recorded",
                "When a clock's due time passes without the action, or the action lands after the due time, the SLA engine marks that clock Breached once (it cannot be undone), "
                + "writes an SLA-state history entry and raises the automatic Level 2 escalation. An action recorded exactly at the due time is on time."),
            new("How breaches are attributed to employees",
                "To the employee who held the ticket at the moment the breach was recorded: the latest assignment at or before it. The current owner is irrelevant, "
                + "and a breach recorded while nobody held the ticket belongs to nobody. A Met evaluation belongs to whoever held the ticket when the action was recorded; "
                + "a Pending one to the employee who holds the ticket now."),
            new("How the report counts",
                "SLA Breaches is the number of distinct tickets with at least one breach entry recorded in the period and attributed to the employee — so a ticket whose two clocks both "
                + "breached is one Breached record here (the other entry is shown on its row). Met and Pending are listed beside it for context: Met = on-time actions recorded in the period; "
                + "Pending = clocks still open on tickets the employee holds now (Closed tickets excluded)."),
            new("Period",
                "UTC calendar days, inclusive, exactly as on the report. Timestamps on this view are UTC.")
        };

        var policies = configuration.Policies
            .Select(p => new TeamPerformanceSlaPolicyDto(
                p.PriorityName, p.FirstResponseTargetMinutes, p.ResolutionTargetMinutes, BasisLabel(p.ClockBasis), p.IsActive))
            .ToList();

        var calendarDto = calendar is null
            ? null
            : new TeamPerformanceSlaCalendarDto(
                calendar.Name,
                calendar.TimeZone,
                calendar.StartLocal.ToString("HH:mm", CultureInfo.InvariantCulture),
                calendar.EndLocal.ToString("HH:mm", CultureInfo.InvariantCulture),
                calendar.WorkingDays.Select(d => d.ToString()).ToList(),
                calendar.HolidayCount);

        return new TeamPerformanceSlaRulesDto(rules, policies, calendarDto);
    }
}
