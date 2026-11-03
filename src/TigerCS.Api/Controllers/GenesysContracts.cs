using System.Text.Json;
using System.Text.Json.Serialization;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The <b>transport</b> shape of an inbound Genesys inquiry — the outermost
/// edge of the integration, and the only place a Genesys-facing wire format
/// is allowed to exist. Its whole job is to be mapped onto the normalized
/// <see cref="GenesysInquiryDto"/> the application layer understands, so no
/// domain or application code depends on how Genesys words anything.
///
/// <para>
/// <b>This is TigerCS' contract, not a reproduction of Genesys'.</b> No
/// official Genesys webhook schema, event-topic vocabulary, authentication
/// mechanism or payload has been confirmed to this repository, so none is
/// invented here: this endpoint asks for exactly the facts Ticketing needs
/// and nothing more. In particular there is <b>no event field</b> — posting
/// this body means "create or reuse a ticket for this conversation", and
/// nothing else. A ringing call never reaches TigerCS. When the Genesys team supplies their real contract, the
/// adapter that translates it lives at this boundary — this record changes
/// or gains a sibling, and nothing behind it moves.
/// </para>
/// </summary>
/// <param name="ConversationId">Required. Genesys' conversation id — the idempotency key: the same value always resolves to the same ticket.</param>
/// <param name="Channel">Required. One of "Phone", "LiveChat", "WhatsApp", "Instagram", "Facebook", "SocialMedia" (case-insensitive; spaces, "-" and "_" are ignored). "WebsiteChat", "WebChat" and "WebMessaging" (Genesys' own name for its web chat) are accepted as the same Live Chat channel; "FacebookMessenger"/"Messenger" as Facebook; "Twitter", "X" and "Telegram" as SocialMedia.</param>
/// <param name="InteractionId">Genesys' interaction id, where it differs from the conversation id.</param>
/// <param name="ParticipantId">Genesys' customer-participant id, where available.</param>
/// <param name="CommunicationId">Genesys' communication id, where available.</param>
/// <param name="Direction">"Inbound"/"Outbound", where available.</param>
/// <param name="CustomerPhone">The customer's number — what the existing TigerCS customer lookup searches with. A telephony address is accepted as Genesys reports it (<c>Call.Ani</c>: "tel:+971…"); it is normalized to "+971…" before it is searched or stored.</param>
/// <param name="CustomerName">The customer's name, where the channel collected one (e.g. the website chat form).</param>
/// <param name="CustomerEmail">The customer's email, where the channel collected one.</param>
/// <param name="CalledNumber">The Tiger number the customer dialed. Recorded, never used for routing.</param>
/// <param name="QueueId">The Genesys queue — resolved to a department via the configured queue mapping when the customer chose no department.</param>
/// <param name="QueueName">The queue's display name, where available.</param>
/// <param name="AgentId">The handling Genesys agent's <b>Genesys User ID</b> (the immutable id, not a name), where available. When supplied, it is resolved server-side to the mapped Ticketing user and that user is recorded as the interaction's handler; an unmapped id is recorded verbatim with no handler.</param>
/// <param name="AgentName">The handling agent's display name, where available. Display only — never an identity key.</param>
/// <param name="StartedAtUtc">When the interaction started, UTC.</param>
/// <param name="DepartmentId">The department the customer explicitly selected (website chat). Wins over the queue mapping.</param>
/// <param name="DepartmentCode">The same explicit selection as a TigerCS department code, for a caller that knows codes rather than ids.</param>
/// <param name="TowerName">The tower/project the customer typed into the chat form, where collected.</param>
/// <param name="UnitNumber">The unit number the customer typed into the chat form, where collected.</param>
/// <param name="Subject">A short subject the channel collected, where available — becomes the ticket's request summary.</param>
/// <param name="RequestType">The request type the bot identified, when it already has one. Validated, then its department routing, automatic assignment and SLA policy are applied. Omit it (or send it blank) when unknown: the ticket then awaits classification in the human follow-up queue, and the bot can send it later on <c>PATCH</c>.</param>
/// <param name="ServiceRequestId">Identifies ONE distinct service request inside the conversation (e.g. <c>documents</c>, <c>leasing</c>) — chosen by the caller, any stable string up to 64 characters. Each (<c>conversationId</c>, <c>serviceRequestId</c>) pair is its own idempotent ticket: a second distinct request in the same conversation creates its own ticket, a retry of the same pair returns the original. Omit it and the whole conversation is one ticket, as before.</param>
/// <param name="HumanFollowUpRequired">true when this request needs a person (apartment leasing, a complaint). The ticket is then never closed by the chatbot inactivity rules, by <c>closedByAi</c> or by document delivery — only by an employee through the normal TigerCS workflow.</param>
public sealed record GenesysInquiryRequest(
    string ConversationId,
    string Channel,
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
    GenesysRequestTypePart? RequestType = null,
    string? ServiceRequestId = null,
    [property: JsonConverter(typeof(LenientNullableBoolConverter))] bool? HumanFollowUpRequired = null);

