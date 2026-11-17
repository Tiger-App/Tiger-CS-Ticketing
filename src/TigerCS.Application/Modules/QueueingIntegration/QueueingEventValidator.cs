using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Domain.Modules.QueueingIntegration;

namespace TigerCS.Application.Modules.QueueingIntegration;

/// <summary>
/// Validates a queueing event. The envelope is the same for every event; what else is required depends on the event type, and every problem is
/// reported against the field's wire name so the Queueing System can fix exactly what is wrong.
/// </summary>
public static class QueueingEventValidator
{
    private const int IdMax = QueueingTicketLink.ExternalIdMaxLength;

    /// <summary>The fields each event type requires beyond the envelope (EventId, EventType, ExternalTicketId, EventSequence, OccurredAtUtc).</summary>
    public static readonly IReadOnlyDictionary<QueueingEventType, string[]> RequiredFields = new Dictionary<QueueingEventType, string[]>
    {
        [QueueingEventType.TicketCreated] = ["QueueNumber", "CompanyId", "TicketTypeId"],
        [QueueingEventType.TicketAssigned] = ["AssignedUserId"],
        [QueueingEventType.TicketReassigned] = ["AssignedUserId"],
        [QueueingEventType.TicketReturned] = [],
        [QueueingEventType.TicketCommentAdded] = ["Comment"],
        [QueueingEventType.TicketRecalled] = [],
        [QueueingEventType.TicketClosed] = [],
        [QueueingEventType.TicketCancelled] = []
    };

    /// <summary>A timestamp without an offset is read as UTC (the field is named UtcAt); one with an offset is converted.</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static bool TryValidate(
        QueueingEventRequest request, QueueingIntegrationOptions options, DateTime nowUtc,
        out QueueingEventMessage? message, out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new Dictionary<string, List<string>>();
        void Add(string field, string text)
        {
            if (!problems.TryGetValue(field, out var list))
            {
                problems[field] = list = [];
            }

            list.Add(text);
        }

        string? Text(string field, string? value, int max, bool required)
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (trimmed is null)
            {
                if (required)
                {
                    Add(field, $"{field} is required.");
                }

                return null;
            }

            if (trimmed.Length > max)
            {
                Add(field, $"{field} must be at most {max} characters.");
            }

            return trimmed;
        }

        var eventId = Text("EventId", request.EventId, QueueingEvent.EventIdMaxLength, required: true);
        var externalTicketId = Text("ExternalTicketId", request.ExternalTicketId, IdMax, required: true);

        QueueingEventType? type = null;
        if (string.IsNullOrWhiteSpace(request.EventType))
        {
            Add("EventType", "EventType is required.");
        }
        else if (Enum.TryParse<QueueingEventType>(request.EventType.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed) && !long.TryParse(request.EventType.Trim(), out _))
        {
            type = parsed;
        }
        else
        {
            Add("EventType", $"EventType '{request.EventType}' is not supported. Supported: {string.Join(", ", Enum.GetNames<QueueingEventType>())}.");
        }

        if (request.EventSequence is not { } sequence)
        {
            Add("EventSequence", "EventSequence is required.");
        }
        else if (sequence < 1)
        {
            Add("EventSequence", "EventSequence must be 1 or greater.");
        }

        if (request.OccurredAtUtc is not { } occurredAt || occurredAt == default)
        {
            Add("OccurredAtUtc", "OccurredAtUtc is required.");
        }
        else if (AsUtc(occurredAt) > nowUtc.AddSeconds(options.MaxClockSkewSeconds))
        {
            Add("OccurredAtUtc", "OccurredAtUtc is in the future.");
        }

        var queueNumber = Text("QueueNumber", request.QueueNumber, 100, required: type == QueueingEventType.TicketCreated);
        var companyId = Text("CompanyId", request.CompanyId, IdMax, required: type == QueueingEventType.TicketCreated);
        var ticketTypeId = Text("TicketTypeId", request.TicketTypeId, IdMax, required: type == QueueingEventType.TicketCreated);
        var assignedUserId = Text("AssignedUserId", request.AssignedUserId, IdMax,
            required: type is QueueingEventType.TicketAssigned or QueueingEventType.TicketReassigned);
        var performedBy = Text("PerformedByUserId", request.PerformedByUserId, IdMax, required: false);
        var counterId = Text("CounterId", request.CounterId, IdMax, required: false);
        var comment = Text("Comment", request.Comment, options.MaxCommentLength, required: type == QueueingEventType.TicketCommentAdded);
        var reason = Text("Reason", request.Reason, QueueingEvent.DetailMaxLength, required: false);
        var customerName = Text("CustomerName", request.CustomerName, 200, required: false);
        var customerPhone = Text("CustomerPhone", request.CustomerPhone, 32, required: false);
        var customerEmail = Text("CustomerEmail", request.CustomerEmail, 256, required: false);

        if (problems.Count > 0 || type is null)
        {
            message = null;
            errors = problems.ToDictionary(p => p.Key, p => p.Value.ToArray());
            return false;
        }

        errors = [];
        message = new QueueingEventMessage(
            eventId!, type.Value, externalTicketId!, request.EventSequence!.Value, AsUtc(request.OccurredAtUtc!.Value),
            queueNumber, companyId, ticketTypeId, customerName, customerPhone, customerEmail, assignedUserId, performedBy, counterId, comment, reason);
        return true;
    }
}
