namespace TigerCS.Domain.Modules.Collections.Review;

/// <summary>
/// The five Genesys contact-list reminder types, mapped one-to-one onto the approved campaign stages, plus <see cref="DirectSend"/>: a manual contact-list dispatch that belongs to no
/// campaign stage and has no stage schedule. DirectSend records are only ever listed and approved when explicitly asked for (by the Direct Send mode).
/// </summary>
public enum CampaignReminderType
{
    CurrentMonth = 1,
    FollowUp = 2,
    Overdue = 3,
    LegalNotice = 4,
    LegalCase = 5,
    DirectSend = 6
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
        _ => throw new ArgumentOutOfRangeException(nameof(type))   // DirectSend has no stage
    };

    /// <summary>The reminder types that follow the approved campaign stages (every type except <see cref="CampaignReminderType.DirectSend"/>).</summary>
    public static readonly IReadOnlyList<CampaignReminderType> StageTypes =
        [CampaignReminderType.CurrentMonth, CampaignReminderType.FollowUp, CampaignReminderType.Overdue, CampaignReminderType.LegalNotice, CampaignReminderType.LegalCase];

    /// <summary>
    /// The two existing Genesys contact lists a Direct Send may be uploaded to: Current Month or Follow Up (their ids come from the existing <c>ContactListIds</c> configuration). There is no list of
    /// its own. Anything else, including a blank choice, is refused: the person must choose.
    /// </summary>
    public static readonly IReadOnlyList<CampaignReminderType> DirectSendDestinations = [CampaignReminderType.CurrentMonth, CampaignReminderType.FollowUp];

    public static bool TryParseDirectSendDestination(string? value, out CampaignReminderType destination)
    {
        destination = default;
        var text = value?.Replace(" ", "").Trim();
        foreach (var option in DirectSendDestinations)
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
        CampaignReminderType.DirectSend => "Direct send - manual contact list",
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
        CampaignReminderType.DirectSend => "Direct Send",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