/// <summary>
/// The request type the bot identified. Send <c>requestTypeId</c> (preferred) or
/// <c>name</c>; the id wins when both are sent. Omit the whole part, or send blanks, when the bot
/// did not supply one (an explicitly invalid one is refused with 422 and nothing is created): the ticket is then created <b>Unclassified</b> for the existing manual classification workflow. A missing request type is never a request for a human: no handoff is recorded for it.
/// </summary>
/// <param name="RequestTypeId">The configured request type's id (see <c>GET /api/request-types</c>).</param>
/// <param name="Name">The request type's exact name (case-insensitive). Refused as ambiguous if more than one department has it.</param>
[JsonConverter(typeof(GenesysRequestTypePartConverter))]
public sealed record GenesysRequestTypePart(int? RequestTypeId = null, string? Name = null);

/// <summary>
/// Accepts the request type either as the documented object (<c>{"requestTypeId":17}</c> / <c>{"name":"Leasing"}</c>) or as a bare string,
/// which is read as the request type's <b>name</b> (<c>"requestType": "Leasing"</c>). The name still has to match a configured request type
/// exactly — nothing is guessed from it.
/// </summary>
public sealed class GenesysRequestTypePartConverter : JsonConverter<GenesysRequestTypePart>
{
    public override GenesysRequestTypePart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return new GenesysRequestTypePart(Name: reader.GetString());
            case JsonTokenType.StartObject:
                int? id = null;
                string? name = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var property = reader.GetString();
                    reader.Read();
                    if (string.Equals(property, "requestTypeId", StringComparison.OrdinalIgnoreCase))
                    {
                        id = reader.TokenType switch
                        {
                            JsonTokenType.Number => reader.GetInt32(),
                            JsonTokenType.String when int.TryParse(reader.GetString(), out var parsed) => parsed,
                            JsonTokenType.String when string.IsNullOrWhiteSpace(reader.GetString()) => null,
                            JsonTokenType.Null => null,
                            _ => throw new JsonException("requestTypeId must be a number.")
                        };
                    }
                    else if (string.Equals(property, "name", StringComparison.OrdinalIgnoreCase))
                    {
                        name = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                return new GenesysRequestTypePart(id, name);
            default:
                throw new JsonException("requestType must be an object or the request type's name.");
        }
    }

    public override void Write(Utf8JsonWriter writer, GenesysRequestTypePart value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.RequestTypeId is { } id)
        {
            writer.WriteNumber("requestTypeId", id);
        }
        else
        {
            writer.WriteNull("requestTypeId");
        }

        writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }
}

/// <summary>The transport shape of a conversation-end report, with the transcript available up to that moment. See <see cref="GenesysInquiryRequest"/> for why this record exists at the edge.</summary>
/// <param name="ConversationId">Required. The conversation being ended — the id it was ingested under.</param>
/// <param name="EndedAtUtc">When it ended, UTC. The server's clock is used when absent.</param>
/// <param name="EndReason">Why it ended, as reported ("AgentDisconnect", "CustomerDisconnect", "Timeout", …).</param>
/// <param name="AgentId">The handling agent, where the end event names one.</param>
/// <param name="AgentName">The handling agent's display name.</param>
/// <param name="Transcript">The conversation's messages, in order. Absent/empty for a voice call.</param>
public sealed record GenesysConversationEndRequest(
    string ConversationId,
    DateTime? EndedAtUtc = null,
    string? EndReason = null,
    string? AgentId = null,
    string? AgentName = null,
    IReadOnlyList<GenesysTranscriptMessageRequest>? Transcript = null);

/// <summary>One transcript message, at the transport edge.</summary>
/// <param name="Sender">Required. "Customer", "HumanAgent", "VirtualAgent" or "System" (case-insensitive).</param>
/// <param name="SentAtUtc">Required. When the message was sent, UTC.</param>
/// <param name="Body">Required. The message text, verbatim.</param>
/// <param name="SenderName">The sender's display name, where available.</param>
/// <param name="SenderId">The sender's Genesys participant/agent id, where available.</param>
/// <param name="ExternalMessageId">Genesys' own message id, where available.</param>
public sealed record GenesysTranscriptMessageRequest(
    string Sender,
    DateTime SentAtUtc,
    string Body,
    string? SenderName = null,
    string? SenderId = null,
    string? ExternalMessageId = null);

