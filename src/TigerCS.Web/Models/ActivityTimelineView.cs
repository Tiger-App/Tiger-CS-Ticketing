using TigerCS.Application.Modules.Ticketing.ActivityTimeline;
using TigerCS.Web.Pages;

namespace TigerCS.Web.Models;

/// <summary>
/// Presentation-only helpers for the Ticket Details Activity tab: which filter chips an entry answers to, which icon and tone it wears and
/// how it is grouped by day. It reads an <see cref="ActivityEntry"/> and changes nothing about it — no event is added, dropped or reworded.
/// </summary>
public static class ActivityTimelineView
{
    public static readonly IReadOnlyList<(string Key, string Label)> Filters =
    [
        ("all", "All"), ("ai", "AI Agent"), ("human", "Human Agent"), ("assignment", "Assignment"), ("system", "System"),
    ];

    /// <summary>The filter keys an entry answers to (space separated). "All" is every entry and is not listed. Customer / unknown actors appear under All only.</summary>
    public static string FilterKeys(ActivityEntry entry)
    {
        var keys = new List<string>(2);
        var evt = entry.Event;
        if (evt is not null)
        {
            switch (evt.ActorType)
            {
                case TimelineActorTypes.Ai: keys.Add("ai"); break;
                case TimelineActorTypes.Agent or TimelineActorTypes.Supervisor: keys.Add("human"); break;
                case TimelineActorTypes.System: keys.Add("system"); break;
            }
        }
        else
        {
            keys.Add(entry.IsHuman ? "human" : "system");
        }

        if (entry.Kind == "transfer" || evt?.Category == "Ownership"
            || (evt?.EventType.Contains("Assign", StringComparison.OrdinalIgnoreCase) ?? false)
            || (evt?.EventType.Contains("Owner", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            keys.Add("assignment");
        }

        return string.Join(' ', keys);
    }

    public static string Icon(ActivityEntry entry) => entry.Kind switch
    {
        "created" => "ticket",
        "note" => "doc",
        "escalation" or "anomaly" => "alert",
        "closure" => "check-circle",
        "reopen" => "refresh",
        "approval" => "check",
        "handoff" => "users",
        "conversation" => entry.Event?.ActorType == TimelineActorTypes.Ai ? "chat" : "phone",
        "sla" => "clock",
        "transfer" => "user-round-cog",
        "notification" => "mail",
        _ => entry.Event?.EventType == "TicketCreated" ? "ticket" : "list",
    };

    public static string Tone(ActivityEntry entry) => entry.Kind switch
    {
        "escalation" or "anomaly" => "critical",
        "closure" or "reopen" or "sla" => "warning",
        "note" or "approval" or "handoff" or "notification" => "info",
        _ => entry.Event?.ActorType == TimelineActorTypes.Ai ? "info" : "quiet",
    };

    public static string Title(ActivityEntry entry) => entry.Event?.EventName
        ?? (entry.Kind == "note" ? "Note added" : entry.Description);

    public static string ActorName(ActivityEntry entry) => entry.Event?.ActorName ?? entry.Actor;

    /// <summary>"AI", "Agent", "Supervisor", "System" … for the small actor chip; null when the entry carries no typed actor.</summary>
    public static string? ActorType(ActivityEntry entry) => entry.Event?.ActorType;

    /// <summary>The calendar day (Asia/Dubai) an entry belongs to, for the day headings.</summary>
    public static DateOnly Day(ActivityEntry entry) => DateOnly.FromDateTime(TicketDisplay.ToUae(entry.TimestampUtc));

    public static string DayLabel(DateOnly day) => day.ToString("ddd, d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public static string Time(DateTime utc) => TicketDisplay.ToUae(utc).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether the entry has anything to show beyond its one-line row.</summary>
    public static bool HasMore(ActivityEntry entry)
    {
        var e = entry.Event;
        if (!string.IsNullOrEmpty(entry.Detail)) return true;
        if (e is null) return !string.IsNullOrEmpty(entry.Note);
        return !string.IsNullOrWhiteSpace(e.Description) || !string.IsNullOrWhiteSpace(e.Reason) || e.PreviousValue is not null
            || e.NewValue is not null || e.RecordedAtUtc is not null || !string.IsNullOrWhiteSpace(e.TimeNote) || e.Closure is not null
            || e.TicketInteractionId is not null;
    }
}
