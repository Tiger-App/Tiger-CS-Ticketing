using System.Globalization;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Models;

/// <summary>What the Payment tab shows, decided from the Api's answers — never inferred from roles in the browser.</summary>
public enum PaymentPanelState
{
    /// <summary>Not loaded yet: the tab fetches its content when opened (the loading state).</summary>
    Deferred,

    /// <summary>The customer is not identified in Tiger CRM, so there is no finance account to look up.</summary>
    NotCrmCustomer,

    /// <summary>The finance source has no linked account for this customer. No speculative record is shown.</summary>
    NoAccounts,

    /// <summary>Several accounts and none chosen yet: selection is required before any detail is shown.</summary>
    SelectAccount,

    Loaded,

    /// <summary>
    /// The unit-based view: the customer's units (CRM, or PACT / Leasing contracts found by phone) each with the PACT figures of that company + tower + apartment.
    /// Every unit states whether it has amounts, confirmed zero dues, no data, a failed / ambiguous match, or a source error.
    /// </summary>
    UnitSummary,

    /// <summary>
    /// A CRM customer with no financial source: the per-account source is not integrated, and EDSM
    /// has no verified mapping for this customer (the Api's NotMapped reason is shown).
    /// </summary>
    NotMapped,

    /// <summary>A PACT customer: EDSM's payment summary only — no instalments, transactions or reminders.</summary>
    EdsmSummary,

    /// <summary>The viewer lacks the Collections financial-read permission.</summary>
    Forbidden,

    /// <summary>Collections is switched off in this environment.</summary>
    Disabled,

    /// <summary>The finance source is unavailable — shown with Retry, never as AED 0.00.</summary>
    Unavailable,

    /// <summary>The Api could not be reached, or answered unexpectedly.</summary>
    Error
}

/// <summary>
/// Where the Payment panel's own navigation goes — the account/company
/// selector form, the Retry/Refresh links and the account cards. The
/// Customer Profile's tab is the default (<see cref="ForProfile"/>); the
/// New Ticket wizard supplies its own so the same panel, loaded by the same
/// loader, navigates within the wizard's carried state instead of leaving
/// it.
/// </summary>
/// <param name="SelectorAction">The GET form action of the account selector.</param>
/// <param name="HiddenFields">Hidden fields the selector form must carry (the profile's <c>tab=payment</c>; the wizard's whole step state).</param>
/// <param name="AccountParameter">The query parameter the selector posts the chosen account/company id as.</param>
/// <param name="AccountHref">A link that reloads the panel for the given account id (null for "no account chosen").</param>
/// <param name="AutoSubmit">True when the selector should submit itself on change (the profile's tab has its own fetch script; the wizard relies on this).</param>
public sealed record PaymentPanelLinks(
    string SelectorAction,
    IReadOnlyDictionary<string, string> HiddenFields,
    string AccountParameter,
    Func<string?, string> AccountHref,
    bool AutoSubmit = false)
{
    public static PaymentPanelLinks ForProfile(string customerKey)
    {
        var baseHref = $"/Customers/{Uri.EscapeDataString(customerKey)}?tab=payment";
        return new PaymentPanelLinks(
            $"/Customers/{Uri.EscapeDataString(customerKey)}",
            new Dictionary<string, string> { ["tab"] = "payment" },
            "account",
            account => account is null ? $"{baseHref}#payment" : $"{baseHref}&account={Uri.EscapeDataString(account)}#payment");
    }
}

