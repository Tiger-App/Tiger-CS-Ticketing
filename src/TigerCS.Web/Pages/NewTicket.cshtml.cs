using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Web.Models;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages;

/// <summary>
/// One customer a verification source matched on Step 1 — a display card,
/// never raw identifiers. <see cref="Key"/> is the round-trippable selection
/// token ("crm", or "ext:{source}:{escaped external id}") carried in the
/// query string; everything the ticket ultimately persists still travels
/// through the packed unit selections, exactly as before the redesign.
///
/// <para>
/// <b>A unified card</b> (<see cref="IsUnified"/>) is the CRM Buyer and a
/// PACT customer that customer details and a unit match verified to be the same person
/// (see <see cref="NewTicketModel.LinkedPactCustomer"/>): <see cref="Key"/>
/// stays "crm", <see cref="Sources"/> names both, and
/// <see cref="LinkedPactCustomerId"/> keeps the PACT tenant id so both
/// identities (and both ticket histories) stay reachable. Neither identity
/// replaces the other — a ticket still persists exactly one of them,
/// decided by the unit the agent selects on Step 2.
/// </para>
/// </summary>
public sealed record NewTicketCandidate(
    string Key, string Source, string? DisplayName, string? PhoneNumber, string? Email, int UnitsCount,
    IReadOnlyList<string>? Sources = null, string? LinkedPactCustomerId = null)
{
    /// <summary>Every source that verified this card — the primary <see cref="Source"/> alone unless the card is unified.</summary>
    public IReadOnlyList<string> Sources { get; init; } = Sources ?? [Source];

    public bool IsUnified => Sources.Count > 1;

    public string PrimaryVerificationSource => Sources[0];
}

