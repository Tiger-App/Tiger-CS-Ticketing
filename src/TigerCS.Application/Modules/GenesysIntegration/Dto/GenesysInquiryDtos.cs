using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// The channel a Genesys inquiry arrived on, as <b>TigerCS</b> names it —
/// the normalized boundary value, deliberately not Genesys' own media-type
/// vocabulary (which is not confirmed). The integration/API edge maps
/// whatever Genesys sends onto one of these; everything inside Ticketing
/// speaks only this enum, and each value resolves to a configured
/// <c>Channel</c> row for recording/reporting.
/// </summary>
public enum GenesysChannel
{
    /// <summary>A voice call. A ticket is created when the agent ANSWERS, never while the phone is ringing.</summary>
    Phone = 1,

    /// <summary>The website's live chat (the form that asks the customer to choose Leasing / Customer Service / Maintenance).</summary>
    WebsiteChat = 2,

    WhatsApp = 3,

    /// <summary>
    /// A social-media direct message from a platform that has no channel of its own (X, Telegram, …), or one whose platform Genesys did not
    /// name. Instagram and Facebook Messenger are <see cref="Instagram"/> and <see cref="Facebook"/>, never this value.
    /// </summary>
    SocialMedia = 4,

    /// <summary>An Instagram direct message — recorded under its own <c>INSTAGRAM</c> channel so it is findable under Instagram.</summary>
    Instagram = 5,

    /// <summary>A Facebook Messenger conversation — recorded under its own <c>FACEBOOK</c> channel so it is findable under Facebook.</summary>
    Facebook = 6
}

/// <summary>
/// The normalized Genesys inquiry — the <b>only</b> shape Ticketing's
/// application layer understands, defined here on Ticketing's side of the
/// boundary. Raw Genesys transport payloads never travel past
/// <c>TigerCS.Api</c>'s Genesys contracts: the API edge maps them onto this
/// record, so no domain or application code depends on Genesys' JSON or its
/// field names.
///
/// <para>
/// <b>There is no event vocabulary.</b> Receiving this inquiry means "create
/// or reuse a ticket for this conversation" and nothing else. A ringing phone
/// never reaches TigerCS at all — the phone flow starts when the agent picks
/// up — so no Ringing/Answered/Started vocabulary is invented for Genesys to
/// send.
/// </para>
///
/// <para>
/// <b>Everything except <see cref="ConversationId"/> and
/// <see cref="Channel"/> is optional</b>, because Genesys' per-channel
/// guarantees are not confirmed. Absent values are stored as null, never
/// guessed. All four ticket-creating channels converge on this one contract
/// and one ingestion flow — there is no per-channel ticket-creation path.
/// </para>
/// </summary>
/// <param name="ConversationId">Required. Genesys' conversation identifier — the idempotency key and the permanent Ticket ↔ conversation link.</param>
/// <param name="Channel">Required. Which TigerCS-normalized channel the inquiry arrived on.</param>
/// <param name="InteractionId">Genesys' interaction id, where it differs from the conversation id and is supplied.</param>
/// <param name="ParticipantId">Genesys' customer-participant id, where supplied.</param>
/// <param name="CommunicationId">Genesys' communication id, where supplied.</param>
/// <param name="Direction">"Inbound"/"Outbound" as reported, where available.</param>
/// <param name="CustomerPhone">The customer's mobile number — the identity input the EXISTING TigerCS customer lookup searches with. Absent for a chat that collected no number.</param>
/// <param name="CustomerName">The customer's name as the channel collected it (e.g. the website chat form's Full Name).</param>
/// <param name="CustomerEmail">The customer's email as the channel collected it.</param>
/// <param name="CalledNumber">The Tiger number the customer dialed (Genesys-side datum; never used by Ticketing for routing).</param>
/// <param name="QueueId">The Genesys queue the inquiry was routed to — resolved to a department through the configured queue mapping when the customer selected no department.</param>
/// <param name="QueueName">The Genesys queue's display name, where supplied.</param>
/// <param name="AgentId">The Genesys agent handling the inquiry, where supplied.</param>
/// <param name="AgentName">The Genesys agent's display name, where supplied.</param>
/// <param name="StartedAtUtc">When the interaction started on the Genesys side (UTC), where supplied.</param>
/// <param name="DepartmentId">
/// The department the customer explicitly selected — the website chat's
/// Leasing / Customer Service / Maintenance choice. Takes priority over the
/// queue mapping. Supply this OR <paramref name="DepartmentCode"/>, not both.
/// </param>
/// <param name="DepartmentCode">The same explicit selection expressed as a TigerCS department code, for a caller that knows codes rather than ids.</param>
/// <param name="TowerName">The tower/project the customer typed into the website chat form, where collected — carried onto the ticket as the manual project snapshot when no verified customer match exists.</param>
/// <param name="UnitNumber">The unit number the customer typed into the website chat form, where collected — see <paramref name="TowerName"/>.</param>
/// <param name="Subject">A short subject/summary the channel collected, where available. Used as the ticket's request summary; a generated, channel-named summary is used when absent.</param>
/// <param name="RequestType">The request type the bot identified, when it already has one; otherwise null and the ticket awaits classification in the human follow-up queue.</param>
/// <param name="ServiceRequestId">
/// The caller's identifier of ONE distinct service request inside the conversation (e.g. "documents", "leasing"). A conversation may carry
/// several; each (<paramref name="ConversationId"/>, <paramref name="ServiceRequestId"/>) pair is its own idempotent ticket. Absent keeps the
/// original rule: the conversation is one ticket, and every repeat returns it.
/// </param>
/// <param name="HumanFollowUpRequired">true = this service request needs a person (leasing, a complaint). Such a ticket is never closed by the chatbot inactivity rules, by the AI or by document delivery — only by the authorized TigerCS workflow.</param>
public sealed record GenesysInquiryDto(
    string ConversationId,
    GenesysChannel Channel,
    string? InteractionId = null,
    string? ParticipantId = null,
    string? CommunicationId = null,
    string? Direction = null,
    string? CustomerPhone = null,
    string? CustomerName = null,
    string? CustomerEmail = null,
    string? CalledNumber = null,
    string? QueueId = null,
    string? QueueName = null,
    string? AgentId = null,
    string? AgentName = null,
    DateTime? StartedAtUtc = null,
    int? DepartmentId = null,
    string? DepartmentCode = null,
    string? TowerName = null,
    string? UnitNumber = null,
    string? Subject = null,
    GenesysRequestTypeDto? RequestType = null,
    string? ServiceRequestId = null,
    bool? HumanFollowUpRequired = null);