/// <summary>
/// Caller-specific options for <see cref="TigerCS.Web.Services.CustomerPaymentPanelLoader"/>:
/// where the panel navigates, whether reminders may be sent from it, and —
/// when no account was chosen explicitly — which account/company to scope
/// to first (the one holding the unit the agent selected). Nothing here
/// changes what the Api answers or how its states are interpreted.
/// </summary>
/// <param name="Links">The panel's navigation; null means the Customer Profile's tab.</param>
/// <param name="AllowSending">False hides the Send Reminder forms (a read-only view, e.g. before ticket submission). The Api still enforces the permission either way.</param>
/// <param name="PreferredUnitNumber">For a CRM customer with several finance accounts and no explicit choice: pick the account whose unit number matches (trimmed, case-insensitive); otherwise selection is still required.</param>
/// <param name="PreferredExternalUnitId">For a PACT customer with several EDSM companies and no explicit choice: pick the company whose contracts include this PACT unit id; otherwise the first company.</param>
/// <param name="PreferredCrmUnitId">For a CRM customer: the CRM unit the agent is working on (New Ticket's selected unit). With no explicit choice the unit-based view opens on that unit; with none the customer's units are listed and one must be chosen.</param>
/// <param name="CrmLookupPhone">A phone the CRM customer is known by (New Ticket's typed number) when the unit-based lookup has no profile to take phones from. Unlike <c>LookupPhoneNumber</c> it changes nothing about the per-account / EDSM view.</param>
/// <param name="LookupPhoneNumber">A pre-ticket lookup context. The API verifies the selected identity and any CRM/PACT association again, without requiring a directory profile.</param>
public sealed record PaymentPanelOptions(
    PaymentPanelLinks? Links = null,
    bool AllowSending = true,
    string? PreferredUnitNumber = null,
    string? PreferredExternalUnitId = null,
    string? LookupPhoneNumber = null,
    long? PreferredCrmUnitId = null,
    string? CrmLookupPhone = null);

public sealed class CustomerPaymentPanel
{
    public const string DisabledCode = "CollectionsDisabled";

    public required string CustomerKey { get; init; }
    public long? CrmCustomerId { get; init; }
    public PaymentPanelState State { get; init; }

    private PaymentPanelLinks? _links;

    /// <summary>Where this panel's selector, Retry and account links go — the Customer Profile's tab unless the caller supplied its own.</summary>
    public PaymentPanelLinks Links
    {
        get => _links ??= PaymentPanelLinks.ForProfile(CustomerKey);
        init => _links = value;
    }

    /// <summary>Whether Send Reminder forms may render at all (the viewer's permission, <see cref="CanSend"/>, is still required on top).</summary>
    public bool AllowSending { get; init; } = true;

    /// <summary>True when the account/company shown was chosen by the caller's preferred unit rather than an explicit selection — the scope is then labelled for the agent.</summary>
    public bool ScopedByUnit { get; init; }

    public CollectionsOutstandingResponseDto? Outstanding { get; init; }

    /// <summary>The unit-based linking result, for <see cref="PaymentPanelState.UnitSummary"/> (also kept as the diagnosis when no figure could be shown).</summary>
    public CustomerUnitLinkResultDto? UnitLink { get; init; }

    /// <summary>The unit being shown: the explicit choice, or the only candidate. Null while a choice is still required.</summary>
    public LinkedUnitCandidateDto? SelectedUnit => UnitLink?.Candidates.FirstOrDefault(c => c.SelectionId == UnitLink.SelectedId);

    public string UnitLabel(LinkedUnitCandidateDto c) =>
        $"{(TigerCS.Domain.Modules.Collections.CollectionsTowerName.IsUsable(c.TowerName, c.TowerNumber) ? c.TowerName!.Trim() : c.ProjectName ?? (c.TowerNumber is null ? "—" : $"Tower {c.TowerNumber}"))} · {c.UnitNumber ?? "—"}{(c.CompanyId is { } company ? $" · company {company}" : "")}{(c.ContractNumber is { } contract ? $" · contract {contract}" : "")}";

    public static string UnitStatusText(string financialStatus) => financialStatus switch
    {
        "Available" => "Amounts due",
        "NoDues" => "Nothing due",
        "NoFinancialData" => "No financial data",
        "MatchFailed" => "Unit match needs review",
        "SourceError" => "Source error",
        _ => financialStatus
    };

    public static string UnitStatusCss(string financialStatus) => financialStatus switch
    {
        "Available" => "critical",
        "NoDues" => "ok",
        "SourceError" => "critical",
        "MatchFailed" => "pending",
        _ => "neutral"
    };

    /// <summary>The Payment tab's schedule status as text: Overdue, Due (this month) or Not yet due.</summary>
    public static string ScheduleStatusText(string status) => status switch { "NotYetDue" => "Not yet due", _ => status };

    public static string ScheduleStatusCss(string status) => status switch { "Overdue" => "critical", "Due" => "pending", _ => "neutral" };

    /// <summary>A UTC instant as Dubai time ("14 Oct 2026 12:00"), the zone every Collections date rule runs in. One shared conversion: <see cref="TigerCS.Web.Models.DubaiTime"/>.</summary>
    public static string DubaiTime(DateTime utc) => TigerCS.Web.Models.DubaiTime.Format(utc, "d MMM yyyy HH:mm");

