namespace TigerCS.Domain.Modules.Collections.Review;

/// <summary>The five Genesys contact-list reminder types, mapped one-to-one onto the approved campaign stages.</summary>
public enum CampaignReminderType
{
    CurrentMonth = 1,
    FollowUp = 2,
    Overdue = 3,
    LegalNotice = 4,
    LegalCase = 5
}

public static class CampaignReminderTypes
{
    public static CampaignReminderType FromStage(CollectionsCampaignStage stage) => stage switch
    {
        CollectionsCampaignStage.CurrentMonthReminder => CampaignReminderType.CurrentMonth,
        CollectionsCampaignStage.FollowUpReminder => CampaignReminderType.FollowUp,
        CollectionsCampaignStage.OverdueReminder => CampaignReminderType.Overdue,
        CollectionsCampaignStage.LegalNotice => CampaignReminderType.LegalNotice,
        CollectionsCampaignStage.LegalReferral => CampaignReminderType.LegalCase,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    public static CollectionsCampaignStage ToStage(CampaignReminderType type) => type switch
    {
        CampaignReminderType.CurrentMonth => CollectionsCampaignStage.CurrentMonthReminder,
        CampaignReminderType.FollowUp => CollectionsCampaignStage.FollowUpReminder,
        CampaignReminderType.Overdue => CollectionsCampaignStage.OverdueReminder,
        CampaignReminderType.LegalNotice => CollectionsCampaignStage.LegalNotice,
        CampaignReminderType.LegalCase => CollectionsCampaignStage.LegalReferral,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>The name of the dedicated Genesys contact list every Manual send and Manual export goes to (a business-defined name, shown wherever the destination is shown).</summary>
    public const string ManualListName = "TigerCS Manual Collections";

    /// <summary>The configuration key (<c>Collections:GenesysOutbound:ContactListIds:Manual</c>) and the destination name of the dedicated Manual list.</summary>
    public const string ManualListKey = "Manual";

    /// <summary>
    /// True when <paramref name="requested"/> names the Manual list or nothing at all. Manual sending has exactly one destination: a request that names any other list is refused, never redirected.
    /// The campaign's ReminderType is a property of each unit and is not touched by where it is sent.
    /// </summary>
    public static bool IsManualList(string? requested)
    {
        var text = requested?.Replace(" ", "").Trim();
        return string.IsNullOrEmpty(text) || string.Equals(text, ManualListKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The stage's display name. It is used verbatim as the Genesys campaign name (and as the Campaigns page option text), so it must not be
    /// reworded or decorated anywhere else. The reminder type / stage enum stays the internal identifier.
    /// </summary>
    public static string StageName(CampaignReminderType type) => type switch
    {
        CampaignReminderType.CurrentMonth => "Current month reminder - day 14",
        CampaignReminderType.FollowUp => "Follow-up reminder - day 28",
        CampaignReminderType.Overdue => "Overdue reminder - day 1",
        CampaignReminderType.LegalNotice => "Legal notice - days 12 and 14",
        CampaignReminderType.LegalCase => "Internal Legal referral - day 30",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static string StageName(CollectionsCampaignStage stage) => StageName(FromStage(stage));

    /// <summary>Default ReminderType label written to Genesys; overridable by configuration after checking the supplied templates.</summary>
    public static string DefaultLabel(CampaignReminderType type) => type switch
    {
        CampaignReminderType.CurrentMonth => "Current Month",
        CampaignReminderType.FollowUp => "Follow Up",
        CampaignReminderType.Overdue => "Overdue",
        CampaignReminderType.LegalNotice => "Legal Notice",
        CampaignReminderType.LegalCase => "Legal Case",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