/// <summary>
/// The normalized end-of-conversation report, with the transcript available
/// up to that moment. Sent when a conversation ends for ANY reason — the
/// agent ended it, the customer closed the browser, the connection dropped,
/// Genesys timed it out — so the interaction is always finalized and the
/// conversation history is never lost.
/// </summary>
/// <param name="ConversationId">Required. The conversation being ended — the same id the inquiry was ingested under.</param>
/// <param name="EndedAtUtc">When it ended (UTC). Defaults to the server's clock when Genesys supplies none.</param>
/// <param name="EndReason">Why it ended, as reported ("AgentDisconnect", "CustomerDisconnect", "Timeout", …). Free text until the contract fixes an enumeration.</param>
/// <param name="AgentId">The handling agent, where the end event names one and the interaction did not already know it.</param>
/// <param name="AgentName">The handling agent's display name — see <paramref name="AgentId"/>.</param>
/// <param name="Transcript">The conversation's messages, in order. Empty/absent for a voice call, or for a chat that ended before anything was said.</param>
/// <param name="TicketId">The ticket whose interaction this ends, when the conversation carries several tickets; absent resolves the conversation's default (first) ticket.</param>
public sealed record GenesysConversationEndDto(
    string ConversationId,
    DateTime? EndedAtUtc = null,
    string? EndReason = null,
    string? AgentId = null,
    string? AgentName = null,
    IReadOnlyList<GenesysTranscriptMessageDto>? Transcript = null,
    long? TicketId = null);

/// <summary>One transcript message, normalized. Order is taken from the list's own order; <paramref name="SentAtUtc"/> is what the UI displays.</summary>
/// <param name="Sender">"Customer", "Agent" or "System" (case-insensitive). Anything else is rejected rather than silently attributed.</param>
/// <param name="SentAtUtc">When the message was sent (UTC).</param>
/// <param name="Body">The message text, verbatim. Required.</param>
/// <param name="SenderName">The sender's display name, where available.</param>
/// <param name="SenderId">The sender's Genesys participant/agent id, where available.</param>
/// <param name="ExternalMessageId">Genesys' own message id, where available.</param>
public sealed record GenesysTranscriptMessageDto(
    string Sender,
    DateTime SentAtUtc,
    string Body,
    string? SenderName = null,
    string? SenderId = null,
    string? ExternalMessageId = null);

/// <summary>How a Genesys inquiry ingestion landed.</summary>
public enum GenesysIngestionOutcome
{
    /// <summary>A new ticket was created for this conversation.</summary>
    TicketCreated,

    /// <summary>This conversation was already ingested — the SAME ticket is returned, and nothing was created. The correct answer to any retry or duplicate delivery.</summary>
    AlreadyIngested,

    /// <summary>The Genesys integration is switched off (<c>Genesys:Enabled=false</c>). Nothing was created; normal manual ticketing is unaffected.</summary>
    IntegrationDisabled,