/// <summary>What the ingestion endpoint answers — always naming the ticket, so a retry and the original call are indistinguishable to the caller.</summary>
/// <param name="Outcome">"TicketCreated" or "AlreadyIngested".</param>
/// <param name="ConversationId">The conversation this refers to, echoed back.</param>
/// <param name="TicketId">The one ticket this conversation produced.</param>
/// <param name="TicketNumber">That ticket's human-facing number.</param>
/// <param name="ClassificationStatus">"Classified" when a valid request type was applied; "AwaitingClassification" when no request type was supplied (the ticket is then on Normal priority, Unclassified, and no human handoff is recorded). A request type that was supplied but is invalid is not accepted at all: the call answers 422 and nothing is created. Absent on a repeated (<c>AlreadyIngested</c>) delivery, which changes nothing.</param>
/// <param name="RequestTypeId">The applied request type, when classified.</param>
/// <param name="DepartmentId">The responsible department after routing.</param>
/// <param name="AssignedEmployeeId">The employee the automatic assignment chose; null means the department queue.</param>
/// <param name="ClassificationDetail">Why the ticket is awaiting classification, when it is.</param>
/// <param name="ServiceRequestId">The service request this ticket is for, echoed back when one was sent.</param>
public sealed record GenesysInquiryAcceptedResponse(
    string Outcome,
    string ConversationId,
    long TicketId,
    string TicketNumber,
    string? ClassificationStatus = null,
    int? RequestTypeId = null,
    int? DepartmentId = null,
    Guid? AssignedEmployeeId = null,
    string? ClassificationDetail = null,
    string? ServiceRequestId = null);

/// <summary>What the conversation-end endpoint answers. <paramref name="TicketStatus"/> is included deliberately: it shows that ending a conversation did NOT close the ticket.</summary>
/// <param name="Outcome">"Ended" or "AlreadyEnded".</param>
/// <param name="ConversationId">The conversation, echoed back.</param>
/// <param name="TicketId">The ticket the conversation belongs to.</param>
/// <param name="TicketNumber">That ticket's number.</param>
/// <param name="TicketStatus">The ticket's status — unchanged by the conversation ending.</param>
/// <param name="TranscriptMessageCount">How many transcript messages the interaction now holds.</param>
public sealed record GenesysConversationEndResponse(
    string Outcome,
    string ConversationId,
    long? TicketId,
    string? TicketNumber,
    string? TicketStatus,
    int TranscriptMessageCount);

