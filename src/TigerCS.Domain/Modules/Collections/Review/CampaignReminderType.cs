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

    /// <summary>
    /// The existing Genesys contact lists a Manual send of <paramref name="type"/> may use. A reminder (Overdue, Current Month, Follow Up) may go to any of the three reminder lists
    /// (for example a Current Month reminder to the Follow Up list); a legal notice or legal case goes only to its own list, so legal wording never lands in a reminder list and vice versa.
    /// The campaign type's own list is first and is the default.
    /// </summary>
    public static IReadOnlyList<CampaignReminderType> ManualDestinations(CampaignReminderType type) => type switch
    {
        CampaignReminderType.CurrentMonth => [CampaignReminderType.CurrentMonth, CampaignReminderType.FollowUp, CampaignReminderType.Overdue],
        CampaignReminderType.FollowUp => [CampaignReminderType.FollowUp, CampaignReminderType.CurrentMonth, CampaignReminderType.Overdue],
        CampaignReminderType.Overdue => [CampaignReminderType.Overdue, CampaignReminderType.CurrentMonth, CampaignReminderType.FollowUp],
        _ => [type]
    };

    /// <summary>The chosen destination of a Manual send (blank = the campaign type's own list); false when the choice is not one of <see cref="ManualDestinations"/>.</summary>
    public static bool TryResolveManualDestination(CampaignReminderType type, string? requested, out CampaignReminderType destination)
    {
        destination = type;
        var text = requested?.Replace(" ", "").Trim();
        if (string.IsNullOrEmpty(text)) return true;
        foreach (var option in ManualDestinations(type))
            if (string.Equals(text, option.ToString(), StringComparison.OrdinalIgnoreCase)) { destination = option; return true; }
        return false;
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