/// <summary>
/// "+ New Ticket" — redesigned as a four-step wizard (Customer → Property →
/// Issue → Review) with a persistent summary panel, wired to the same real
/// endpoints as before (POST /api/intake-records,
/// GET /api/intake-records/{id}/customer-lookup,
/// GET /api/crm/buyers?phoneNumber={phoneNumber}, GET /api/departments,
/// GET /api/categories, the customer-history/related-tickets reads, and
/// POST /api/tickets). The visible request taxonomy uses Request Category
/// (department) and a dependent Request Type, preserving routing/workflow IDs.
///
/// <para>
/// <b>Step 1 (Customer)</b> records the intake (channel + phone — the
/// intake carries no Department in this flow; Department moved to the Issue
/// step, and an intake without a Department searches every configured
/// lookup source, exactly per the existing department-aware rule) and shows
/// each matched customer as a compact card with their recent-ticket
/// awareness. <b>Step 2 (Property)</b> selects the unit — CRM Buyer units
/// and PACT/Tasleeh units keep their packed-selection integrity (ids and
/// display text always travel together), manual entry stays the fallback —
/// and surfaces the existing related-tickets advisory for the selected
/// unit. <b>Step 3 (Issue)</b> chooses Request Category → Request Type →
/// Priority → the request text. <b>Step 4 (Review)</b> shows a concise summary
/// before the one create action.
/// </para>
///
/// <para>
/// <b>Customer lookup still never gates ticket creation.</b> For every
/// source, Found / NotFound / Failed all leave the agent a path forward —
/// a not-found customer continues through manual entry, and no source's
/// failure ever hides another source's results. A matched PACT/Tasleeh
/// customer IS verified — against that source — and persists exactly as
/// before: the generic external identity plus the human-readable
/// Project/Unit snapshot. The phone number is free-form and travels
/// verbatim end to end (no pattern, no normalization — the one permitted
/// transformation stays inside PactCustomerHttpGateway).
/// </para>
///
/// <para>
/// State is carried step-to-step via the query string (GET) and hidden form
/// fields (POST) rather than server-side session/TempData — every step is a
/// plain, bookmarkable, refresh-safe request. Going Back never loses the
/// selection; changing the customer naturally clears the unit (the unit
/// selection tokens belong to the customer that produced them). Lookup
/// <i>results</i> are re-read on the steps that render them (idempotent
/// reads); the agent's explicit selections are never re-derived. The
/// carried CRM/external identifiers and snapshot text are
/// display/carry-forward only — POST /api/tickets re-validates everything
/// server-side regardless of what this page sends.
/// </para>
/// </summary>
public sealed class NewTicketModel(
    IntakeRecordsApiClient intakeClient,
    CustomerLookupApiClient customerLookupClient,
    CrmBuyerLookupApiClient crmBuyerLookupClient,
    DepartmentsApiClient departmentsClient,
    CategoriesApiClient categoriesClient,
    TicketsApiClient ticketsClient,
    CustomerHistoryApiClient customerHistoryClient,
    RequestTypesApiClient? requestTypesClient = null,
    ChannelsApiClient? channelsClient = null,
    CustomerPaymentPanelLoader? paymentPanelLoader = null,
    TimeProvider? timeProvider = null,
    CustomerContactApiClient? customerContactClient = null,
    ProjectCatalogApiClient? projectCatalogClient = null) : PageModel
{
    public const string StepCustomer = "customer";
    public const string StepProperty = "property";
    public const string StepIssue = "issue";
    public const string StepReview = "review";
    public const string StepDone = "done";

    /// <summary>How many recent tickets the Step 1 existing-tickets awareness shows per matched customer — a glance, never the full history (that lives in the Customer Workspace).</summary>
    public const int CandidateHistoryLimit = 3;

    public string Step { get; private set; } = StepCustomer;
    public string? ErrorMessage { get; private set; }

    [BindProperty] public IntakeInput Intake { get; set; } = new();
    [BindProperty] public CreateStepInput CreateStep { get; set; } = new();

    public long? IntakeRecordId { get; private set; }
    public string? PhoneNumber { get; private set; }

    /// <summary>
    /// The channels Step 1 offers — the ACTIVE rows of the configured
    /// channel catalogue (Admin → Configuration → Channels) from
    /// <c>GET /api/channels</c>, already ordered by display order then name.
    /// Never a hard-coded list: a channel an administrator deactivates
    /// disappears from here (and only from here), a new one appears without
    /// a release.
    /// </summary>
    public IReadOnlyList<ChannelDto> Channels { get; private set; } = [];

    /// <summary>Set only when the Channels API call itself failed — distinct from "loaded successfully but empty".</summary>
    public string? ChannelsErrorMessage { get; private set; }

    /// <summary>
    /// The channel the Search form currently names, resolved against the
    /// loaded active channels by id (what the picker posts) or by code (what
    /// older callers/tests post). Null when nothing valid is selected.
    /// </summary>
    public ChannelDto? SelectedChannel => ResolveChannel(Intake.ChannelId);

    /// <summary>Whether the phone field is mandatory — the selected channel's own <c>RequiresPhone</c> configuration, never a channel-name comparison. Defaults to required until a channel is resolved.</summary>
    public bool PhoneRequired => SelectedChannel?.RequiresPhone ?? true;

    /// <summary>The Step 1 customer selection token: "crm", "ext:{source}:{escaped id}", or "manual". Null until the agent picks (or falls back to manual entry).</summary>
    public string? CustomerKey { get; private set; }

    /// <summary>The real CRM Buyer Lookup match the agent selected on Step 2 — all four set together, or none. A distinct identifier space from the older CRM-unit-number cache (UnitReferenceId/ContactReferenceId).</summary>
    public int? CrmBuyerCustomerId { get; private set; }
    public int? CrmBuyerLeadId { get; private set; }
    public int? CrmBuyerUnitId { get; private set; }
    public int? CrmBuyerProjectId { get; private set; }

    /// <summary>Display-only snapshot text carried forward from Step 2 for the summary panel and review. Never trusted as anything but a label — ticket creation re-validates the CRM Buyer ids server-side.</summary>
    public string? CrmBuyerCustomerName { get; private set; }
    public string? CrmBuyerProjectName { get; private set; }
    public string? CrmBuyerUnitNumber { get; private set; }

    /// <summary>
    /// The one CRM customer <c>GET /api/crm/buyers?phoneNumber=</c> matched —
    /// business rule: a CRM phone number belongs to exactly one customer, so
    /// this page is built for exactly one, never a list to disambiguate
    /// between. Carries every eligible Sold/Contract unit that customer owns
    /// — never auto-selected. Null until Step 1 has actually run a lookup, or
    /// when it found no match.
    /// </summary>
    public CrmBuyerMatchDto? CrmBuyerMatch { get; private set; }

    /// <summary>
    /// True when CRM answered with more than one distinct customer for this
    /// phone number (409 Conflict) — a CRM data-integrity conflict
    /// (<c>CrmBuyerLookupAppService.GetBuyerByPhoneAsync</c>'s
    /// <c>AmbiguousCustomerMatch</c> outcome), not "no match". No customer or
    /// unit is ever auto-selected for this case; the agent falls back to
    /// manual entry exactly as for no match, but sees a distinct message
    /// naming the conflict.
    /// </summary>
    public bool CrmBuyerAmbiguousMatch { get; private set; }

    /// <summary>True when CRM Buyer Lookup itself could not be reached/answered (outage, timeout, misconfiguration) rather than answering with zero matches — same manual-entry consequence as NotFound, but presented as "temporarily unavailable", never as "not found".</summary>
    public bool CrmBuyerLookupUnavailable { get; private set; }

    /// <summary>
    /// The department-aware customer lookup response — one entry per source
    /// the intake enables (all three for this flow's department-less
    /// intakes), each independently Found/NotFound/Failed so one source's
    /// outage never hides another's result. Null until Step 1 has run, or
    /// when the lookup call itself failed
    /// (<see cref="CustomerLookupUnavailable"/>).
    /// </summary>
    public CustomerLookupResultDto? CustomerLookup { get; private set; }

    /// <summary>True when the generic customer-lookup call itself failed — the page fails open to the previous CRM-only behavior (<see cref="CrmParticipates"/> stays true) rather than losing CRM lookup, and never blocks the wizard.</summary>
    public bool CustomerLookupUnavailable { get; private set; }

    /// <summary>
    /// Whether the Crm source participates for this intake — read from the
    /// department-aware lookup response, never from a hard-coded department
    /// check. Fails open to true when that response is unavailable, so the
    /// real CRM Buyer Lookup never silently disappears on a lookup-config
    /// outage.
    /// </summary>
    public bool CrmParticipates { get; private set; } = true;

    /// <summary>
    /// The non-CRM source entries (PACT/Tasleeh) straight from the
    /// department-aware response. The response's own Crm entry is used only
    /// as the participation signal for the real CRM Buyer Lookup — its
    /// customers are not rendered, because that generic Crm leg is still
    /// fixture-backed (Crm:Provider=Mock) while Buyer Lookup is the real CRM
    /// integration (see this type's remarks).
    /// </summary>
    private readonly DateOnly _selectionDate = PactContractActivity.TodayInDubai(timeProvider ?? TimeProvider.System);

    private IReadOnlyList<CustomerLookupUnitDto> SelectablePactUnits(CustomerLookupCustomerDto customer) =>
        customer.Units.Where(u => u.ContractEndDate is not { } end || end >= _selectionDate)
            .DistinctBy(u => u.ExternalUnitId).ToList();

    public IReadOnlyList<CustomerLookupSourceResultDto> ExternalLookupSources =>
        CustomerLookup?.Sources.Where(s => !string.Equals(s.Source, "Crm", StringComparison.Ordinal))
            .Select(s => s.Source != "Pact" ? s : s with
            {
                Customers = s.Customers.Select(c => c with { Units = SelectablePactUnits(c) }).ToList()
            }).ToList() ?? [];

    /// <summary>True when the Department has zero lookup sources configured — nothing was searched (never a silent fall-back to "search everything"), and the agent continues with manual entry.</summary>
    public bool NoLookupSourcesConfigured => CustomerLookup is { Sources.Count: 0 };

    /// <summary>Every matched customer, as Step 1 cards — the CRM Buyer (at most one) plus each PACT/Tasleeh Found customer. Empty until a search has run, or when nothing matched.</summary>
    public IReadOnlyList<NewTicketCandidate> Candidates { get; private set; } = [];

    /// <summary>
    /// Step 1's existing-ticket awareness, keyed by candidate: each matched
    /// customer's ticket counts and a few most-recent tickets (active first),
    /// from the existing identity-keyed Customer History reads — one bounded
    /// call per matched customer, never per ticket, and never a display-name
    /// or phone match. A candidate whose history call failed simply has no
    /// entry; the wizard never blocks on it.
    /// </summary>
    public IReadOnlyDictionary<string, CustomerHistoryDto> CandidateHistories { get; private set; } =
        new Dictionary<string, CustomerHistoryDto>();

    /// <summary>The Step 1 card the agent selected, re-resolved from the (re-read) lookup results on Step 2. Null on the manual path.</summary>
    public NewTicketCandidate? SelectedCandidate { get; private set; }

    /// <summary>
    /// The PACT customer unified with the CRM Buyer on one card, when the
    /// two share a <b>verified unit link</b>: at least one of the Buyer's
    /// CRM units and one of the PACT customer's current contracts name the
    /// same unit number in the same project (compared trimmed,
    /// case-insensitively — see <see cref="UnitsLink"/>). A shared phone
    /// number alone never unifies: the phone is what produced both matches
    /// in the first place, so it proves nothing about identity. Null when
    /// no PACT customer links. Both identities are kept intact: the CRM
    /// Buyer ids and the PACT tenant id each travel exactly as before, and
    /// the unit the agent selects on Step 2 decides which one the ticket
    /// persists.
    /// </summary>
    public CustomerLookupCustomerDto? LinkedPactCustomer { get; private set; }

    /// <summary>The linked PACT customer's current units that no CRM unit matches — offered on Step 2 under the unified card, selectable through the external (PACT) path. Empty when there is no link.</summary>
    public IReadOnlyList<CustomerLookupUnitDto> LinkedPactOnlyUnits { get; private set; } = [];

    public IReadOnlyList<CustomerLookupUnitDto> LinkedPactSelectableUnits =>
        LinkedPactCustomer is { } pact ? SelectablePactUnits(pact) : [];

    public IReadOnlyList<CrmBuyerUnitDto> CrmOnlySelectableUnits =>
        CrmBuyerMatch?.Units.Where(crm => !LinkedPactSelectableUnits.Any(pact => UnitsLink(crm, pact))).ToList() ?? [];

    /// <summary>The PACT selection token of the linked customer ("ext:Pact:{tenant id}"), for the workspace hand-off and the card's identity line.</summary>
    public string? LinkedPactCustomerKey =>
        LinkedPactCustomer is { } linked ? $"ext:Pact:{Uri.EscapeDataString(linked.ExternalCustomerId)}" : null;

    /// <summary>True when this CRM unit is also one of the linked PACT customer's current contracts — shown as a note on the unit row; the CRM identity is still what the selection persists.</summary>
    public bool IsLinkedToPact(CrmBuyerUnitDto unit) =>
        LinkedPactCustomer is { } linked && linked.Units.Any(pactUnit => UnitsLink(unit, pactUnit));

    /// <summary>
    /// The verified unit link: same unit number AND same project name,
    /// compared trimmed and case-insensitively. Both are required — a unit
    /// number alone ("1506") repeats across projects, and a project alone
    /// is shared by every unit in it.
    /// </summary>
    public static bool UnitsLink(CrmBuyerUnitDto crmUnit, CustomerLookupUnitDto pactUnit) =>
        CustomerIdentityLinker.Same(crmUnit.UnitNumber, pactUnit.UnitNumber)
        && (CustomerIdentityLinker.Same(crmUnit.ProjectName, pactUnit.PropertyName)
            || CustomerIdentityLinker.Same(crmUnit.ProjectArabicName, pactUnit.PropertyName));

    /// <summary>
    /// The PACT/Tasleeh customer+unit the agent selected on Step 2, packed
    /// exactly like the CRM value (see <see cref="OnPostUseExternalUnit"/>)
    /// and carried step-to-step via query string/hidden field like every
    /// other wizard value. Unpacked into the External* properties below.
    /// Display/carry-forward only past the prefill — the persisted record of
    /// the selection is the external identity plus the manual Project/Unit
    /// snapshot (see this type's remarks).
    /// </summary>
    public string? ExternalSelection { get; private set; }

    /// <summary>"Pact" or "Tasleeh" — which source verified the Step 2 selection; persisted as the Ticket's CustomerVerificationSource.</summary>
    public string? ExternalSource { get; private set; }
    /// <summary>The source's own customer identifier (for PACT, its tenantID) — persisted on the Ticket as ExternalCustomerId (an external identifier only, never a local reference).</summary>
    public string? ExternalCustomerId { get; private set; }
    /// <summary>The source's own identifier for the selected unit (for PACT, its unitID) — persisted on the Ticket as ExternalUnitId.</summary>
    public string? ExternalUnitId { get; private set; }
    /// <summary>The source's own name for the customer (PACT's <c>customerName</c>) — carried through the wizard and snapshotted on the Ticket as ExternalCustomerName.</summary>
    public string? ExternalCustomerName { get; private set; }
    /// <summary>The source's own email for the customer (PACT's <c>customerEmail</c>) — snapshotted on the Ticket as ExternalCustomerEmail. Null when the source holds none; never invented.</summary>
    public string? ExternalCustomerEmail { get; private set; }
    public string? ExternalProjectName { get; private set; }
    public string? ExternalUnitNumber { get; private set; }

    /// <summary>The manually-entered property, carried as wizard values once Step 2's manual form is submitted (the manual path's equivalent of the packed selections).</summary>
    public string? ManualProjectName { get; private set; }
    public string? ManualUnitNumber { get; private set; }

    /// <summary>The Department directory the Issue step's dropdown renders — real, existing Departments only, never a typed id.</summary>
    public IReadOnlyCollection<DepartmentDto> Departments { get; private set; } = [];

    /// <summary>Set only when the Departments API call itself failed — the dropdown still renders (empty), the page just says so.</summary>
    public string? DepartmentsErrorMessage { get; private set; }

    /// <summary>
    /// The active Categories the Issue step offers — scoped
    /// to the selected Department when one is chosen, otherwise every active
    /// Category grouped by its Department (the pre-redesign behavior,
    /// preserved so a Department choice is a narrowing aid, never a gate).
    /// </summary>
    public IReadOnlyCollection<CategoryDto> Categories { get; private set; } = [];

    /// <summary>Set only when the Categories API call itself failed — distinct from "loaded successfully but empty".</summary>
    public string? CategoriesErrorMessage { get; private set; }

    /// <summary>
    /// Duplicate-ticket awareness: the selected customer's recent tickets
    /// for the selected unit (same verified identity, same unit snapshot,
    /// active tickets first), shown as the advisory "Related tickets" panel
    /// on Step 2 once a unit is selected. Advisory only — creation is never
    /// blocked; the agent always keeps "Continue with New Ticket". Null when
    /// there is no verified identity to key on (plain manual entry — a
    /// manual customer must never be associated with someone else's tickets)
    /// or when the lookup itself failed; a failure never blocks the wizard.
    /// </summary>
    public CustomerHistoryDto? RelatedTickets { get; private set; }

    // ---- Manual customer entry / Edit customer (Step 1) — Tiger Ticketing's own record, keyed by the intake phone ----

    /// <summary>The name/e-mail an agent already saved for this phone (Tiger Ticketing's own record), or null. Wins over any looked-up name in the summary and cards: it is what the agent told us, and never changes verification.</summary>
    public CustomerContactDto? SavedContact { get; private set; }

    /// <summary>Step 1 shows the Customer Name / Email / Phone form instead of the lookup results — manual entry, or Edit on an existing customer.</summary>
    public bool ShowContactForm { get; private set; }

    /// <summary>True when the form was opened to edit an existing (looked-up) customer rather than to enter a new one by hand.</summary>
    public bool ContactFormIsEdit { get; private set; }

    /// <summary>The Edit button on a matched customer's card is offered only when the contact endpoint is available.</summary>
    public bool CanEditContact => customerContactClient is not null;

    /// <summary>A flash after Save on an existing customer.</summary>
    public bool ContactSaved { get; private set; }

    public string? ContactFormName { get; private set; }
    public string? ContactFormEmail { get; private set; }
    public string? ContactNameError { get; private set; }
    public string? ContactEmailError { get; private set; }

    // ---- Step 2 project / unit options (Tiger Ticketing's own known projects and units) ----

    public IReadOnlyList<ProjectOptionDto> ProjectOptions { get; private set; } = [];
    public IReadOnlyList<string> UnitOptions { get; private set; } = [];

    public long? CreatedTicketId { get; private set; }
    public string? CreatedTicketNumber { get; private set; }

    // ---- Payments & Fines — shown before ticket submission (Step 2 once a
    // verified unit is selected, and again read-only on Step 4), for the
    // verified identity the selection carries: a CRM Buyer's finance
    // accounts, or a PACT tenant's EDSM payment summary. Loaded through the
    // same CustomerPaymentPanelLoader the Customer Profile's Payment tab
    // uses, so every figure, transaction, fine and availability state is
    // the Api's own answer — never inferred, never zero-filled — and it
    // never depends on the customer having any previous ticket. ----

    /// <summary>The payment panel for the selected unit's verified identity. Null on the manual path, before a unit is selected, or when the loader is not configured.</summary>
    public CustomerPaymentPanel? PaymentPanel { get; private set; }

    /// <summary>
    /// Under the unified CRM + PACT card, the OTHER identity's payment
    /// panel (the PACT tenant's EDSM summary when a CRM unit is selected;
    /// the CRM finance account when a PACT unit is selected), each scoped
    /// to the account/company holding the selected unit where the source
    /// confirms one. Both identities' verified mappings stay exactly as the
    /// Api resolves them — nothing is cross-wired between the two.
    /// </summary>
    public CustomerPaymentPanel? LinkedPaymentPanel { get; private set; }

    /// <summary>The agent's explicit account/company choice for <see cref="PaymentPanel"/> (query string <c>paymentAccount</c>); null scopes to the selected unit.</summary>
    public string? PaymentAccount { get; private set; }

    /// <summary>The agent's explicit account/company choice for <see cref="LinkedPaymentPanel"/> (query string <c>linkedPaymentAccount</c>).</summary>
    public string? LinkedPaymentAccount { get; private set; }

    /// <summary>Which identity <see cref="PaymentPanel"/> describes — "Crm", "Pact", or another external source's name.</summary>
    public string? PaymentPanelSource => PaymentPanel is { State: PaymentPanelState.EdsmSummary }
        || PaymentPanel is { CustomerKey: var key } && CustomerPaymentPanel.IsPactCustomer(key)
            ? "Pact" : CrmBuyerCustomerId is not null ? "Crm" : ExternalSource;

    /// <summary>Which identity <see cref="LinkedPaymentPanel"/> describes — the unified card's other half.</summary>
    public string? LinkedPaymentPanelSource => LinkedPaymentPanel is null ? null
        : CustomerPaymentPanel.IsPactCustomer(LinkedPaymentPanel.CustomerKey) ? "Pact" : "Crm";

    // ---- Summary panel (the right-hand sticky panel) — selected display
    // values only, never technical ids, with "Not selected yet" placeholders
    // rendered by the view when these are null. ----

    public bool HasUnitSelection =>
        CrmBuyerUnitId is not null
        || ExternalSelection is not null
        || (!string.IsNullOrWhiteSpace(ManualProjectName) && !string.IsNullOrWhiteSpace(ManualUnitNumber));

    public string? SummaryCustomerName =>
        SavedContact?.Name
        ?? CrmBuyerCustomerName
        ?? ExternalCustomerName
        ?? SelectedCandidate?.DisplayName
        ?? (CustomerKey == "manual" ? "Manual entry" : null);

    /// <summary>The verification-source label for the summary/review — "Tiger CRM"/"PACT"/"Tasleeh", or "Manual entry" (manual entry is not externally verified; the wording never says "not verified"). A PACT unit selected under the unified CRM card is a PACT selection: the ticket persists the PACT identity, and the label says so.</summary>
    public string? SummarySourceKey =>
        CrmBuyerUnitId is not null ? "Crm"
        : ExternalSource ?? (CustomerKey == "crm" ? "Crm"
        : CustomerKey is { } key && key.StartsWith("ext:", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(key.Split(':', 3)[1])
            : CustomerKey == "manual" ? "Manual" : null);

    public string? SummaryVerificationLabel => SummarySourceKey switch
    {
        "Manual" => "Manual entry",
        "Crm" when LinkedPactCustomer is not null
            || PaymentPanel?.PaymentSummary is { MappingStatus: "Mapped", PactTenantId: not null } => "PACT · Tiger CRM",
        "Pact" when LinkedPactCustomer is not null && CrmBuyerMatch is not null => "PACT · Tiger CRM",
        { } source => TicketDisplay.LookupSourceLabel(source),
        _ => null
    };

    public string? SummaryCustomerEmail => SavedContact?.Email ?? ExternalCustomerEmail ?? SelectedCandidate?.Email;

    public string? SummaryProjectName => CrmBuyerProjectName ?? ExternalProjectName ?? ManualProjectName;
    public string? SummaryUnitNumber => CrmBuyerUnitNumber ?? ExternalUnitNumber ?? ManualUnitNumber;

    /// <summary>
    /// Administration / Workflow Designer phase — the configured request
    /// types the Issue step may attach to the ticket, scoped to the selected
    /// Request Category (department). The child picker combines these with
    /// legacy routing entries; a configured child pins the ticket to its
    /// published workflow version and drives assignment, approvals and SLA.
    /// The Api re-validates that the type belongs to the routed department.
    /// </summary>
    public IReadOnlyList<RequestTypeOptionDto> RequestTypeOptions { get; private set; } = [];

    public string? RequestTypesErrorMessage { get; private set; }

    public IReadOnlyList<NewTicketRequestTypeChoice> RequestTypeChoices =>
        RequestTypesErrorMessage is null
            ? NewTicketRequestTypePicker.Build(CreateStep.DepartmentId, Categories, RequestTypeOptions)
            : [];

    public string? SummaryRequestTypeName => SelectedRequestType?.Name ?? SelectedCategory?.Name;

    public RequestTypeOptionDto? SelectedRequestType =>
        CreateStep.RequestTypeId is { } requestTypeId ? RequestTypeOptions.FirstOrDefault(r => r.RequestTypeId == requestTypeId) : null;

    public string DepartmentNameOf(int departmentId) =>
        Departments.FirstOrDefault(d => d.DepartmentId == departmentId)?.Name ?? TicketDisplay.UnknownDepartmentLabel;

    public CategoryDto? SelectedCategory =>
        CreateStep.CategoryId is { } categoryId ? Categories.FirstOrDefault(c => c.CategoryId == categoryId) : null;

    public string? SummaryDepartmentName =>
        SelectedCategory?.DepartmentName
        ?? (CreateStep.DepartmentId is { } departmentId
            ? Departments.FirstOrDefault(d => d.DepartmentId == departmentId)?.Name
            : null);

    /// <summary>1-based index of the current step for the progress stepper (Review and Done both render as step 4).</summary>
    public int StepIndex => Step switch
    {
        StepProperty => 2,
        StepIssue => 3,
        StepReview or StepDone => 4,
        _ => 1
    };

    public async Task<IActionResult> OnGetAsync(
        string? step, long? intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber,
        int? departmentId, long? createdTicketId, string? createdTicketNumber,
        CancellationToken cancellationToken, string? paymentAccount = null, string? linkedPaymentAccount = null,
        bool editContact = false, bool contactSaved = false)
    {
        ApplyWizardState(
            step ?? StepCustomer, intakeRecordId, phoneNumber, customer,
            crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
            crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
            externalSelection, manualProjectName, manualUnitNumber);
        PaymentAccount = string.IsNullOrWhiteSpace(paymentAccount) ? null : paymentAccount;
        LinkedPaymentAccount = string.IsNullOrWhiteSpace(linkedPaymentAccount) ? null : linkedPaymentAccount;

        ContactSaved = contactSaved;
        ShowContactForm = Step == StepCustomer && (CustomerKey == "manual" || editContact);
        ContactFormIsEdit = Step == StepCustomer && CustomerKey != "manual" && editContact;

        switch (Step)
        {
            case StepProperty:
                // A manually entered customer needs a name before a property: if none was saved for this phone yet (deep link, or
                // "Enter property manually" from a looked-up customer), collect it first.
                if (CustomerKey == "manual" && customerContactClient is not null && !string.IsNullOrWhiteSpace(PhoneNumber))
                {
                    await LoadSavedContactAsync(cancellationToken);
                    if (SavedContact is null)
                    {
                        return RedirectToPage(new { step = StepCustomer, intakeRecordId, phoneNumber, customer = "manual" });
                    }
                }

                CreateStep.ManualProjectName ??= ManualProjectName;
                CreateStep.ManualUnitNumber ??= ManualUnitNumber;
                await LoadPropertyStepAsync(cancellationToken);
                await LoadPaymentPanelsAsync(readOnly: false, cancellationToken);
                break;

            case StepIssue:
                await LoadSavedContactAsync(cancellationToken);
                CreateStep.DepartmentId = departmentId;
                CreateStep.ManualProjectName = ManualProjectName;
                CreateStep.ManualUnitNumber = ManualUnitNumber;
                await LoadIssueStepAsync(cancellationToken);
                break;

            case StepReview:
                // Review renders only from the Issue form's own POST (the
                // entered text never travels through a URL) — a direct GET
                // lands back on the Issue step with the selection intact.
                return RedirectToPage(new
                {
                    step = StepIssue, intakeRecordId, phoneNumber, customer,
                    crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
                    crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
                    externalSelection, manualProjectName, manualUnitNumber, departmentId
                });

            case StepDone:
                if (createdTicketId is null || string.IsNullOrWhiteSpace(createdTicketNumber))
                {
                    return RedirectToPage();
                }

                CreatedTicketId = createdTicketId;
                CreatedTicketNumber = createdTicketNumber;
                break;

            default:
                await LoadCustomerStepAsync(cancellationToken);
                break;
        }

        return Page();
    }

    // ---------------------------------------------------------------
    // Step 1 — Customer
    // ---------------------------------------------------------------

    /// <summary>
    /// Records the interaction (channel + phone, verbatim) as the
    /// IntakeRecord every ticket requires, then returns to Step 1 to show
    /// the lookup results. The intake carries no Department in this flow
    /// (Department moved to the Issue step), so the department-aware lookup
    /// searches every configured source — the existing no-department rule,
    /// not a new one.
    /// </summary>
    public async Task<IActionResult> OnPostIntakeAsync(CancellationToken cancellationToken)
    {
        // Deliberately NOT `ModelState.IsValid`: CreateStep is a co-bound
        // [BindProperty] whose [Required] members (CategoryId/PriorityId/
        // RequestSummary) the Search form never posts, so the page-wide
        // ModelState is invalid on every Step 1 POST and a global gate
        // would silently re-render an empty Step 1 for a perfectly valid
        // phone search. Validate only what this form actually submits, and
        // report the failure visibly — this step never fails silently.
        //
        // The channel is validated against CONFIGURATION: it must be one
        // of the active channels the picker was built from, and its own
        // RequiresPhone setting decides whether the phone is mandatory. The
        // Api re-validates both server-side regardless of this check.
        await LoadChannelsAsync(defaultSelection: false, cancellationToken);
        var channel = SelectedChannel;
        if (channel is null)
        {
            ErrorMessage = "Select a valid channel.";
            ModelState.AddModelError("Intake.ChannelId", "Select a valid channel.");
            Step = StepCustomer;
            return Page();
        }

        if (channel.RequiresPhone && string.IsNullOrWhiteSpace(Intake.PhoneNumber))
        {
            ErrorMessage = "Enter a phone number to search.";
            ModelState.AddModelError("Intake.PhoneNumber", "A phone number is required for this channel.");
            Step = StepCustomer;
            return Page();
        }

        var request = new CreateIntakeRecordRequestDto(
            Intake.ChannelId, Intake.PhoneNumber, DepartmentId: null, IsUnitRelated: false,
            RawUnitNumberEntered: null, PriorityHint: null);

        var result = await intakeClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Could not record this interaction.");
            Step = StepCustomer;
            return Page();
        }

        return RedirectToPage(new
        {
            step = StepCustomer,
            intakeRecordId = result.Value.IntakeRecordId,
            phoneNumber = result.Value.PhoneNumber
        });
    }

    private async Task LoadCustomerStepAsync(CancellationToken cancellationToken)
    {
        await LoadChannelsAsync(defaultSelection: true, cancellationToken);

        // Customer Workspace carry-forward: "+ New Ticket" from a searched/
        // selected customer arrives with ?phoneNumber=… — the search field is
        // prefilled (still free-form and editable, value preserved exactly)
        // so the agent never re-types the number; pressing Search re-verifies
        // through the exact same lookups as always.
        if (!string.IsNullOrWhiteSpace(PhoneNumber))
        {
            Intake.PhoneNumber = PhoneNumber;
        }

        if (IntakeRecordId is null || string.IsNullOrWhiteSpace(PhoneNumber))
        {
            return;
        }

        await LoadSavedContactAsync(cancellationToken);
        if (ShowContactForm && CustomerKey == "manual")
        {
            // Manual entry searches nothing: the form is the whole step.
            ContactFormName ??= SavedContact?.Name;
            ContactFormEmail ??= SavedContact?.Email;
            return;
        }

        await RunLookupsAsync(cancellationToken);
        BuildCandidates();
        if (ShowContactForm)
        {
            var first = Candidates.FirstOrDefault();
            ContactFormName ??= SavedContact?.Name ?? first?.DisplayName;
            ContactFormEmail ??= SavedContact?.Email ?? first?.Email;
            return;
        }

        await LoadCandidateHistoriesAsync(cancellationToken);
    }

    /// <summary>
    /// The department-aware customer lookup decides which sources
    /// participate (and carries the PACT/Tasleeh results); the real CRM
    /// Buyer Lookup then runs only when the Crm source is in scope. Both are
    /// idempotent reads, re-run on the steps that render their results —
    /// the agent's explicit selections are what carry forward, never
    /// re-derived.
    /// </summary>
    private async Task RunLookupsAsync(CancellationToken cancellationToken)
    {
        await RunCustomerLookupAsync(cancellationToken);
        if (CrmParticipates)
        {
            await RunCrmBuyerLookupAsync(cancellationToken);
        }
    }

    private async Task RunCustomerLookupAsync(CancellationToken cancellationToken)
    {
        if (IntakeRecordId is not { } intakeRecordId)
        {
            // Deep-linked without an intake id — nothing to look up against;
            // fail open to the CRM-only behavior.
            CustomerLookupUnavailable = true;
            return;
        }

        var result = await customerLookupClient.SearchAsync(intakeRecordId, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            CustomerLookup = result.Value;
            CrmParticipates = result.Value.Sources.Any(s => string.Equals(s.Source, "Crm", StringComparison.Ordinal));
        }
        else
        {
            CustomerLookupUnavailable = true;
        }
    }

    /// <summary>
    /// The one and only CRM search this wizard ever runs — phone number
    /// only, never Unit Number/Project/Tower. Found (200), NotFound (404),
    /// an ambiguous multi-customer conflict (409), and every other outcome
    /// (401/400/502/network-unreachable) are all handled here without
    /// blocking the wizard — every one of them leaves the manual path open.
    /// </summary>
    private async Task RunCrmBuyerLookupAsync(CancellationToken cancellationToken)
    {
        var result = await crmBuyerLookupClient.SearchByPhoneAsync(PhoneNumber!, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            // Business rule: a CRM phone number belongs to exactly one
            // customer — CrmBuyerLookupAppService (TigerCS.Api) already
            // consolidates to at most one entry. Taking the first element is
            // defensive only (never trust a contract further than the wire),
            // not a "pick among several customers" decision this page makes.
            CrmBuyerMatch = result.Value.Count > 0 ? result.Value[0] : null;
        }
        else if (result.Outcome == ApiOutcome.NotFound)
        {
            CrmBuyerMatch = null;
        }
        else if (result.Outcome == ApiOutcome.Conflict)
        {
            CrmBuyerAmbiguousMatch = true;
            CrmBuyerMatch = null;
        }
        else
        {
            CrmBuyerLookupUnavailable = true;
            CrmBuyerMatch = null;
        }
    }

    private void BuildCandidates()
    {
        var candidates = new List<NewTicketCandidate>();
        LinkedPactCustomer = null;
        LinkedPactOnlyUnits = [];

        if (CrmBuyerMatch is { } match)
        {
            // Keep every source record for identity evidence. Matching customer
            // details, normalized phones and project/unit are all required;
            // ambiguity keeps the cards separate.
            LinkedPactCustomer = CustomerIdentityLinker.FindPactMatch(match, [match], CustomerLookup?.Sources ?? []);
            if (LinkedPactCustomer is not null)
            {
                LinkedPactOnlyUnits = SelectablePactUnits(LinkedPactCustomer)
                    .Where(pactUnit => !match.Units.Any(crmUnit => UnitsLink(crmUnit, pactUnit)))
                    .ToList();
            }

            candidates.Add(new NewTicketCandidate(
                "crm",
                "Crm",
                match.Customer.FullNameEnglish ?? match.Customer.FullNameArabic,
                match.Customer.MobileNumber ?? PhoneNumber,
                match.Customer.Email ?? LinkedPactCustomer?.Email,
                match.Units.Count + LinkedPactOnlyUnits.Count,
                LinkedPactCustomer is null ? null : ["Pact", "Crm"],
                LinkedPactCustomer?.ExternalCustomerId));
        }

        foreach (var source in ExternalLookupSources.Where(s => s.Status == "Found"))
        {
            foreach (var external in source.Customers)
            {
                if (LinkedPactCustomer is { } linked
                    && string.Equals(source.Source, "Pact", StringComparison.Ordinal)
                    && string.Equals(external.ExternalCustomerId, linked.ExternalCustomerId, StringComparison.Ordinal))
                {
                    // Already on the unified card.
                    continue;
                }

                // A PACT customer whose contracts have all expired still gets a card: the identity and ticket history
                // stay available while ExternalLookupSources has removed every unit from selection (UnitsCount = 0).
                candidates.Add(new NewTicketCandidate(
                    $"ext:{Uri.EscapeDataString(source.Source)}:{Uri.EscapeDataString(external.ExternalCustomerId)}",
                    source.Source,
                    external.DisplayName,
                    external.PhoneNumber ?? PhoneNumber,
                    external.Email,
                    external.Units.Count));
            }
        }

        Candidates = candidates.OrderBy(c => c.PrimaryVerificationSource switch
        {
            "Pact" => 0,
            "Crm" => 1,
            _ => 2
        }).ToList();
        SelectedCandidate = CustomerKey is { } key ? candidates.FirstOrDefault(c => c.Key == key) : null;

        // The name/e-mail an agent saved for this phone is what the cards (and the summary) show.
        if (SavedContact is { } saved)
        {
            Candidates = Candidates
                .Select(c => c with { DisplayName = saved.Name, Email = saved.Email ?? c.Email })
                .ToList();
            SelectedCandidate = CustomerKey is { } selectedKey ? Candidates.FirstOrDefault(c => c.Key == selectedKey) : null;
        }
    }

    /// <summary>
    /// One bounded, identity-keyed history read per matched customer — the
    /// exact stable identity each card represents (CRM customer id, or the
    /// persisted source + external customer id), never a name or phone
    /// match. Serves both the card's counts and the compact existing-tickets
    /// notice. A failed read leaves that card without the notice; the wizard
    /// never blocks on it. The unified card reads BOTH of its identities and
    /// combines them without duplicate ticket ids
    /// (<see cref="CustomerHistoryMerge"/>); if only one read succeeds, the
    /// card shows that one.
    /// </summary>
    private async Task LoadCandidateHistoriesAsync(CancellationToken cancellationToken)
    {
        var histories = new Dictionary<string, CustomerHistoryDto>();
        foreach (var candidate in Candidates)
        {
            var result = candidate.Key == "crm" && CrmBuyerMatch is { } match
                ? await customerHistoryClient.GetByCrmCustomerIdAsync(
                    match.Customer.CustomerId, CandidateHistoryLimit, cancellationToken, orderActiveFirst: true)
                : await LoadExternalCandidateHistoryAsync(candidate, cancellationToken);
            var history = result is { IsSuccess: true, Value: not null } ? result.Value : null;

            if (candidate.LinkedPactCustomerId is { } linkedPactCustomerId)
            {
                var linkedResult = await customerHistoryClient.GetByExternalIdentityAsync(
                    "Pact", linkedPactCustomerId, CandidateHistoryLimit, cancellationToken, orderActiveFirst: true);
                var linkedHistory = linkedResult is { IsSuccess: true, Value: not null } ? linkedResult.Value : null;
                history = history is null ? linkedHistory
                    : linkedHistory is null ? history
                    : CustomerHistoryMerge.Combine(history, linkedHistory, CandidateHistoryLimit);
            }

            if (history is not null)
            {
                histories[candidate.Key] = history;
            }
        }

        CandidateHistories = histories;
    }

    private Task<ApiResult<CustomerHistoryDto>> LoadExternalCandidateHistoryAsync(
        NewTicketCandidate candidate, CancellationToken cancellationToken)
    {
        var parts = candidate.Key.Split(':', 3);
        return customerHistoryClient.GetByExternalIdentityAsync(
            Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]),
            CandidateHistoryLimit, cancellationToken, orderActiveFirst: true);
    }

    // ---------------------------------------------------------------
    // Step 2 — Property / Unit
    // ---------------------------------------------------------------

    private async Task LoadPropertyStepAsync(CancellationToken cancellationToken)
    {
        // The unit options belong to the selected customer's own lookup
        // results, so those results are re-read here (idempotent reads);
        // once a unit is selected its packed value carries forward and no
        // further lookup runs on later steps.
        await LoadSavedContactAsync(cancellationToken);
        if (CustomerKey == "crm" || CustomerKey?.StartsWith("ext:", StringComparison.Ordinal) == true)
        {
            await RunLookupsAsync(cancellationToken);
            BuildCandidates();
        }
        else
        {
            await LoadProjectOptionsAsync(cancellationToken);
        }

        if (HasUnitSelection && CustomerKey != "manual")
        {
            await LoadRelatedTicketsAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The agent explicitly selected one Buyer's one eligible unit from the
    /// real CRM Buyer Lookup results — its CRM identifiers carry forward to
    /// ticket creation. <paramref name="selectedCrmBuyerUnit"/> packs
    /// "{customerId}:{leadId}:{unitId}:{projectId}:{escaped customer
    /// name}:{escaped project name}:{escaped unit number}" into one value so
    /// the ids and the display text the agent saw always travel together and
    /// can never be mismatched. Every text field is
    /// <see cref="Uri.EscapeDataString(string)"/>-encoded before packing
    /// (encoding a literal ':' as %3A), so splitting on ':' is always safe.
    /// </summary>
    public IActionResult OnPostUseCrmBuyerUnit(
        long intakeRecordId, string? phoneNumber, string selectedCrmBuyerUnit)
    {
        var parts = selectedCrmBuyerUnit.Split(':', 7);
        var customerId = int.Parse(parts[0]);
        var leadId = int.Parse(parts[1]);
        var unitId = int.Parse(parts[2]);
        var projectId = int.Parse(parts[3]);
        var customerName = UnescapeOrNull(parts, 4);
        var projectName = UnescapeOrNull(parts, 5);
        var unitNumber = UnescapeOrNull(parts, 6);

        return RedirectToPage(new
        {
            step = StepProperty,
            intakeRecordId,
            phoneNumber,
            customer = "crm",
            crmBuyerCustomerId = customerId,
            crmBuyerLeadId = leadId,
            crmBuyerUnitId = unitId,
            crmBuyerProjectId = projectId,
            crmBuyerCustomerName = customerName,
            crmBuyerProjectName = projectName,
            crmBuyerUnitNumber = unitNumber
        });
    }

    /// <summary>
    /// The agent explicitly selected one PACT/Tasleeh customer's one unit —
    /// the same packed shape as <see cref="OnPostUseCrmBuyerUnit"/>, packing
    /// "{escaped source}:{escaped external customer id}:{escaped external
    /// unit id}:{escaped customer name}:{escaped project name}:{escaped unit
    /// number}" so ids and display text always travel together. Ticket
    /// creation persists the source/customer/unit identifiers as the
    /// Ticket's generic external verification identity plus the manual
    /// Project/Unit snapshot, exactly as before the redesign.
    /// </summary>
    public IActionResult OnPostUseExternalUnit(
        long intakeRecordId, string? phoneNumber, string? customer, string selectedExternalUnit) =>
        RedirectToPage(new
        {
            step = StepProperty,
            intakeRecordId,
            phoneNumber,
            customer,
            externalSelection = selectedExternalUnit
        });

    /// <summary>
    /// The manual property path — no verified unit exists (customer not
    /// found, a source without unit data, or the agent simply can't find the
    /// unit). Both fields are required together, are never used to run
    /// another lookup, and — exactly as before the redesign — a manual
    /// property carries no external identity: a manual customer must never
    /// be associated with another customer's verified records.
    /// </summary>
    public async Task<IActionResult> OnPostUseManualUnitAsync(
        long intakeRecordId, string? phoneNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CreateStep.ManualProjectName) || string.IsNullOrWhiteSpace(CreateStep.ManualUnitNumber))
        {
            ErrorMessage = "Project and Unit Number are both required.";
            ApplyWizardState(
                StepProperty, intakeRecordId, phoneNumber, "manual",
                null, null, null, null, null, null, null, null, null, null);
            await LoadPropertyStepAsync(cancellationToken);
            return Page();
        }

        // One project, one spelling: a typed name that matches a known project (case-insensitively) is saved as that project's own
        // spelling, so "marina tower" and "Marina Tower" can never become two projects.
        var projectName = CreateStep.ManualProjectName.Trim();
        if (projectCatalogClient is not null)
        {
            var known = await projectCatalogClient.ListProjectsAsync(cancellationToken);
            if (known is { IsSuccess: true, Value: not null }
                && known.Value.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                projectName = match.Name;
            }
        }

        return RedirectToPage(new
        {
            step = StepProperty,
            intakeRecordId,
            phoneNumber,
            customer = "manual",
            manualProjectName = projectName,
            manualUnitNumber = CreateStep.ManualUnitNumber.Trim()
        });
    }

    /// <summary>The known units for a project, for the Unit dropdown (fetched when the Project changes). Read-only; an empty list means "type it in".</summary>
    public async Task<IActionResult> OnGetUnitsAsync(string? project, CancellationToken cancellationToken)
    {
        if (projectCatalogClient is null || string.IsNullOrWhiteSpace(project))
        {
            return new JsonResult(Array.Empty<string>());
        }

        var result = await projectCatalogClient.ListUnitsAsync(project.Trim(), cancellationToken);
        return new JsonResult(result.IsSuccess && result.Value is not null ? result.Value : []);
    }

    private async Task LoadProjectOptionsAsync(CancellationToken cancellationToken)
    {
        if (projectCatalogClient is null)
        {
            return;
        }

        var projects = await projectCatalogClient.ListProjectsAsync(cancellationToken);
        ProjectOptions = projects is { IsSuccess: true, Value: not null } ? projects.Value : [];

        var selected = CreateStep.ManualProjectName ?? ManualProjectName;
        if (!string.IsNullOrWhiteSpace(selected))
        {
            var units = await projectCatalogClient.ListUnitsAsync(selected.Trim(), cancellationToken);
            UnitOptions = units is { IsSuccess: true, Value: not null } ? units.Value : [];
        }
    }

    // ---------------------------------------------------------------
    // Step 1 — manual customer entry / edit (Tiger Ticketing's own record only)
    // ---------------------------------------------------------------

    private async Task LoadSavedContactAsync(CancellationToken cancellationToken)
    {
        if (customerContactClient is null || string.IsNullOrWhiteSpace(PhoneNumber) || SavedContact is not null)
        {
            return;
        }

        var result = await customerContactClient.FindAsync(PhoneNumber, cancellationToken);
        SavedContact = result.IsSuccess ? result.Value : null;
    }

    /// <summary>
    /// Saves the customer's name and e-mail for this phone — creating the record, or updating the existing one (the Api matches on the
    /// canonical phone, so no duplicate can be made). Local to Tiger Ticketing: nothing is sent to CRM, PACT or Tasleeh and the
    /// verification state is untouched. A manual customer continues to Step 2; an existing customer returns to the results.
    /// </summary>
    public async Task<IActionResult> OnPostSaveContactAsync(
        long intakeRecordId, string? phoneNumber, string? customer, string? contactName, string? contactEmail,
        CancellationToken cancellationToken)
    {
        var isManual = customer == "manual";
        ApplyWizardState(StepCustomer, intakeRecordId, phoneNumber, customer, null, null, null, null, null, null, null, null, null, null);
        ShowContactForm = true;
        ContactFormIsEdit = !isManual;
        ContactFormName = contactName;
        ContactFormEmail = contactEmail;

        async Task<IActionResult> ReRenderAsync(string message)
        {
            ErrorMessage = message;
            await LoadCustomerStepAsync(cancellationToken);
            return Page();
        }

        if (customerContactClient is null || string.IsNullOrWhiteSpace(phoneNumber))
        {
            return await ReRenderAsync("Customer details could not be saved right now. Try again.");
        }

        if (string.IsNullOrWhiteSpace(contactName))
        {
            ContactNameError = "Customer name is required.";
            return await ReRenderAsync("Enter the customer's name to continue.");
        }

        var saved = await customerContactClient.SaveAsync(new SaveCustomerContactRequestDto(phoneNumber, contactName, contactEmail), cancellationToken);
        if (!saved.IsSuccess || saved.Value is null)
        {
            var detail = saved.Detail ?? DescribeFailure(saved.Outcome, "Customer details could not be saved.");
            if (saved.Outcome == ApiOutcome.ValidationError)
            {
                ContactEmailError = detail.Contains("email", StringComparison.OrdinalIgnoreCase) ? detail : null;
                ContactNameError = ContactEmailError is null ? detail : null;
            }

            return await ReRenderAsync(detail);
        }

        return isManual
            ? RedirectToPage(new { step = StepProperty, intakeRecordId, phoneNumber, customer = "manual" })
            : RedirectToPage(new { step = StepCustomer, intakeRecordId, phoneNumber, contactSaved = true });
    }

    /// <summary>
    /// Duplicate-ticket awareness for the selected unit: one scoped history
    /// query keyed by the exact verified identity the agent selected,
    /// narrowed to the selected unit's number snapshot, active tickets
    /// first, capped at 5. Deterministic by design; advisory only — a failed
    /// call leaves <see cref="RelatedTickets"/> null and the wizard proceeds.
    /// </summary>
    private async Task LoadRelatedTicketsAsync(CancellationToken cancellationToken)
    {
        const int relatedLimit = 5;
        CustomerHistoryDto? related = null;
        string? unitNumber = null;

        if (CrmBuyerCustomerId is { } crmBuyerCustomerId)
        {
            unitNumber = CrmBuyerUnitNumber;
            var result = await customerHistoryClient.GetByCrmCustomerIdAsync(
                crmBuyerCustomerId, relatedLimit, cancellationToken,
                unitNumber: unitNumber, orderActiveFirst: true);
            related = result.IsSuccess ? result.Value : null;
        }
        else if (ExternalSource is { } externalSource && ExternalCustomerId is { } externalCustomerId)
        {
            unitNumber = ExternalUnitNumber;
            var result = await customerHistoryClient.GetByExternalIdentityAsync(
                externalSource, externalCustomerId, relatedLimit, cancellationToken,
                unitNumber: unitNumber, orderActiveFirst: true);
            related = result.IsSuccess ? result.Value : null;
        }
        else
        {
            return;
        }

        // Under the unified card the same unit may carry tickets under the
        // OTHER identity too (a CRM-selected unit's earlier PACT tickets, or
        // a PACT-selected unit's earlier CRM tickets) — read it as well and
        // combine without duplicate ticket ids, so duplicate-ticket
        // awareness sees the whole unit.
        if (LinkedPactCustomer is { } linked && CrmBuyerMatch is { } match)
        {
            var otherResult = CrmBuyerCustomerId is not null
                ? await customerHistoryClient.GetByExternalIdentityAsync(
                    "Pact", linked.ExternalCustomerId, relatedLimit, cancellationToken, unitNumber: unitNumber, orderActiveFirst: true)
                : await customerHistoryClient.GetByCrmCustomerIdAsync(
                    match.Customer.CustomerId, relatedLimit, cancellationToken, unitNumber: unitNumber, orderActiveFirst: true);
            if (otherResult is { IsSuccess: true, Value: not null } other)
            {
                related = related is null ? other.Value
                    : CrmBuyerCustomerId is not null
                        ? CustomerHistoryMerge.Combine(related, other.Value, relatedLimit)
                        : CustomerHistoryMerge.Combine(other.Value, related, relatedLimit);
            }
        }

        RelatedTickets = related;
    }

    // ---------------------------------------------------------------
    // Step 3 — Issue, and Step 4 — Review & Create
    // ---------------------------------------------------------------

    private async Task LoadIssueStepAsync(CancellationToken cancellationToken)
    {
        await LoadSavedContactAsync(cancellationToken);
        await LoadDepartmentsAsync(cancellationToken);
        await LoadCategoriesAsync(CreateStep.DepartmentId, cancellationToken);
        await LoadRequestTypeOptionsAsync(CreateStep.DepartmentId, cancellationToken);
    }

    private async Task LoadRequestTypeOptionsAsync(int? departmentId, CancellationToken cancellationToken)
    {
        if (requestTypesClient is null)
        {
            RequestTypeOptions = [];
            RequestTypesErrorMessage = "Unable to load request types. Please try again.";
            return;
        }

        var result = await requestTypesClient.GetOptionsAsync(departmentId, cancellationToken);
        RequestTypesErrorMessage = result.IsSuccess && result.Value is not null
            ? null
            : result.Detail ?? DescribeFailure(result.Outcome, "Unable to load request types. Please try again.");
        // Keep unpublished names so a legacy category with the same name cannot
        // bypass an unavailable configured workflow in the combined picker.
        RequestTypeOptions = result.IsSuccess && result.Value is not null ? result.Value : [];
    }

    /// <summary>
    /// Redisplays the Issue step after the Department selection changed —
    /// the Category list reloads scoped to that Department, and every
    /// other entered value (priority, request text) survives because this is
    /// a plain POST round-trip of the same form. A Category that no
    /// longer belongs to the chosen Department is cleared rather than
    /// silently submitted against it.
    /// </summary>
    public async Task<IActionResult> OnPostIssueRefreshAsync(
        long intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber,
        CancellationToken cancellationToken)
    {
        ApplyWizardState(
            StepIssue, intakeRecordId, phoneNumber, customer,
            crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
            crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
            externalSelection, manualProjectName, manualUnitNumber);

        await LoadIssueStepAsync(cancellationToken);

        if (CreateStep.CategoryId is { } categoryId && Categories.All(c => c.CategoryId != categoryId))
        {
            CreateStep.CategoryId = null;
        }

        if (CreateStep.RequestTypeId is { } requestTypeId && RequestTypeOptions.All(r => r.RequestTypeId != requestTypeId))
        {
            CreateStep.RequestTypeId = null;
        }

        if (CreateStep.UseRequestCategoryPicker)
        {
            CreateStep.CategoryId = null;
            CreateStep.RequestTypeId = null;
            if (RequestTypeChoices.All(c => c.Value != CreateStep.RequestTypeChoice))
            {
                CreateStep.RequestTypeChoice = null;
                ModelState.Remove("CreateStep.RequestTypeChoice");
            }
            ModelState.Remove("CreateStep.CategoryId");
            ModelState.Remove("CreateStep.RequestTypeId");
        }

        return Page();
    }

    /// <summary>
    /// Validates the Issue step and renders the Review step — a POST
    /// round-trip (never a redirect), so the entered request text stays in
    /// the form post and out of any URL. Validation failures redisplay the
    /// Issue step with everything the agent entered intact.
    /// </summary>
    public async Task<IActionResult> OnPostReviewAsync(
        long intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber,
        CancellationToken cancellationToken)
    {
        ApplyWizardState(
            StepIssue, intakeRecordId, phoneNumber, customer,
            crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
            crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
            externalSelection, manualProjectName, manualUnitNumber);

        var pickerError = await ResolveRequestTypeChoiceAsync(cancellationToken);
        if ((pickerError ?? ValidateIssue()) is { } validationError)
        {
            ErrorMessage = validationError;
            if (!CreateStep.UseRequestCategoryPicker)
            {
                await LoadIssueStepAsync(cancellationToken);
            }
            return Page();
        }

        Step = StepReview;
        if (!CreateStep.UseRequestCategoryPicker)
        {
            await LoadIssueStepAsync(cancellationToken);
        }
        await LoadReviewPaymentsAsync(cancellationToken);
        return Page();
    }

    /// <summary>
    /// Payments &amp; Fines on the Review step: the selected identity's
    /// panel again, read-only (no reminder is sent from a ticket-creation
    /// screen). The Review step runs no customer lookups — the agent's
    /// selections are what carry forward, never re-derived — so the unified
    /// card's other identity (which only the lookups can establish) is
    /// shown on Step 2, where the agent selects the unit, and not here.
    /// </summary>
    private Task LoadReviewPaymentsAsync(CancellationToken cancellationToken) =>
        LoadPaymentPanelsAsync(readOnly: true, cancellationToken);

    /// <summary>
    /// Loads <see cref="PaymentPanel"/> (and, under the unified card,
    /// <see cref="LinkedPaymentPanel"/>) for the selected unit's verified
    /// identity. A CRM Buyer selection reads the CRM customer's finance
    /// accounts, scoped to the account holding the selected unit number
    /// unless the agent chose one; a PACT selection reads the PACT tenant's
    /// EDSM summary by the same <c>ext:Pact:{tenant}</c> key the Customer
    /// Profile uses (the Api resolves the verified tenant/company mapping —
    /// unfiltered, every contract), scoped to the company whose contract
    /// covers the selected unit. Any other external source has no payment
    /// source, and the panel says so. Manual entry has no identity, so no
    /// panel. Never blocks the wizard: every failure is a panel state.
    /// </summary>
    private async Task LoadPaymentPanelsAsync(bool readOnly, CancellationToken cancellationToken)
    {
        PaymentPanel = null;
        LinkedPaymentPanel = null;
        if (paymentPanelLoader is null || !HasUnitSelection || CustomerKey == "manual")
        {
            return;
        }

        if (CrmBuyerCustomerId is { } crmBuyerCustomerId)
        {
            if (LinkedPactCustomer is { } linked)
            {
                // PACT is the primary financial source on a verified combined
                // customer. Keep CRM finance available afterward under its own ID.
                var linkedUnit = linked.Units.FirstOrDefault(pactUnit =>
                    string.Equals(pactUnit.UnitNumber?.Trim(), CrmBuyerUnitNumber?.Trim(), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pactUnit.PropertyName?.Trim(), CrmBuyerProjectName?.Trim(), StringComparison.OrdinalIgnoreCase));
                PaymentPanel = await paymentPanelLoader.LoadAsync(
                    LinkedPactCustomerKey!, null, PaymentAccount, null, false, cancellationToken,
                    new PaymentPanelOptions(PaymentLinks("paymentAccount"), AllowSending: !readOnly,
                        PreferredExternalUnitId: linkedUnit?.ExternalUnitId, LookupPhoneNumber: PhoneNumber));
                LinkedPaymentPanel = await paymentPanelLoader.LoadAsync(
                    $"crm:{crmBuyerCustomerId}", crmBuyerCustomerId, LinkedPaymentAccount, null, false, cancellationToken,
                    new PaymentPanelOptions(PaymentLinks("linkedPaymentAccount"), AllowSending: !readOnly, PreferredUnitNumber: CrmBuyerUnitNumber, PreferredCrmUnitId: CrmBuyerUnitId, CrmLookupPhone: PhoneNumber));
            }
            else
            {
                PaymentPanel = await paymentPanelLoader.LoadAsync(
                    $"crm:{crmBuyerCustomerId}", crmBuyerCustomerId, PaymentAccount, null, false, cancellationToken,
                    new PaymentPanelOptions(PaymentLinks("paymentAccount"), AllowSending: !readOnly,
                        PreferredUnitNumber: CrmBuyerUnitNumber, LookupPhoneNumber: PhoneNumber, PreferredCrmUnitId: CrmBuyerUnitId, CrmLookupPhone: PhoneNumber));
            }
            return;
        }

        if (ExternalSource is { } externalSource && ExternalCustomerId is { } externalCustomerId)
        {
            PaymentPanel = await paymentPanelLoader.LoadAsync(
                $"ext:{externalSource}:{Uri.EscapeDataString(externalCustomerId)}", null, PaymentAccount, null, false, cancellationToken,
                new PaymentPanelOptions(PaymentLinks("paymentAccount"), AllowSending: !readOnly, PreferredExternalUnitId: ExternalUnitId, LookupPhoneNumber: PhoneNumber));

            if (LinkedPactCustomer is not null && CrmBuyerMatch is { } match
                && string.Equals(externalSource, "Pact", StringComparison.Ordinal)
                && string.Equals(externalCustomerId, LinkedPactCustomer.ExternalCustomerId, StringComparison.Ordinal))
            {
                LinkedPaymentPanel = await paymentPanelLoader.LoadAsync(
                    $"crm:{match.Customer.CustomerId}", match.Customer.CustomerId, LinkedPaymentAccount, null, false, cancellationToken,
                    new PaymentPanelOptions(PaymentLinks("linkedPaymentAccount"), AllowSending: !readOnly, PreferredUnitNumber: ExternalUnitNumber, LookupPhoneNumber: PhoneNumber));
            }
        }
    }

    /// <summary>
    /// The payment panel's navigation inside the wizard: the account
    /// selector and Retry links reload Step 2 with the full carried state
    /// plus the chosen account under <paramref name="accountParameter"/>
    /// (the other panel's choice is carried along untouched).
    /// </summary>
    public PaymentPanelLinks PaymentLinks(string accountParameter)
    {
        var hidden = new Dictionary<string, string?>
        {
            ["step"] = StepProperty,
            ["intakeRecordId"] = IntakeRecordId?.ToString(),
            ["phoneNumber"] = PhoneNumber,
            ["customer"] = CustomerKey,
            ["crmBuyerCustomerId"] = CrmBuyerCustomerId?.ToString(),
            ["crmBuyerLeadId"] = CrmBuyerLeadId?.ToString(),
            ["crmBuyerUnitId"] = CrmBuyerUnitId?.ToString(),
            ["crmBuyerProjectId"] = CrmBuyerProjectId?.ToString(),
            ["crmBuyerCustomerName"] = CrmBuyerCustomerName,
            ["crmBuyerProjectName"] = CrmBuyerProjectName,
            ["crmBuyerUnitNumber"] = CrmBuyerUnitNumber,
            ["externalSelection"] = ExternalSelection,
            ["manualProjectName"] = ManualProjectName,
            ["manualUnitNumber"] = ManualUnitNumber,
            [accountParameter == "paymentAccount" ? "linkedPaymentAccount" : "paymentAccount"] =
                accountParameter == "paymentAccount" ? LinkedPaymentAccount : PaymentAccount
        }
        .Where(kv => !string.IsNullOrEmpty(kv.Value))
        .ToDictionary(kv => kv.Key, kv => kv.Value!);

        var baseQuery = string.Join("&", hidden.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new PaymentPanelLinks(
            "/NewTicket",
            hidden,
            accountParameter,
            account => account is null
                ? $"/NewTicket?{baseQuery}#payments-fines"
                : $"/NewTicket?{baseQuery}&{accountParameter}={Uri.EscapeDataString(account)}#payments-fines",
            AutoSubmit: true);
    }

    public async Task<IActionResult> OnPostCreateAsync(
        long intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber,
        CancellationToken cancellationToken)
    {
        ApplyWizardState(
            StepReview, intakeRecordId, phoneNumber, customer,
            crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
            crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
            externalSelection, manualProjectName, manualUnitNumber);

        // The dropdowns are the only way to supply a CategoryId/PriorityId —
        // never manually typed in — but the request is still rejected here
        // (rather than trusted) if somehow a value is missing. POST
        // /api/tickets is the actual authority and re-validates everything.
        var pickerError = await ResolveRequestTypeChoiceAsync(cancellationToken);
        if ((pickerError ?? ValidateIssue()) is { } validationError)
        {
            ErrorMessage = validationError;
            Step = StepIssue;
            if (!CreateStep.UseRequestCategoryPicker)
            {
                await LoadIssueStepAsync(cancellationToken);
            }
            return Page();
        }

        var hasCrmBuyerMatch = CrmBuyerUnitId is not null;

        // A PACT/Tasleeh selection persists both ways: the generic external
        // verification identity (source + the source's own customer/unit
        // ids) AND the human-readable manual Project/Unit snapshot —
        // mutually exclusive with a CRM Buyer match, exactly as the Api
        // re-validates server-side.
        var hasExternalSelection = !hasCrmBuyerMatch && ExternalSelection is not null;
        var manualProject = CreateStep.ManualProjectName ?? ExternalProjectName ?? ManualProjectName;
        var manualUnit = CreateStep.ManualUnitNumber ?? ExternalUnitNumber ?? ManualUnitNumber;

        var request = new CreateTicketRequestDto(
            IntakeRecordId: intakeRecordId,
            UnitReferenceId: null,
            ContactReferenceId: null,
            CategoryId: CreateStep.CategoryId!.Value,
            PriorityId: CreateStep.PriorityId!.Value,
            RequestSummary: CreateStep.RequestSummary,
            RequestTypeId: CreateStep.RequestTypeId,
            CrmBuyerCustomerId: hasCrmBuyerMatch ? crmBuyerCustomerId : null,
            CrmBuyerLeadId: hasCrmBuyerMatch ? crmBuyerLeadId : null,
            CrmBuyerUnitId: hasCrmBuyerMatch ? crmBuyerUnitId : null,
            CrmBuyerProjectId: hasCrmBuyerMatch ? crmBuyerProjectId : null,
            CrmBuyerCustomerName: hasCrmBuyerMatch ? crmBuyerCustomerName : null,
            CrmBuyerProjectName: hasCrmBuyerMatch ? crmBuyerProjectName : null,
            CrmBuyerUnitNumber: hasCrmBuyerMatch ? crmBuyerUnitNumber : null,
            ManualProjectName: hasCrmBuyerMatch ? null : manualProject,
            ManualUnitNumber: hasCrmBuyerMatch ? null : manualUnit,
            CustomerVerificationSource: hasExternalSelection ? ExternalSource : null,
            ExternalCustomerId: hasExternalSelection ? ExternalCustomerId : null,
            ExternalCustomerName: hasExternalSelection ? ExternalCustomerName : null,
            ExternalCustomerEmail: hasExternalSelection ? ExternalCustomerEmail : null,
            ExternalUnitId: hasExternalSelection ? ExternalUnitId : null,
            AssignToDepartmentQueue: CreateStep.AssignToDepartmentQueue ? true : null);

        var result = await ticketsClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Could not create the ticket.");
            Step = StepReview;
            await LoadIssueStepAsync(cancellationToken);
            await LoadReviewPaymentsAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage(new
        {
            step = StepDone,
            createdTicketId = result.Value.TicketId,
            createdTicketNumber = result.Value.TicketNumber
        });
    }

    private async Task<string?> ResolveRequestTypeChoiceAsync(CancellationToken cancellationToken)
    {
        // Older wizard posts remain compatible; the new form always sets this
        // flag and resolves both IDs from fresh catalogs rather than hidden IDs.
        if (!CreateStep.UseRequestCategoryPicker)
        {
            return null;
        }

        CreateStep.CategoryId = null;
        CreateStep.RequestTypeId = null;
        ModelState.Remove("CreateStep.CategoryId");
        ModelState.Remove("CreateStep.RequestTypeId");
        await LoadIssueStepAsync(cancellationToken);
        if (DepartmentsErrorMessage is not null || CategoriesErrorMessage is not null || RequestTypesErrorMessage is not null)
        {
            return "Unable to load request categories and types. Please try again.";
        }
        if (CreateStep.DepartmentId is not { } departmentId || Departments.All(d => d.DepartmentId != departmentId))
        {
            return "Select a request category before continuing.";
        }
        var choice = RequestTypeChoices.FirstOrDefault(c => c.Value == CreateStep.RequestTypeChoice);
        if (choice is null)
        {
            return "Select a request type belonging to the selected request category.";
        }
        if (choice.CategoryId is null)
        {
            return "Routing is not configured for this request type. Contact an administrator.";
        }
        CreateStep.CategoryId = choice.CategoryId;
        CreateStep.RequestTypeId = choice.RequestTypeId;
        return null;
    }

    /// <summary>The pre-create guards, shared by Review and Create: a property (verified or manual pair), a Category, a Priority, and the request text. The Api re-validates all of it.</summary>
    private string? ValidateIssue()
    {
        var hasCrmBuyerMatch = CrmBuyerUnitId is not null;
        var manualProject = CreateStep.ManualProjectName ?? ExternalProjectName ?? ManualProjectName;
        var manualUnit = CreateStep.ManualUnitNumber ?? ExternalUnitNumber ?? ManualUnitNumber;

        if (!hasCrmBuyerMatch && (string.IsNullOrWhiteSpace(manualProject) || string.IsNullOrWhiteSpace(manualUnit)))
        {
            return "Customer not found in CRM. Project and Unit Number are required.";
        }

        if (CreateStep.CategoryId is null)
        {
            return "Select a category before continuing.";
        }

        if (CreateStep.PriorityId is null)
        {
            return "Select a priority before continuing.";
        }

        if (string.IsNullOrWhiteSpace(CreateStep.RequestSummary))
        {
            return "Describe the customer's request before continuing.";
        }

        return null;
    }

    // ---------------------------------------------------------------
    // Shared plumbing
    // ---------------------------------------------------------------

    private void ApplyWizardState(
        string step, long? intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber)
    {
        Step = step;
        IntakeRecordId = intakeRecordId;
        PhoneNumber = phoneNumber;
        CustomerKey = string.IsNullOrWhiteSpace(customer) ? null : customer;
        CrmBuyerCustomerId = crmBuyerCustomerId;
        CrmBuyerLeadId = crmBuyerLeadId;
        CrmBuyerUnitId = crmBuyerUnitId;
        CrmBuyerProjectId = crmBuyerProjectId;
        CrmBuyerCustomerName = crmBuyerCustomerName;
        CrmBuyerProjectName = crmBuyerProjectName;
        CrmBuyerUnitNumber = crmBuyerUnitNumber;
        ManualProjectName = string.IsNullOrWhiteSpace(manualProjectName) ? null : manualProjectName;
        ManualUnitNumber = string.IsNullOrWhiteSpace(manualUnitNumber) ? null : manualUnitNumber;
        ApplyExternalSelection(externalSelection);
    }

    private void ApplyExternalSelection(string? externalSelection)
    {
        ExternalSelection = string.IsNullOrWhiteSpace(externalSelection) ? null : externalSelection;
        if (ExternalSelection is null)
        {
            return;
        }

        // Seven parts since the customer's email joined the packed value; an
        // older six-part selection still in a query string simply leaves the
        // email null rather than failing to unpack.
        var parts = ExternalSelection.Split(':', 7);
        ExternalSource = UnescapeOrNull(parts, 0);
        ExternalCustomerId = UnescapeOrNull(parts, 1);
        ExternalUnitId = UnescapeOrNull(parts, 2);
        ExternalCustomerName = UnescapeOrNull(parts, 3);
        ExternalProjectName = UnescapeOrNull(parts, 4);
        ExternalUnitNumber = UnescapeOrNull(parts, 5);
        ExternalCustomerEmail = UnescapeOrNull(parts, 6);
    }

    private static string? UnescapeOrNull(string[] parts, int index)
    {
        if (index >= parts.Length || parts[index].Length == 0)
        {
            return null;
        }

        return Uri.UnescapeDataString(parts[index]);
    }

    /// <summary>
    /// Loads the active channel list for Step 1 and, when
    /// <paramref name="defaultSelection"/> is set (rendering the form), defaults
    /// the picker to the first configured channel if the form does not name
    /// one yet — never on a POST, where a missing channel is a validation
    /// failure. A failure leaves an empty list with a visible message — the
    /// Search form is unusable without a channel, and says so rather than
    /// silently offering nothing.
    /// </summary>
    private async Task LoadChannelsAsync(bool defaultSelection, CancellationToken cancellationToken)
    {
        if (channelsClient is null)
        {
            Channels = [];
            ChannelsErrorMessage = "Unable to load the channel list. Please try again.";
            return;
        }

        var result = await channelsClient.GetChannelsAsync(cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ChannelsErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Unable to load the channel list. Please try again.");
            Channels = [];
            return;
        }

        Channels = result.Value;
        if (defaultSelection && string.IsNullOrWhiteSpace(Intake.ChannelId) && Channels.Count > 0)
        {
            Intake.ChannelId = Channels[0].ChannelId.ToString();
        }
    }

    private ChannelDto? ResolveChannel(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        var value = channelId.Trim();
        if (byte.TryParse(value, out var id))
        {
            return Channels.FirstOrDefault(c => c.ChannelId == id);
        }

        return Channels.FirstOrDefault(c => string.Equals(c.Code, value, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadDepartmentsAsync(CancellationToken cancellationToken)
    {
        var result = await departmentsClient.GetDepartmentsAsync(cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            DepartmentsErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Unable to load the department list. Please try again.");
            Departments = [];
        }
        else
        {
            Departments = result.Value;
        }
    }

    private async Task LoadCategoriesAsync(int? departmentId, CancellationToken cancellationToken)
    {
        var result = await categoriesClient.GetCategoriesAsync(departmentId, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            CategoriesErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Unable to load ticket categories. Please try again.");
            Categories = [];
        }
        else
        {
            Categories = result.Value;
        }
    }

    /// <summary>
    /// A user-facing fallback for a failed Api call whose response carried no
    /// safe "detail"/"title" text (e.g. an empty-bodied 401/403, or a
    /// connection failure) — distinct, actionable wording for the outcomes an
    /// agent can actually act on; every other outcome keeps the caller's own
    /// page-specific fallback text.
    /// </summary>
    private static string DescribeFailure(ApiOutcome outcome, string fallback) => outcome switch
    {
        ApiOutcome.Unauthorized or ApiOutcome.Forbidden => "Your session is not authorized to perform this action.",
        ApiOutcome.Unreachable => "Unable to contact the Ticketing API. Please try again shortly.",
        _ => fallback
    };

    public sealed class IntakeInput
    {
        /// <summary>
        /// The selected channel — the numeric ChannelId the picker posts
        /// (the Api also accepts the channel's stable code). No default:
        /// the picker is built from, and defaults to, the configured
        /// active channels; nothing about channels is hard-coded here.
        /// </summary>
        [Required]
        public string ChannelId { get; set; } = string.Empty;

        /// <summary>
        /// Free-form string, deliberately: a leading '+' (e.g.
        /// "+971501234567") is valid and the value is preserved EXACTLY as
        /// entered through UI → PageModel → API → IntakeRecord — no format
        /// annotation ([RegularExpression]/[Phone]/[DataType]), no HTML
        /// pattern/type restriction, and no reformatting anywhere upstream
        /// may be added (guarded by NewTicketModelTests/
        /// WebFrontEndCleanupTests). The one permitted transformation lives
        /// at the PACT integration boundary only
        /// (PactCustomerHttpGateway.NormalizePactPhone strips the '+' for
        /// PACT requests); CRM receives the number exactly as entered.
        /// </summary>
        /// <para>
        /// Not <c>[Required]</c>: whether a phone number is mandatory is
        /// the selected channel's <c>RequiresPhone</c> configuration,
        /// enforced in <see cref="OnPostIntakeAsync"/> and again by the Api.
        /// </para>
        public string PhoneNumber { get; set; } = string.Empty;
    }

    public sealed class CreateStepInput
    {
        /// <summary>The visible Request Category uses the existing department ID.
        /// Its selected child resolves the routing and workflow IDs server-side.</summary>
        public int? DepartmentId { get; set; }

        public bool UseRequestCategoryPicker { get; set; }

        /// <summary>A server-resolved child selection, scoped to the selected Request Category.</summary>
        public string? RequestTypeChoice { get; set; }

        /// <summary>The real CategoryId of a dropdown selection ("Category") — never typed in by hand. Nullable so "nothing selected" is a distinct, validatable state rather than a fake id like 0.</summary>
        [Required(ErrorMessage = "Select a category.")]
        public int? CategoryId { get; set; }

        /// <summary>1=Critical, 2=High, 3=Medium, 4=Low — dropdown only. Nullable so "nothing selected" is distinct and validatable.</summary>
        [Required(ErrorMessage = "Select a priority.")]
        public byte? PriorityId { get; set; }

        /// <summary>Optional configured Request Type (workflow/assignment/approval/SLA configuration) — a dropdown selection, never typed. Null means the ticket is created without one, exactly as before this phase.</summary>
        public int? RequestTypeId { get; set; }

        [Required]
        public string RequestSummary { get; set; } = string.Empty;

        /// <summary>The agent explicitly leaves the new ticket in the department queue. Unticked (the default), a CS Agent who is eligible becomes its owner.</summary>
        public bool AssignToDepartmentQueue { get; set; }

        /// <summary>Required together with <see cref="ManualUnitNumber"/> on the manual property path; never used to run another lookup.</summary>
        public string? ManualProjectName { get; set; }

        public string? ManualUnitNumber { get; set; }
    }
}
