namespace TigerCS.Application.Modules.Collections;

/// <summary>Direct PACT report reads; independent of the per-customer EDSM APIs.</summary>
public sealed class PactReceivablesOptions
{
    public const string SectionName = "CollectionsSource:PactReceivables";
    public bool Enabled { get; set; }
    /// <summary>
    /// Optional suffix of the report procedures, e.g. "V2" selects dbo.p4AccountReceivablesV2 / dbo.p32AccountReceivablesV2
    /// (review drafts in docs/Collections/pact-sql; not deployed). Empty uses the deployed originals. Letters/digits only.
    /// </summary>
    public string ProcedureSuffix { get; set; } = "";
    public string ConnectionStringName { get; set; } = "PACTRPT";
    public int CommandTimeoutSeconds { get; set; } = 120;
    /// <summary>
    /// Whole-request budget (API → SQL). The two company procedures run concurrently, so the slower one may use up to
    /// <see cref="CommandTimeoutSeconds"/>; the shared deadline adds a margin so the SQL command timeout (reported as a
    /// SQL timeout) always fires before the budget token (reported as "timed out"). The Web client's HttpClient timeout
    /// must exceed this value or the browser-facing request expires first.
    /// </summary>
    public int RequestBudgetSeconds => Math.Clamp(CommandTimeoutSeconds, 1, 300) + 30;
    /// <summary>Lower bound (instalment due date) passed to the PACT procedures as @StartDate.</summary>
    public DateTime StartDate { get; set; } = new(2026, 1, 1);
    public int MaxSourceRows { get; set; } = 250000;
    public string Currency { get; set; } = "AED";
    /// <summary>
    /// Default of the "Minimum outstanding amount (AED)" filter when a request does not carry one. An instalment is shown only when its
    /// remaining unpaid Amount is &gt;= this value. Display filter only: the snapshot always holds every positive balance.
    /// </summary>
    public decimal DefaultMinOutstandingAmount { get; set; } = 100m;
    /// <summary>Upper bound accepted for the filter (sanity check).</summary>
    public decimal MaxMinOutstandingAmount { get; set; } = 1_000_000_000m;
    /// <summary>Off by default: the list shows every apartment with a positive due or overdue amount.</summary>
    public bool ApplyLegacyExclusions { get; set; }
    /// <summary>Lower bound of the legacy booking/down-payment CRM query; required only when legacy exclusions are on.</summary>
    public DateTime? LegacyDownPaymentFromDate { get; set; }
    public string CrmConnectionStringName { get; set; } = "CrmDatabase";
    /// <summary>Set after copying the actual Helper.SEPCIAL_CASES list; an empty list must be intentional.</summary>
    public bool SpecialCasesConfigured { get; set; }
    public List<string> SpecialCaseUnitCodes { get; set; } = [];
    /// <summary>
    /// GULF_CUSTOMERS / CHEQUES: the unit codes the business pays by cheque. Excluded from every campaign (Preview, Export, Approval, Send, Auto) except that they become eligible for MANUAL
    /// Preview, Export and Send on <see cref="GulfCustomersInclusionDay"/> of each month (Dubai date), or when an authorized person includes a unit manually for one Manual campaign.
    /// Set true once the supplied list has been loaded; false (or an empty list) disables the policy. The list is configuration (<c>appsettings</c> / environment variables), never code.
    /// </summary>
    public bool GulfCustomersConfigured { get; set; }
    public List<string> GulfCustomerUnitCodes { get; set; } = [];
    /// <summary>The day of the month (Dubai) on which GULF_CUSTOMERS units are eligible for Manual campaigns. The business rule is 14.</summary>
    public int GulfCustomersInclusionDay { get; set; } = 14;
    public List<string> PdcExcludedUnitCodes { get; set; } = [];
    /// <summary>
    /// Verified mapping of the original PACT report <c>Status</c> value to a payment status
    /// (Unpaid, PartiallyPaid or Paid). Empty until each value has been confirmed against PACT;
    /// any unmapped value is shown as Unknown with the original text retained.
    /// </summary>
    public Dictionary<string, string> SourceStatusMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