/// <summary>
/// The one Genesys update body. Every part is optional and independently
/// idempotent — send only what changed, and resend freely.
///
/// <para>
/// <b>Deliberately narrow.</b> There is no field for category, priority,
/// status, owner, department, resolution or closure: Genesys owns the
/// conversation, TigerCS owns the ticket's business state, and a field
/// absent from this contract is one Genesys cannot reach. The one business
/// fact it may report is the <c>requestType</c> the bot identified —
/// validated by TigerCS, which then applies that request type's configured
/// department routing, automatic assignment and SLA policy itself.
/// </para>
/// </summary>
/// <param name="ConversationId">Required. Must resolve to an interaction on the ticket in the route.</param>
/// <param name="AgentId">The Genesys User ID of the agent handling it, when known. Applied only if the interaction does not already name one; when it maps to a Ticketing user, that user is recorded as the interaction's handler (same apply-if-absent rule).</param>
/// <param name="AgentName">That agent's display name, same apply-if-absent rule. Display only — never an identity key.</param>
/// <param name="Ended">Set when the conversation has finished, for any reason. <b>Never closes the ticket.</b></param>
/// <param name="Handoff">Set when the conversation needs a human agent, or when one has taken it.</param>
/// <param name="StartedAtUtc">When the interaction started, UTC — recorded only if the Create call did not carry it. Never moves once known.</param>
/// <param name="Routing">Set when the conversation moved to another queue, or an agent connected / it was transferred. Same ticket, always.</param>
/// <param name="CustomerConfirmation">Set when the customer explicitly confirmed the issue is resolved. <b>Record-only — never resolves or closes the ticket.</b></param>
/// <param name="RequestType">Set when the bot has identified the request type (initially missing, or later in the conversation). Validated; then the request type's department routing, automatic assignment and SLA policy are applied without restarting the SLA clock. Repeating the same one changes nothing; a different one than the ticket already has is refused with 409.</param>
/// <param name="CustomerActive">
/// <c>true</c> = the customer just did something in the conversation (sent a message, answered the bot). It restarts the Website/Chatbot
/// inactivity clock: such a ticket closes automatically once the customer has done nothing for more than
/// <c>Genesys:CustomerInactivityTimeoutMinutes</c> (default 10), counted from the LAST interaction, not from ticket creation. Send it on
/// every inbound customer message — TigerCS cannot see messages Genesys does not report. <c>awaitingCustomerReply: false</c> also counts.
/// <b>Omit it</b> (or send false) to change nothing.
/// </param>
/// <param name="ClosedByAi">
/// <c>true</c> = the Genesys AI (voicebot / chatbot) <b>finished the conversation</b>: TigerCS closes the ticket (Resolved → Closed) with the
/// exact reason <c>Closed by AI</c>. This is NOT a customer interaction and is separate from <c>awaitingCustomerReply</c>. Closes only a
/// ticket the AI still owns: refused (nothing changed, <c>closedByAiNote</c> says why) when a human is requested, pending or connected, a person
/// owns the ticket, it is a Call Center ticket, or it is already closed (an earlier closure — including a document-delivery one — is never
/// overwritten). Repeating it is harmless. Applied after every other part of the same update. <b>Omit it</b> (or false) to change nothing.
/// </param>
/// <param name="Transcript">
/// Messages of the <b>live</b> conversation, sent as they happen (one or several per call). Each has <c>sender</c> (<c>Customer</c>,
/// <c>VirtualAgent</c> or <c>HumanAgent</c>), <c>sentAtUtc</c> (the real time), <c>body</c> and — strongly recommended — a stable
/// <c>externalMessageId</c> (the Genesys message id). Purely additive: messages are appended in the order delivered and never edited or removed;
/// one already stored (same <c>externalMessageId</c>, or the same sender+time+body when none) is ignored, so retries and the final
/// <c>ended.transcript</c> repeating them create no duplicates. It does not end the conversation. A human agent opening the ticket sees
/// everything delivered so far. Only a <c>HumanAgent</c> line can record First Response — never <c>VirtualAgent</c>. <b>Omit it</b> when there is nothing new.
/// </param>
/// <param name="AwaitingCustomerReply">
/// Chatbot inactivity timer. <c>true</c> = the chatbot just asked the customer
/// something and is waiting for the reply: TigerCS starts a persistent timer
/// (server clock). Repeating <c>true</c> while the timer runs changes nothing — a retried
/// webhook never restarts it. <c>false</c> = the customer replied: the timer is cancelled.
/// Send <c>true</c> again with the chatbot's next question to start a new one. If the customer
/// stays silent for more than <c>Genesys:AwaitingReplyTimeoutMinutes</c> (default 5) — on tickets the Website/Chatbot "customer inactive" rule does not track (voice, Call Center) — a
/// background job closes the ticket as a customer-inactivity closure — unless a human has been
/// requested or assigned, or the conversation ended, in which case no timer runs.
/// <b>Omit it</b> to leave the timer as it is.
/// </param>
/// <param name="HumanFollowUpRequired">true = this ticket needs a person: both chatbot inactivity clocks stop for good and neither <c>closedByAi</c> nor document delivery may close it afterwards. Sticky — only an employee resolves/closes it through the TigerCS workflow. <b>Omit it</b> (or false) to change nothing.</param>
public sealed record GenesysTicketUpdateRequest(
    string ConversationId,
    string? AgentId = null,
    string? AgentName = null,
    GenesysConversationEndPart? Ended = null,
    GenesysHandoffPart? Handoff = null,
    DateTime? StartedAtUtc = null,
    GenesysRoutingPart? Routing = null,
    GenesysCustomerConfirmationPart? CustomerConfirmation = null,
    [property: JsonConverter(typeof(LenientNullableBoolConverter))] bool? AwaitingCustomerReply = null,
    GenesysRequestTypePart? RequestType = null,
    [property: JsonConverter(typeof(LenientNullableBoolConverter))] bool? CustomerActive = null,
    [property: JsonConverter(typeof(LenientNullableBoolConverter))] bool? ClosedByAi = null,
    IReadOnlyList<GenesysTranscriptMessageRequest>? Transcript = null,
    [property: JsonConverter(typeof(LenientNullableBoolConverter))] bool? HumanFollowUpRequired = null);

/// <summary>
/// Reads a flag as <c>true</c>/<c>false</c>, the strings <c>"true"</c>/<c>"false"</c>, or blank/null → null ("not supplied"). A Genesys data
/// action cannot leave a field out of its request template, so an unset Architect variable arrives as <c>""</c> — which must mean "omitted",
/// exactly like every other optional part of this body. Anything else is a 400.
/// </summary>
public sealed class LenientNullableBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (bool.TryParse(text.Trim(), out var parsed))
                {
                    return parsed;
                }

                break;
        }

        throw new JsonException("The flag must be true or false.");
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteBooleanValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// The customer explicitly confirmed, during the interaction, that the issue
/// is resolved. TigerCS records it on the same ticket — Ticket Details and the
/// audit trail show it — and does <b>not</b> resolve or close the ticket:
/// those stay with the ticket owner and Customer Service, under their existing
/// authorization.
/// </summary>
/// <param name="ConfirmedResolved">Must be <c>true</c>; anything else is refused with 400.</param>
/// <param name="ConfirmedAtUtc">When the customer said it, UTC. Defaults to now. Send it: a resend with the same value stores nothing twice.</param>
/// <param name="Note">What the customer said, or the flow's note. Display only.</param>
public sealed record GenesysCustomerConfirmationPart(
    bool? ConfirmedResolved = null,
    DateTime? ConfirmedAtUtc = null,
    string? Note = null);

