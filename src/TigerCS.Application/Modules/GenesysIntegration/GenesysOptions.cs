namespace TigerCS.Application.Modules.GenesysIntegration;

/// <summary>
/// The Genesys integration's own settings — a plain value in the Application
/// layer (the composition root binds configuration onto it, exactly as
/// <c>ReopenPolicy</c> and <c>OutboxDispatchPolicy</c> do, so this project
/// keeps depending on nothing but the Domain).
///
/// <para>
/// <b><see cref="Enabled"/> is the feature flag.</b> False — the default —
/// means every Genesys ingestion entry point answers "integration disabled"
/// and writes nothing; manual/Face-to-Face ticket creation, the New Ticket
/// wizard, and every existing flow behave exactly as they did before this
/// phase. Nothing in the normal ticketing path reads this flag, which is
/// what makes that guarantee structural rather than a promise.
/// </para>
///
/// <para>
/// <b>No Genesys credential or endpoint lives here.</b> Ticketing exposes
/// inbound endpoints only. Genesys Cloud authenticates with OAuth 2.0 client
/// credentials to TigerGroupWeb, which forwards to these endpoints as an
/// authenticated TigerCS service account through the system's existing JWT
/// authentication. TigerCS never calls Genesys' own API, so none of it (base
/// URL, OAuth client) is configured here.
/// </para>
/// </summary>
public sealed class GenesysOptions
{
    public const string SectionName = "Genesys";

    /// <summary>Whether inbound Genesys inquiry/end processing is switched on. Default false — the integration stays dark until it is deliberately enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The TigerCS <b>Web</b> application's public base address (e.g.
    /// <c>https://tigercs-uat.example/</c>) — what a Secure Screen Pop
    /// <c>launchUrl</c> is built on, since the Api cannot know where the Web
    /// is served. Unset means Screen Pop is not configured: the endpoint
    /// refuses to issue a launch rather than guess an address.
    /// </summary>
    public string? ScreenPopWebBaseUrl { get; set; }

    /// <summary>The default for <see cref="CustomerInactivityTimeoutMinutes"/>: Website/Chatbot tickets close after "more than 10 minutes" without customer interaction.</summary>
    public const int DefaultCustomerInactivityTimeoutMinutes = 10;

    /// <summary>The default for <see cref="AwaitingReplyTimeoutMinutes"/>: the original bot-prompt rule, unchanged.</summary>
    public const int DefaultAwaitingReplyTimeoutMinutes = 5;

    /// <summary>
    /// How long a Website/Chatbot conversation may go without ANY customer interaction (measured from the last one, not from ticket
    /// creation) before the background job closes its ticket. Default 10; the customer must be silent for <b>more than</b> this long.
    /// Call Center tickets, human-managed tickets and voice calls are outside this rule. Zero or a negative value switches it off.
    /// Set it under <c>Genesys:CustomerInactivityTimeoutMinutes</c>.
    /// </summary>
    public int CustomerInactivityTimeoutMinutes { get; set; } = DefaultCustomerInactivityTimeoutMinutes;

    /// <summary>
    /// The original rule — the chatbot asked something (<c>awaitingCustomerReply: true</c>) and the customer did not answer — which
    /// still governs every ticket the Website/Chatbot rule does not track (voice, Call Center). Default 5, so those tickets are not
    /// affected by the 10-minute rule. Zero or a negative value switches it off. <c>Genesys:AwaitingReplyTimeoutMinutes</c>.
    /// </summary>
    public int AwaitingReplyTimeoutMinutes { get; set; } = DefaultAwaitingReplyTimeoutMinutes;

    /// <summary>The bot-prompt timeout, or null when that rule is switched off.</summary>
    public TimeSpan? AwaitingReplyTimeout =>
        AwaitingReplyTimeoutMinutes > 0 ? TimeSpan.FromMinutes(AwaitingReplyTimeoutMinutes) : null;