    /// <summary>Plain words for the machine reasons the linking reports.</summary>
    public static string ReasonText(string reason) => reason switch
    {
        "ProjectMappingMissing" => "The CRM project has no verified PACT tower mapping.",
        "ProjectMappingAmbiguous" => "The CRM project maps to more than one PACT tower.",
        "ProjectMappingUnreadable" => "The CRM project mapping could not be read.",
        "CompanyMappingAmbiguous" => "The tower exists in more than one PACT company.",
        "UnitCodeInvalid" or "UnitCodeMissing" => "The unit has no valid PACT unit code.",
        "CompanyMissing" => "PACT gave no company for the contract.",
        "CompanyNotSupported" => "No receivables source exists for this PACT company.",
        "LeasingReceivablesSourceMissing" => "No company-7 (Leasing) receivables source exists.",
        "PactHoldsNoRecord" => "PACT holds no receivable record for this unit.",
        "CompanySnapshotNotLoaded" => "The company's PACT receivables are not loaded yet.",
        "SeveralPactAccounts" or "SeveralPactCustomers" => "More than one PACT account holds this unit.",
        "UnitInSeveralCompanies" => "The unit code exists in more than one PACT company.",
        "CrmCustomerAmbiguous" => "More than one CRM customer holds an eligible sale of this unit.",
        "CrmOwnershipConflict" => "CRM lists a different customer for this unit.",
        "ContactSourceConflict" => "CRM and PACT hold different contact details for the customer.",
        "PactUnavailable" or "UnitReceivablesSourceNotConfigured" => "The PACT receivables could not be read.",
        _ => reason
    };

    /// <summary>EDSM's payment summary, for <see cref="PaymentPanelState.EdsmSummary"/>.</summary>
    public CollectionsPaymentSummaryResponseDto? PaymentSummary { get; init; }

    /// <summary>The EDSM company (paired with the PACT tenant) shown, chosen with the account selector.</summary>
    public int? SelectedCompanyId { get; init; }

    public CollectionsCompanyPaymentSummaryDto? SelectedCompany =>
        PaymentSummary?.Companies.FirstOrDefault(c => c.CompanyId == SelectedCompanyId) ?? PaymentSummary?.Companies.FirstOrDefault();

    /// <summary>An EDSM field as an amount card value: "AED 812,500.00" when read, otherwise why not.</summary>
    public static string EdsmFieldAmount(CollectionsEdsmFieldDto? field, string currency) =>
        field is { Status: "Provided", Raw: { } raw } ? $"{currency} {raw.Trim()}" : field is null ? "Not provided" : EdsmField(field);

    public static string EdsmCompanyLabel(CollectionsCompanyPaymentSummaryDto c, string? tenantId) =>
        $"{c.CompanyName ?? $"Company {c.CompanyId}"} · {(c.BusinessModel switch { "Owned" => "owned", "Rented" => "rented", _ => "unknown" })} · tenant {tenantId ?? "—"}";

    /// <summary>The PACT customer key prefix (<c>ext:Pact:{tenantID}</c>) — the only identity EDSM's summary can be resolved for.</summary>
    public const string PactKeyPrefix = "ext:Pact:";