/// <summary>
/// Where the conversation is now: its current queue and connected agent. Send
/// it when the conversation enters a queue, when an agent connects, and on
/// every transfer. It never creates a ticket and never moves the ticket's
/// department, owner or status; when an agent connects to human work that is
/// still waiting for one, that work is recorded as taken by them.
/// </summary>
/// <param name="QueueId">The Genesys queue id the conversation is now in.</param>
/// <param name="QueueName">That queue's name.</param>
/// <param name="AgentId">The Genesys User ID of the agent now <b>connected</b> — send it when the agent answers/accepts, never while the interaction is only ringing or being offered to them. Every agent named here is recorded as having handled the interaction (which is what gives a CS Agent access to its ticket), so a transfer must send the receiving agent when they connect.</param>
/// <param name="AgentName">That agent's display name. Display only.</param>
/// <param name="ConnectedAtUtc">When that agent connected (Genesys' connected time, UTC). Optional.</param>
public sealed record GenesysRoutingPart(
    string? QueueId = null,
    string? QueueName = null,
    string? AgentId = null,
    string? AgentName = null,
    DateTime? ConnectedAtUtc = null);

/// <summary>The conversation has finished — for any reason: the agent ended it, the customer closed the browser, the connection dropped, Genesys timed it out.</summary>
/// <param name="EndedAtUtc">When it ended. Defaults to now.</param>
/// <param name="EndReason">Why, as reported (e.g. "AgentDisconnect", "CustomerDisconnect", "Timeout"). Free text — no vocabulary is confirmed.</param>
/// <param name="Transcript">The conversation in order, as far as available. Resending an already-stored transcript stores nothing twice.</param>
public sealed record GenesysConversationEndPart(
    DateTime? EndedAtUtc = null,
    string? EndReason = null,
    IReadOnlyList<GenesysTranscriptMessageRequest>? Transcript = null);

/// <summary>Human-agent state for the conversation, on any channel. A callback is one possible <paramref name="Mode"/>, never the concept.</summary>
/// <param name="Required">
/// <c>true</c> records that the conversation needs a human agent — idempotent,
/// as outstanding work is answered with that same work item.
/// <c>false</c> <b>stands outstanding work down</b> (the AI reconnected and
/// resumed, or the human is no longer needed), which requires
/// <paramref name="Reason"/> and leaves the ticket untouched; nothing
/// outstanding is not an error.
/// <b>Omit it</b> to do neither — an update reporting only
/// <paramref name="AssignedAgentId"/> must not read as a cancellation, which
/// is why this is nullable rather than defaulting to false.
/// </param>
/// <param name="Trigger">Why a human is needed, typed: "CustomerRequestedHuman", "AiConnectionLost", "AiEscalated", "RoutingDecision" or "AgentTransfer". <b>Omit it</b> unless Genesys states it — TigerCS never derives it from the channel or the end reason.</param>
/// <param name="AgentAvailable">True when Genesys is handing straight over to a named human; false when nobody is available and the work must wait.</param>
/// <param name="Mode">"Callback", "ContinueChat", "ReplyInChannel" or "HumanTakeover". <b>Omit it</b> unless Genesys actually states it — TigerCS never derives it from the channel.</param>
/// <param name="Reason">Why a human is needed (a virtual agent's escalation reason, a routing note).</param>
/// <param name="WorkItemId">Genesys' own routing-task id, if it has one — the stronger idempotency key. Omit if Genesys has none.</param>
/// <param name="AssignedAgentId">Set to record that this Genesys agent took the outstanding work. Applies to the existing work item, never a second one.</param>
public sealed record GenesysHandoffPart(
    bool? Required = null,
    bool AgentAvailable = false,
    string? Mode = null,
    string? Reason = null,
    string? Trigger = null,
    string? WorkItemId = null,
    string? AssignedAgentId = null);