    /// <summary>ConversationId was missing or blank — without it nothing can be made idempotent, so the inquiry is refused rather than half-processed.</summary>
    ConversationIdRequired,

    /// <summary>No department could be resolved: the customer selected none, and the queue is unmapped (or names an inactive/unknown department).</summary>
    DepartmentNotResolved,

    /// <summary>The normalized channel does not resolve to a configured, active <c>Channel</c> row.</summary>
    ChannelNotConfigured,

    /// <summary>A request type WAS supplied but does not exist, is inactive, is ambiguous or cannot route. Nothing was written: no ticket, intake record, interaction or audit entry. (A request type that is merely absent is not an error: the ticket is created and awaits human classification.)</summary>
    RequestTypeInvalid,

    /// <summary>Ticket creation itself was refused; <see cref="GenesysIngestionResult.TicketCreationOutcome"/> carries the underlying reason unchanged.</summary>
    TicketCreationFailed
}

/// <summary>
/// The result of ingesting one normalized inquiry.
/// <see cref="Ticket"/> is populated for both <see cref="GenesysIngestionOutcome.TicketCreated"/>
/// and <see cref="GenesysIngestionOutcome.AlreadyIngested"/> — a retry gets
/// the original ticket back, which is exactly what makes the operation safe
/// to repeat.
/// </summary>
public sealed record GenesysIngestionResult(
    GenesysIngestionOutcome Outcome,
    TicketResponseDto? Ticket = null,
    TicketCreationOutcome? TicketCreationOutcome = null,
    string? Detail = null,
    GenesysClassificationResult? Classification = null)
{
    public static GenesysIngestionResult Created(TicketResponseDto ticket, GenesysClassificationResult? classification = null) =>
        new(GenesysIngestionOutcome.TicketCreated, ticket, Classification: classification);

    public static GenesysIngestionResult AlreadyIngested(TicketResponseDto ticket) =>
        new(GenesysIngestionOutcome.AlreadyIngested, ticket);

    public static GenesysIngestionResult Failure(GenesysIngestionOutcome outcome, string? detail = null) =>
        new(outcome, Detail: detail);

    public static GenesysIngestionResult CreationFailed(TicketCreationOutcome creationOutcome) =>
        new(GenesysIngestionOutcome.TicketCreationFailed, TicketCreationOutcome: creationOutcome);
}

/// <summary>How an incremental transcript delivery landed.</summary>
public enum GenesysTranscriptAppendOutcome
{
    /// <summary>New messages were stored.</summary>
    Appended,

    /// <summary>Every message was already stored (a retry, or the final transcript repeating live deliveries) — nothing written.</summary>
    Unchanged,

    IntegrationDisabled,
    ConversationNotFound,

    /// <summary>A message has an unrecognized sender or an empty body; nothing was stored.</summary>
    InvalidTranscript,

    /// <summary>A concurrent delivery stored at the same position; nothing from this request was kept and resending is safe.</summary>
    Conflict
}

/// <summary>What an incremental transcript delivery did. <paramref name="Total"/> is the whole stored transcript's length afterwards.</summary>
public sealed record GenesysTranscriptAppendResult(
    GenesysTranscriptAppendOutcome Outcome, int Appended = 0, int Total = 0, string? Detail = null)
{
    public static GenesysTranscriptAppendResult Failure(GenesysTranscriptAppendOutcome outcome, string? detail = null) =>
        new(outcome, 0, 0, detail);
}

/// <summary>How a conversation-end report landed.</summary>
public enum GenesysConversationEndOutcome
{
    /// <summary>The interaction was finalized (end time, reason, transcript).</summary>
    Ended,

    /// <summary>The interaction was already ended — nothing changed, the existing end record and transcript stand. The correct answer to a duplicate end event.</summary>
    AlreadyEnded,

    /// <summary>No interaction exists for this conversation id (an end for a conversation that never produced a ticket, e.g. a ringing call that was never answered).</summary>
    ConversationNotFound,

    /// <summary>The Genesys integration is switched off.</summary>
    IntegrationDisabled,

    /// <summary>ConversationId was missing or blank.</summary>
    ConversationIdRequired,

    /// <summary>A transcript message was malformed (unknown sender, or an empty body).</summary>
    InvalidTranscript
}

/// <summary>The result of ending one conversation — carries the ticket the interaction belongs to, so a caller can confirm the ticket is still open.</summary>
public sealed record GenesysConversationEndResult(
    GenesysConversationEndOutcome Outcome,
    long? TicketId = null,
    string? TicketNumber = null,
    string? TicketStatus = null,
    int TranscriptMessageCount = 0,
    string? Detail = null)
{
    public static GenesysConversationEndResult Failure(GenesysConversationEndOutcome outcome, string? detail = null) =>
        new(outcome, Detail: detail);
}
