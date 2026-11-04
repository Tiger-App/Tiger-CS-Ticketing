using System.Globalization;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Models;

/// <summary>How one transcript line is drawn.</summary>
public enum ChatBubbleKind
{
    /// <summary>The customer — right-aligned.</summary>
    Customer,

    /// <summary>The virtual agent / chatbot — left-aligned.</summary>
    Bot,

    /// <summary>A person on the team — left-aligned, with their own sender label.</summary>
    Human,

    /// <summary>A platform event (transfer, automated notice) — not conversation, drawn apart from the bubbles.</summary>
    System
}

/// <summary>One drawable line of a transcript: a bubble, or a system event. <see cref="DateHeading"/> precedes it when the UAE calendar day changes.</summary>
public sealed record ChatLine(ChatBubbleKind Kind, string Who, string Time, string Body, string? DateHeading);

/// <summary>
/// The Ticket Details Overview's Conversation History, as presentation only: it reshapes the interactions the Api already
/// returns and changes none of their content. Messages are drawn in the order the Api delivers them (the transcript's own
/// sequence), their text is untouched, and nothing is de-duplicated here — a customer who sends the same words twice sent two
/// messages. The only de-duplication in the system is the Api's, and only for records that carry the same source message id.
/// </summary>
public static class ConversationHistoryView
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static ChatBubbleKind KindOf(string? sender) => sender switch
    {
        "Customer" => ChatBubbleKind.Customer,
        "VirtualAgent" => ChatBubbleKind.Bot,
        "HumanAgent" or "Agent" => ChatBubbleKind.Human,
        _ => ChatBubbleKind.System
    };

    /// <summary>The sender as it is labelled above a message: a person by name, the bot as the virtual agent, the customer as such.</summary>
    public static string WhoLabel(TicketInteractionMessageDto message)
    {
        var name = string.IsNullOrWhiteSpace(message.SenderName) ? null : message.SenderName.Trim();
        return KindOf(message.Sender) switch
        {
            ChatBubbleKind.Customer => name ?? "Customer",
            ChatBubbleKind.Bot => name is null ? "Virtual agent" : $"{name} · Virtual agent",
            ChatBubbleKind.Human => name is null ? "Human agent" : $"{name} · Human agent",
            _ => name ?? "System"
        };
    }

    public static IReadOnlyList<ChatLine> Lines(TicketInteractionDto interaction)
    {
        var lines = new List<ChatLine>(interaction.Messages.Count);
        string? previousDay = null;
        foreach (var message in interaction.Messages)
        {
            var day = TicketDisplay.UaeDate(message.SentAtUtc);
            lines.Add(new ChatLine(
                KindOf(message.Sender), WhoLabel(message), TicketDisplay.UaeClock(message.SentAtUtc), message.Body,
                previousDay is not null && previousDay != day ? day : null));
            previousDay = day;
        }

        return lines;
    }

    /// <summary>"Started 22 Oct 2026 14:05 · Ended 14:12" in UAE time; "Not ended" while the conversation is live.</summary>
    public static string TimeWindow(TicketInteractionDto interaction)
    {
        var started = TicketDisplay.UaeDateTime(interaction.StartedAtUtc);
        if (interaction.EndedAtUtc is not { } ended)
        {
            return $"{started} → not ended (UAE)";
        }

        var sameDay = TicketDisplay.UaeDate(interaction.StartedAtUtc) == TicketDisplay.UaeDate(ended);
        return $"{started} → {(sameDay ? TicketDisplay.UaeClock(ended) : TicketDisplay.UaeDateTime(ended))} (UAE)";
    }

    /// <summary>Who handled the interaction, in a few words; null for an interaction Ticketing created itself.</summary>
    public static string? HandledBy(TicketInteractionDto interaction) => interaction.HandlingState switch
    {
        "Ai" => "AI (virtual agent)",
        "WaitingForHuman" => "AI · human requested, not yet taken",
        "Human" => string.IsNullOrWhiteSpace(interaction.AgentName) ? "Human agent" : $"Human agent · {interaction.AgentName.Trim()}",
        _ => null
    };

    /// <summary>"css key" for the handled-by badge.</summary>
    public static string HandledByCss(TicketInteractionDto interaction) => interaction.HandlingState switch
    {
        "Human" => "human",
        "WaitingForHuman" => "waiting",
        _ => "ai"
    };

    /// <summary>The handoff, in a few words; null when the interaction has no AI/human handling state to hand off from.</summary>
    public static string? HandoffStatus(TicketInteractionDto interaction) =>
        interaction.HandlingState is null
            ? null
            : interaction.Handoff is { } handoff ? $"Handoff: {TicketDisplay.HandoffStatusLabel(handoff.Status)}" : "No handoff";

    public static string MessageCount(TicketInteractionDto interaction) =>
        interaction.Messages.Count switch
        {
            0 => interaction.Status == "Ended" ? "No transcript" : "No messages yet",
            1 => "1 message",
            var n => $"{n} messages"
        };

    /// <summary>Which group opens by default: the only one, or the most recent when there are several.</summary>
    public static bool OpensByDefault(IReadOnlyList<TicketInteractionDto> interactions, int index) => index == interactions.Count - 1;
}