/// <summary>What the update endpoint answers. <paramref name="TicketStatus"/> is echoed deliberately: it is the proof that ending a conversation did not close the ticket.</summary>
/// <param name="Outcome">"Applied".</param>
/// <param name="ConversationId">The conversation, echoed back.</param>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">Its number.</param>
/// <param name="TicketStatus">Its status — unchanged by anything in this contract.</param>
/// <param name="ConversationEnded">Whether the interaction is now recorded as ended.</param>
/// <param name="TranscriptMessageCount">How many transcript messages the interaction holds after this update.</param>
/// <param name="HandoffStatus">The pending human work's status when there is any: WaitingForAgent, Assigned, InProgress, Completed or Cancelled. Null when this conversation never needed a human.</param>
/// <param name="TicketAgentHandoffId">That work item's id, when there is one.</param>
/// <param name="AwaitingCustomerReply">Whether the inactivity timer is running for this conversation after this update.</param>
/// <param name="InactivityDeadlineUtc">When the ticket becomes eligible for inactivity closure, while the timer runs; otherwise null.</param>
/// <param name="AwaitingCustomerReplyNote">Why a requested timer was not started (e.g. human follow-up pending), when it was not.</param>
/// <param name="ClassificationStatus">"Classified" when the update carried a request type that is now applied.</param>
/// <param name="RequestTypeId">The applied request type.</param>
/// <param name="DepartmentId">The responsible department after routing.</param>
/// <param name="AssignedEmployeeId">The employee chosen by the automatic assignment; null means the department queue.</param>
/// <param name="ClosedByAi">True when this call closed the ticket because <c>closedByAi: true</c> was sent.</param>
/// <param name="ClosedByAiNote">Why <c>closedByAi: true</c> did not close the ticket, when it did not.</param>
public sealed record GenesysTicketUpdateResponse(
    string Outcome,
    string ConversationId,
    long TicketId,
    string TicketNumber,
    string TicketStatus,
    bool ConversationEnded,
    int TranscriptMessageCount,
    string? HandoffStatus,
    long? TicketAgentHandoffId,
    bool AwaitingCustomerReply = false,
    DateTime? InactivityDeadlineUtc = null,
    string? AwaitingCustomerReplyNote = null,
    string? ClassificationStatus = null,
    int? RequestTypeId = null,
    int? DepartmentId = null,
    Guid? AssignedEmployeeId = null,
    bool ClosedByAi = false,
    string? ClosedByAiNote = null);

/// <summary>
/// The transport shape of a Genesys <b>agent action</b>: the Ticketing screen
/// opened from Genesys identifying which agent is working, and on which
/// conversation. This is the strict agent endpoint — <c>genesysUserId</c> is
/// required and must be mapped to an active Ticketing user.
///
/// <para>
/// <b>There is deliberately no Ticketing user id in this body.</b> Which
/// Ticketing user handled the interaction is resolved on the server from
/// <paramref name="GenesysUserId"/> alone; any such property a client sends
/// is ignored by model binding, never honoured.
/// </para>
/// </summary>
/// <param name="GenesysUserId">Required. The agent's immutable Genesys User ID — the only value used to identify the agent.</param>
/// <param name="AgentEmail">The agent's email as Genesys knows it. Informational only: recorded on the audit trail, never used to resolve the agent.</param>
/// <param name="ConversationId">The conversation the agent is working, when there is one. Its interaction then records the resolved user as the handler.</param>
public sealed record GenesysAgentContextRequest(
    string? GenesysUserId,
    string? AgentEmail = null,
    string? ConversationId = null);

/// <summary>What the agent-context endpoint answers: who the agent is in Ticketing, and — when a conversation was named — which ticket and interaction it belongs to.</summary>
/// <param name="Outcome">"Resolved".</param>
/// <param name="GenesysUserId">The Genesys User ID, echoed back as stored on the mapping.</param>
/// <param name="UserId">The Ticketing user id the agent resolved to.</param>
/// <param name="UserName">That user's account name.</param>
/// <param name="DisplayName">That user's display name.</param>
/// <param name="Roles">The user's Ticketing roles.</param>
/// <param name="DepartmentIds">The departments the user is a member of.</param>
/// <param name="ConversationId">The conversation, echoed back, when one was supplied.</param>
/// <param name="TicketId">The ticket that conversation belongs to, when one was supplied.</param>
/// <param name="TicketNumber">That ticket's number.</param>
/// <param name="TicketInteractionId">The interaction that now records the handler.</param>
/// <param name="HandledByUserId">The Ticketing user recorded as the interaction's handler — the first agent to be resolved on it, which may differ from <paramref name="UserId"/> after a transfer.</param>
public sealed record GenesysAgentContextResponse(
    string Outcome,
    string GenesysUserId,
    Guid UserId,
    string? UserName,
    string? DisplayName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<int> DepartmentIds,
    string? ConversationId,
    long? TicketId,
    string? TicketNumber,
    long? TicketInteractionId,
    Guid? HandledByUserId);

