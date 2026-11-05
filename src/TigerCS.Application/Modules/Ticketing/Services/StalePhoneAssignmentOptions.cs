namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Settings of the recurring STALE PHONE assignment process (config section <c>StalePhoneAssignment</c>) - separate from the one-time
/// <see cref="CsAgentBackfillOptions"/> backfill and from the Call Center reconciler, so it can be switched on, off or tuned on its own.
///
/// The approved rule it carries out: an Open / In Progress, unowned ticket whose live Genesys conversations are all PHONE calls, with no human need
/// and no recorded activity for more than <see cref="StaleAfterHours"/> hours, is treated as STALE_AI and dealt to the next CS Agent of the shared
/// rotation. It needs no Genesys end event, never writes an end timestamp, never closes the ticket and never moves it. It ONLY acts on such tickets:
/// every other unowned ticket is left to the normal routing, the reconciler and the backfill.
///
/// Off and dry-run by default. Going live needs BOTH <see cref="Enabled"/> = true AND <see cref="DryRun"/> = false, which requires approval.
/// </summary>
public sealed class StalePhoneAssignmentOptions
{
    public const string SectionName = "StalePhoneAssignment";

    /// <summary>The recurring process runs only when true. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>True (default): evaluate and LOG what would be assigned; write nothing.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Minutes between runs (clamped 1..240 by the host).</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Tickets examined per page (at most 20 pages per run).</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Most tickets WRITTEN per run, so a large historical catch-up is throttled.</summary>
    public int MaxAssignmentsPerRun { get; set; } = 50;

    /// <summary>Pause after each live assignment.</summary>
    public int DelayBetweenTicketsMs { get; set; } = 250;

    /// <summary>A just-created ticket is given time to finish its own routing.</summary>
    public int MinTicketAgeMinutes { get; set; } = 3;

    /// <summary>Hours without any recorded activity after which a live Phone conversation stops blocking assignment (approved: 6). Null / &lt;= 0 disables the process.</summary>
    public int? StaleAfterHours { get; set; } = CsAgentBackfillOptions.DefaultStalePhoneAiAfterHours;

    /// <summary>The same rule expressed as the backfill options the shared classifier and assignment service use. Only the Phone rule is on.</summary>
    public CsAgentBackfillOptions ToBackfillOptions() => new()
    {
        Enabled = true,                       // this process has its own gate (Enabled / DryRun); the backfill service itself never reads it
        DelayBetweenTicketsMs = Math.Max(0, DelayBetweenTicketsMs),
        MinTicketAgeMinutes = Math.Max(0, MinTicketAgeMinutes),
        IncludeAiEndedWithoutHuman = true,
        StaleAiAfterHours = null,             // the generic (all-channel) stale rule is not part of this process
        StalePhoneAiAfterHours = StaleAfterHours,
    };
}