    public static bool IsPactCustomer(string customerKey) => customerKey.StartsWith(PactKeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the tab has a source to ask: a CRM customer (finance accounts) or a PACT customer (EDSM summary).</summary>
    public static bool HasPaymentSource(string customerKey, long? crmCustomerId) => crmCustomerId is not null || IsPactCustomer(customerKey);

    /// <summary>
    /// An EDSM summary field exactly as far as it is known: EDSM's own formatted
    /// string when it was read, or a short label for why there is none. Never
    /// "0.00" for a value that was not provided.
    /// </summary>
    public static string EdsmField(CollectionsEdsmFieldDto field) => field switch
    {
        { Status: "Provided", Raw: { } raw } => raw.Trim(),
        { Meaning: "ZeroOrLess" } => "None",
        { Meaning: "NotComputedForRented" } => "Not computed",
        { Status: "Missing" } => "Not provided",
        { Status: "Empty" } => "Blank",
        _ => "Not read"
    };

    public static string EdsmStatusText(string status) => status switch
    {
        "NotSupported" => "EDSM does not support this company",
        "BusinessRuleRejected" => "EDSM refused the request",
        "ValidationRejected" => "EDSM rejected the request parameters",
        "Unauthorized" => "EDSM rejected TigerCS's credentials",
        "InvalidResponse" => "EDSM's response could not be read",
        "Disabled" => "Not enabled",
        "NotMatchable" => "Cannot be matched",
        "DeadlineExceeded" => "EDSM did not answer in time",
        _ => "EDSM is unavailable"
    };
    public CollectionsAccountDto? SelectedAccount { get; init; }

    public CollectionsInstalmentsResponseDto? Instalments { get; init; }
    public CollectionsPaymentHistoryResponseDto? History { get; init; }
    public CollectionsReminderHistoryResponseDto? Reminders { get; init; }

    /// <summary>Eligible candidates for the selected account (one per open reminder window).</summary>
    public IReadOnlyList<CollectionsReminderCandidateDto> Candidates { get; init; } = [];

    /// <summary>False when the candidates route refused the viewer — the send action is then hidden.</summary>
    public bool CanSend { get; init; }

    /// <summary>A fresh key per rendered form, so a double submit of the same form queues once.</summary>
    public string SendIdempotencyKey { get; init; } = Guid.NewGuid().ToString("N");

    public string? Notice { get; init; }
    public bool NoticeIsError { get; init; }

    public bool IsStale => SelectedAccount?.DataStatus == "Stale" || Outstanding?.DataStatus == "Stale";

    /// <summary>Principal figures arrived. Amount due now may still be null when the source did not report penalties, fees or credits.</summary>
    public bool HasFigures => SelectedAccount?.RemainingPrincipalAmount is not null;

    public bool IsSettled => SelectedAccount is { AmountDueNow: 0m, RemainingPrincipalAmount: 0m };

    /// <summary>Web sends on SMS/email only; the voice bot is dialled by Genesys.</summary>
    public static IReadOnlyList<string> WebChannels(CollectionsReminderCandidateDto candidate) =>
        candidate.AvailableChannels.Where(c => c is "Sms" or "Email").ToList();

    public IReadOnlyList<CollectionsReminderCandidateDto> SendableCandidates =>
        CanSend && AllowSending && !IsStale ? Candidates.Where(c => WebChannels(c).Count > 0).ToList() : [];

    public string AccountLabel(CollectionsAccountDto a) =>
        $"{a.TowerName ?? "—"} · {a.UnitNumber ?? "—"} · {a.AccountId}";

    public static string Money(decimal? amount, string currency) =>
        amount is { } value ? $"{currency} {value.ToString("N2", CultureInfo.InvariantCulture)}" : "Unavailable";

    /// <summary>For a figure on an account whose principal did arrive: null means the source did not report it — said so, never "0.00".</summary>
    public static string Reported(decimal? amount, string currency) =>
        amount is null ? "Not provided by source" : Money(amount, currency);

    public static string Date(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Label(string value) => value switch
    {
        "OverdueMonthly" => "Overdue > 1 month",
        "CurrentMonth" => "Current month",
        "MonthEndFollowUp" => "Month-end follow-up",
        "PartiallyPaid" => "Partially paid",
        "DueToday" => "Due today",
        "NoAnswer" => "No answer",
        "VoiceBot" => "Voice bot",
        "Sms" => "SMS",
        "PromiseToPay" => "Promise to pay",
        "AlreadyPaid" => "Says already paid",
        "RequestedHuman" => "Asked for a person",
        "AiDisconnected" => "AI disconnected",
        "UnpaidPrincipalOlderThanOneCalendarMonth" => "unpaid principal older than one calendar month",
        "CurrentMonthUnpaidPrincipal" => "this month's unpaid principal",
        _ => value
    };

    /// <summary>Badge tone (<c>badge-pay-*</c>, built on the semantic palette tokens). Status is always also written as text.</summary>
    public static string StatusCss(string status) => status switch
    {
        "Paid" or "Posted" or "Delivered" or "Answered" or "Created" or "Reused" => "ok",
        "Overdue" or "Failed" or "NoAnswer" => "critical",
        "DueToday" or "Queued" or "Pending" => "pending",
        "Sent" or "PartiallyPaid" => "progress",
        _ => "neutral"
    };

    public static string ApiOutcomeMessage(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Forbidden => "You don't have permission to send payment reminders.",
        ApiOutcome.Conflict => "The amount or eligibility changed since this reminder was offered. Review the updated reminder and send again.",
        _ => $"The reminder was not queued: {detail ?? "the ticketing service could not be reached."}"
    };
}