/// <summary>
/// The transport shape of a Secure Screen Pop launch request. Only
/// <paramref name="GenesysUserId"/> is required; the others choose the landing
/// page, in priority order: the conversation's ticket → <paramref name="TicketId"/>
/// → the customer lookup for <paramref name="CustomerPhone"/> → the Tickets list.
/// A value that does not resolve (a conversation with no ticket yet, an
/// unknown ticket id) falls through to the next choice rather than failing.
/// </summary>
/// <param name="GenesysUserId">Required. The agent's immutable Genesys User ID — the only value that identifies the agent. Must be mapped to an active TigerCS user.</param>
/// <param name="ConversationId">The Genesys conversation being worked. When it produced a ticket, the agent lands on that ticket and is recorded as the interaction's handler.</param>
/// <param name="TicketId">A TigerCS ticket id to open when the conversation names no ticket.</param>
/// <param name="CustomerPhone">The caller's number, opening Customer Lookup when no ticket applies.</param>
public sealed record GenesysScreenPopRequest(
    string? GenesysUserId,
    string? ConversationId = null,
    long? TicketId = null,
    string? CustomerPhone = null);

/// <summary>
/// The chatbot/voicebot's request for a verified customer's unit and project
/// details (<c>POST /api/genesys/customers/unit-details</c>).
/// </summary>
/// <param name="CustomerReference">Required. The verified customer: <c>crm:{id}</c> or the plain CRM customer id (the lookup's <c>externalCustomerId</c>). Checked server-side against CRM — it is never trusted on its own.</param>
/// <param name="PhoneNumber">Required. The verified number the customer was identified by (any <c>tel:</c>/+971/971 form). It is how CRM is asked which units the customer owns.</param>
/// <param name="UnitId">Optional. The CRM unit id the customer selected, from a previous <c>UnitSelectionRequired</c> answer. When absent, the customer's eligible units are returned instead.</param>
/// <param name="VerificationSessionId">Optional. A confirmed one-time-code / authenticated-user session from <c>POST /api/verification-sessions</c> for this unit and this service account. Required only for the private financial fields (sold price, registration cost); without it they are withheld and <c>financialDetailsStatus</c> says why. A phone number or customer id alone is never proof.</param>
public sealed record GenesysCustomerUnitDetailsRequest(
    string? CustomerReference,
    string? PhoneNumber,
    int? UnitId = null,
    Guid? VerificationSessionId = null);

/// <summary>An issued Screen Pop launch.</summary>
/// <param name="LaunchUrl">Open this in any browser or WebView. It carries a one-time token (valid once, for one hour) and nothing else — no username, password or user data.</param>
/// <param name="ExpiresAtUtc">When the launch stops being redeemable, in UTC.</param>
/// <param name="ExpiresInSeconds">The same deadline as a duration: 3600.</param>
/// <param name="TargetPath">The TigerCS Web page the agent will land on.</param>
/// <param name="TicketId">The ticket that page shows, when it shows one.</param>
public sealed record GenesysScreenPopResponse(
    string LaunchUrl,
    DateTime ExpiresAtUtc,
    int ExpiresInSeconds,
    string TargetPath,
    long? TicketId);

/// <summary>
/// Translates the transport records above into the normalized application
/// contracts. The one place Genesys' vocabulary is interpreted — an
/// unrecognized channel or event is rejected here with a clear message,
/// never guessed at deeper in the system.
/// </summary>
internal static class GenesysContractMapper
{
    internal static bool TryMap(GenesysInquiryRequest request, out GenesysInquiryDto inquiry, out string? error)
    {
        inquiry = null!;
        error = null;

        if (!TryParseChannel(request.Channel, out var channel))
        {
            error = $"Unsupported channel '{request.Channel}'. Expected Phone, LiveChat (or WebsiteChat / WebMessaging), WhatsApp, Instagram, Facebook (or FacebookMessenger) or SocialMedia.";
            return false;
        }

        inquiry = new GenesysInquiryDto(
            request.ConversationId,
            channel,
            Absent(request.InteractionId),
            Absent(request.ParticipantId),
            Absent(request.CommunicationId),
            Absent(request.Direction),
            Absent(request.CustomerPhone),
            Absent(request.CustomerName),
            Absent(request.CustomerEmail),
            Absent(request.CalledNumber),
            Absent(request.QueueId),
            Absent(request.QueueName),
            Absent(request.AgentId),
            Absent(request.AgentName),
            request.StartedAtUtc,
            request.DepartmentId,
            Absent(request.DepartmentCode),
            Absent(request.TowerName),
            Absent(request.UnitNumber),
            Absent(request.Subject),
            Map(request.RequestType),
            Absent(request.ServiceRequestId)?.Trim(),
            request.HumanFollowUpRequired);
        return true;
    }