    /// <summary>The default for <see cref="DefaultTicketPriority"/>: the business tier name used by the SLA document.</summary>
    public const string DefaultDefaultTicketPriority = "Normal";

    /// <summary>
    /// The priority a new Genesys ticket starts with, by name. Resolved
    /// against the Priorities table at ingestion time (never by a hard-coded
    /// id): a priority row with exactly this name wins, otherwise the
    /// documented business alias applies (<c>Normal</c> is the Medium tier —
    /// see <c>PriorityAliases</c>). Set it under
    /// <c>Genesys:DefaultTicketPriority</c>; blank disables the default and
    /// restores "no priority until classified".
    /// </summary>
    public string? DefaultTicketPriority { get; set; } = DefaultDefaultTicketPriority;

    /// <summary>
    /// Whether ending an AI conversation that no human ever took raises pending human work on TigerCS' own initiative
    /// (<c>HandoffTrigger.AiConnectionLost</c>). <b>Off by default</b>: a conversation ending — the AI finished, the customer left, the
    /// browser closed — is not a request for a human, and only an explicit Genesys signal (<c>handoff.required = true</c>) may record one.
    /// Switch it on (<c>Genesys:AutoRaiseHandoffOnConversationEnd</c> = true) only to restore the previous safety-net behaviour.
    /// </summary>
    public bool AutoRaiseHandoffOnConversationEnd { get; set; }

    /// <summary>
    /// <b>Who answered owns the ticket</b> (<c>Genesys:AssignOnAnswer</c>, default <b>true</b>): a Genesys call answered by a mapped, eligible
    /// Call Center agent is assigned DIRECTLY to that agent instead of going through Round Robin, and an answering agent who cannot take it
    /// leaves the ticket flagged in the Call Center queue. Set it to <c>false</c> to restore the previous behaviour (Round Robin for every
    /// ticket that needs a person; the answering agent only recorded as a handler) without redeploying - the rollback lever for this rule.
    /// </summary>
    public bool AssignOnAnswer { get; set; } = true;

    /// <summary>
    /// <b>E-mail-form HumanAgent <c>senderId</c></b> (<c>Genesys:AllowEmailSenderIdFallback</c>, default <b>true</b>). The contract says a transcript
    /// <c>senderId</c> is the agent's Genesys User ID (GUID), and that is always tried first. Genesys has been observed to send the agent's
    /// e-mail there instead (ticket 225: <c>callcenter5@...</c>). With this on, an e-mail-form id resolves to a Ticketing user only when it
    /// equals - exactly, as a whole address - the account e-mail (or recorded Genesys e-mail) of ONE active, unlocked, non-service CS Agent;
    /// anything else (none, several, inactive, locked, service, not a CS Agent) resolves to nobody. Display names are never matched.
    /// Set <c>false</c> to accept the GUID form only.
    /// </summary>
    public bool AllowEmailSenderIdFallback { get; set; } = true;

    /// <summary>
    /// The code of the department that acts as the <b>safe classification queue</b> (<c>Genesys:ClassificationDepartmentCode</c>, e.g. the
    /// department whose staff triage Unclassified tickets). Used only when configuration cannot place a Genesys ticket: no department,
    /// queue mapping or valid request type named one, or a supplied request type is unusable (unmapped intent, retired department, no
    /// published workflow). The ticket is then created <b>Unclassified</b> in this department for a person to classify, rather than being
    /// refused (and the customer's request lost) or routed on a guess. Resolved by code against the Departments table — never an id in code.
    /// Unset (the default) keeps the previous behaviour: an unroutable inquiry or an unusable request type is refused with 422 and nothing is created.
    /// </summary>
    public string? ClassificationDepartmentCode { get; set; }

    /// <summary>The configured timeout, or null when automatic closure is switched off.</summary>
    public TimeSpan? CustomerInactivityTimeout =>
        CustomerInactivityTimeoutMinutes > 0 ? TimeSpan.FromMinutes(CustomerInactivityTimeoutMinutes) : null;
}