    /// <summary>
    /// A blank optional value means "not supplied". A Genesys data action
    /// cannot leave a field out of its request template, so an unset
    /// Architect variable arrives as "" — which must never win over a value
    /// TigerCS found itself (a CRM customer name) or be parsed as a
    /// vocabulary word (a handoff mode).
    /// </summary>
    private static string? Absent(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The channel names Genesys may use for the one Live Chat channel, beside
    /// the normalized enum names themselves. "LiveChat" is the name the
    /// business and the channel catalogue use (<c>LIVE_CHAT</c>);
    /// "WebMessaging" is what Genesys Cloud calls its web chat. Both are the
    /// same channel as <see cref="GenesysChannel.WebsiteChat"/> — an alias,
    /// never a second channel.
    /// </summary>
    private static readonly Dictionary<string, GenesysChannel> ChannelAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LiveChat"] = GenesysChannel.WebsiteChat,
        ["WebMessaging"] = GenesysChannel.WebsiteChat,
        ["WebChat"] = GenesysChannel.WebsiteChat,
        // Facebook Messenger is the Facebook channel, not a second one.
        ["FacebookMessenger"] = GenesysChannel.Facebook,
        ["Messenger"] = GenesysChannel.Facebook,
        ["InstagramDirect"] = GenesysChannel.Instagram,
        // Social platforms without a channel of their own share the generic social-media channel.
        ["SocialDM"] = GenesysChannel.SocialMedia,
        ["Social"] = GenesysChannel.SocialMedia,
        ["Twitter"] = GenesysChannel.SocialMedia,
        ["X"] = GenesysChannel.SocialMedia,
        ["Telegram"] = GenesysChannel.SocialMedia
    };

    internal static bool TryParseChannel(string? value, out GenesysChannel channel)
    {
        var name = value?.Trim() ?? string.Empty;

        // "Live Chat", "live_chat" and "Facebook-Messenger" are the same words as LiveChat / FacebookMessenger: separators carry no meaning.
        var compact = string.Concat(name.Where(c => c is not (' ' or '_' or '-')));
        if (compact.Length > 0 && (ChannelAliases.TryGetValue(compact, out channel) || NamedEnum.TryParse(compact, out channel)))
        {
            return true;
        }

        channel = default;
        return false;
    }

    internal static GenesysTicketUpdateDto Map(GenesysTicketUpdateRequest request) => new(
        request.ConversationId,
        Absent(request.AgentId),
        Absent(request.AgentName),
        request.Ended is null
            ? null
            : new GenesysConversationEndUpdateDto(
                request.Ended.EndedAtUtc,
                Absent(request.Ended.EndReason),
                request.Ended.Transcript?
                    .Select(m => new GenesysTranscriptMessageDto(m.Sender, m.SentAtUtc, m.Body, m.SenderName, m.SenderId, m.ExternalMessageId))
                    .ToList()),
        request.Handoff is null
            ? null
            : new GenesysHandoffUpdateDto(
                request.Handoff.Required,
                request.Handoff.AgentAvailable,
                Absent(request.Handoff.Mode),
                Absent(request.Handoff.Reason),
                Absent(request.Handoff.Trigger),
                Absent(request.Handoff.WorkItemId),
                Absent(request.Handoff.AssignedAgentId)),
        request.StartedAtUtc,
        request.Routing is null
            ? null
            : new GenesysRoutingUpdateDto(
                Absent(request.Routing.QueueId), Absent(request.Routing.QueueName),
                Absent(request.Routing.AgentId), Absent(request.Routing.AgentName), request.Routing.ConnectedAtUtc),
        request.CustomerConfirmation is null
            ? null
            : new GenesysCustomerConfirmationUpdateDto(
                request.CustomerConfirmation.ConfirmedResolved,
                request.CustomerConfirmation.ConfirmedAtUtc,
                Absent(request.CustomerConfirmation.Note)),
        request.AwaitingCustomerReply,
        Map(request.RequestType),
        request.CustomerActive,
        request.ClosedByAi,
        request.Transcript?
            .Select(m => new GenesysTranscriptMessageDto(m.Sender, m.SentAtUtc, m.Body, m.SenderName, m.SenderId, m.ExternalMessageId))
            .ToList(),
        request.HumanFollowUpRequired);

    private static GenesysRequestTypeDto? Map(GenesysRequestTypePart? part) =>
        part is null ? null : new GenesysRequestTypeDto(part.RequestTypeId, Absent(part.Name));

    internal static GenesysAgentContextDto Map(GenesysAgentContextRequest request) =>
        new(request.GenesysUserId, request.AgentEmail, request.ConversationId);

    internal static GenesysScreenPopRequestDto Map(GenesysScreenPopRequest request) =>
        new(request.GenesysUserId, request.ConversationId, request.TicketId, request.CustomerPhone);

    internal static GenesysConversationEndDto Map(GenesysConversationEndRequest request) => new(
        request.ConversationId,
        request.EndedAtUtc,
        request.EndReason,
        request.AgentId,
        request.AgentName,
        request.Transcript?
            .Select(m => new GenesysTranscriptMessageDto(m.Sender, m.SentAtUtc, m.Body, m.SenderName, m.SenderId, m.ExternalMessageId))
            .ToList());
}
