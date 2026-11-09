namespace TigerCS.Application.Modules.Collections.Reconciliation;

/// <summary>
/// THE definition of "the financial source is reconciled", used by the Campaigns preview, the stored review data and the approval summary alike (they used to disagree:
/// one looked at the sign-off flag only, the other also at the procedure). Fail-closed: every branch that is not a full pass explains itself.
/// <para>The decision is made PER COMPANY: the sign-off and the procedure are shared, the automated evidence is each company's own. One company's missing, failed or superseded
/// evidence closes that company only. A company whose check found differences that PERSIST after a controlled refresh (a synchronization or mapping defect) stays open for every
/// unit that agrees; the units that differ are listed as <see cref="SourceGateCompanyDto.HeldUnits"/> and held back one by one, never the whole company or the other company.</para>
/// </summary>
public static class SourceGate
{
    public const string Reconciled = "Reconciled", SignOffMissing = "SignOffMissing", ProcedureChanged = "ProcedureChanged",
        EvidenceNotRun = "EvidenceNotRun", EvidenceFailed = "EvidenceFailed", EvidenceError = "EvidenceError", EvidenceExpired = "EvidenceExpired",
        EvidenceSuperseded = "EvidenceSuperseded";
    /// <summary>Reconciled, but some units differ persistently from PACT and are held back individually (the code of the overall gate and of that company).</summary>
    public const string ReconciledWithHeldUnits = "ReconciledWithHeldUnits";

    public static string CompanyName(int companyId) => companyId == 4 ? "Tiger Group Dubai" : companyId == 32 ? "Tiger Group Sharjah" : $"Company {companyId}";

    public static SourceGateDto Evaluate(CollectionsCampaignOptions campaign, PactReceivablesOptions source, IFinancialReconciliationEvidence? evidence, DateTime nowUtc,
        IReadOnlyDictionary<int, DateTime?>? snapshotPublishedUtc = null)
    {
        var result = EvaluateCore(campaign, source, evidence, nowUtc, snapshotPublishedUtc);
        // The snapshot refresh always reads p4/p32AccountReceivables; ProcedureSuffix only concerns the archived direct reader. A sign-off recorded "for V2" would not cover the data in use.
        var warning = string.IsNullOrEmpty(source.ProcedureSuffix) ? null
            : $"ProcedureSuffix \"{source.ProcedureSuffix}\" is configured, but the receivables snapshot is always refreshed from p4/p32AccountReceivables (no suffix). A sign-off must cover the procedure that is actually read.";
        return result with { LedgerVerificationAttested = result.SignOffRecorded, Warning = warning };
    }

    private static SourceGateDto EvaluateCore(CollectionsCampaignOptions campaign, PactReceivablesOptions source, IFinancialReconciliationEvidence? evidence, DateTime nowUtc,
        IReadOnlyDictionary<int, DateTime?>? snapshotPublishedUtc)
    {
        var reference = string.IsNullOrWhiteSpace(campaign.ReconciliationReference) ? null : campaign.ReconciliationReference.Trim();
        var signed = campaign.FinancialSourceValidated;
        int[] companies = [4, 32];
        if (!signed)
            return Global(false, SignOffMissing, "No reconciliation sign-off is recorded for the configured PACT procedure (Collections:Campaigns:FinancialSourceValidated is off). A person must review the reconciliation and record the sign-off.", false, reference, companies);
        if (!string.Equals(campaign.ValidatedProcedureSuffix ?? "", source.ProcedureSuffix ?? "", StringComparison.Ordinal))
            return Global(false, ProcedureChanged, $"The sign-off was recorded for procedure suffix \"{campaign.ValidatedProcedureSuffix}\" but \"{source.ProcedureSuffix}\" is configured now; it must be reconciled again.", true, reference, companies);
        if (!campaign.RequireAutomatedReconciliation || evidence is null)
            return Global(true, Reconciled, "Reconciled by sign-off only (the automated snapshot-consistency check is not required by configuration).", true, reference, companies);

        var items = evidence.Evidence(nowUtc, Math.Max(1, campaign.ReconciliationMaxAgeHours), snapshotPublishedUtc);
        var perCompany = companies.Select(c => CompanyGate(c, items.FirstOrDefault(i => i.CompanyId == c), campaign)).ToList();
        var closed = perCompany.Where(c => !c.Reconciled).ToList();
        var held = perCompany.Sum(c => c.HeldUnits.Count);
        if (closed.Count == 0)
        {
            var at = perCompany.Select(c => c.EvidenceAtUtc).Where(a => a is not null).DefaultIfEmpty(null).Min();
            if (held == 0)
                return new(true, Reconciled, "Reconciled: a person has signed off the procedure against PACT, and the snapshot is consistent with a fresh read of that procedure for both companies. (The automated check alone is not an independent ledger verification.)", at, 0, true, reference, Companies: perCompany);
            return new(true, ReconciledWithHeldUnits, $"Reconciled for every unit that agrees with PACT; {held:N0} unit(s) differ persistently from the PACT procedure after a refresh and are held back individually ({string.Join("; ", perCompany.Where(c => c.HeldUnits.Count > 0).Select(c => $"{c.CompanyName}: {c.HeldUnits.Count:N0}"))}). That is a synchronization or mapping defect to investigate, not a reason to stop the other customers.", at, held, true, reference, Companies: perCompany);
        }
        // The overall code is the most serious closed company's (differences, then an incomplete check, then a check not run, then a superseded or expired one); the message names every closed company.
        string[] order = [EvidenceFailed, EvidenceError, EvidenceNotRun, EvidenceSuperseded, EvidenceExpired];
        var worst = closed.OrderBy(c => Array.IndexOf(order, c.Code) is var i && i >= 0 ? i : order.Length).First();
        var message = string.Join(" ", closed.Select(c => $"{c.CompanyName}: {c.Message}"));
        if (closed.Count < perCompany.Count) message += $" {string.Join(" and ", perCompany.Where(c => c.Reconciled).Select(c => c.CompanyName))} {(perCompany.Count(c => c.Reconciled) == 1 ? "is" : "are")} reconciled and can still be sent.";
        return new(false, worst.Code, message, closed.Select(c => c.EvidenceAtUtc).Where(a => a is not null).DefaultIfEmpty(null).Max(), closed.Sum(c => c.HeldUnits.Count) + held, true, reference, Companies: perCompany);
    }

    private static SourceGateDto Global(bool reconciled, string code, string message, bool signOff, string? reference, int[] companies) =>
        new(reconciled, code, message, null, 0, signOff, reference, Companies: companies.Select(c => new SourceGateCompanyDto(c, CompanyName(c), reconciled, code, message, null, null, [])).ToList());

    private static SourceGateCompanyDto CompanyGate(int company, ReconciliationEvidenceItem? item, CollectionsCampaignOptions campaign)
    {
        var name = CompanyName(company);
        SourceGateCompanyDto Closed(string code, string message) => new(company, name, false, code, message, item?.AtUtc, item?.SnapshotSuccessUtc, []);
        if (item is null || item.State == ReconciliationStates.NotRun)
            return Closed(EvidenceNotRun, "no snapshot-consistency check has been run for this company. Run it (it compares the snapshot with a fresh read of the same PACT procedure).");
        return item.State switch
        {
            ReconciliationStates.Passed => new(company, name, true, Reconciled, "the snapshot is consistent with a fresh read of the PACT procedure.", item.AtUtc, item.SnapshotSuccessUtc, []),
            // Differences confirmed after a controlled refresh, every differing unit known: those units are held, the rest of the company is consistent and may be sent.
            ReconciliationStates.Failed when item.PersistentAfterRefresh && item.HeldUnitsComplete && item.HeldUnits is { Count: > 0 } held =>
                new(company, name, true, ReconciledWithHeldUnits, $"{held.Count:N0} unit(s) still differ from the PACT procedure after a refresh of the snapshot (a synchronization or mapping defect to investigate: see the diagnostics); they are held back individually, every other unit is consistent.", item.AtUtc, item.SnapshotSuccessUtc, held),
            ReconciliationStates.Failed when item.PersistentAfterRefresh =>
                Closed(EvidenceFailed, $"{item.MismatchUnits:N0} unit(s) differ from the PACT procedure after a refresh and not all of them could be listed (more than the diagnostics keep); the company stays closed until the differences are investigated."),
            ReconciliationStates.Failed =>
                Closed(EvidenceFailed, $"the snapshot-consistency check found differences for {item.MismatchUnits:N0} unit(s) and the snapshot could not be refreshed to resolve them ({item.Message}); refresh the company and run the check again."),
            ReconciliationStates.Error => Closed(EvidenceError, $"the latest snapshot-consistency check could not be completed ({item.Message}). That is not a finding about the data; run it again."),
            ReconciliationStates.Superseded => Closed(EvidenceSuperseded, $"{item.Message} Run the check again (it runs by itself after a scheduled refresh)."),
            ReconciliationStates.Expired => Closed(EvidenceExpired, $"the last passing snapshot-consistency check is older than {campaign.ReconciliationMaxAgeHours} hours; run it again."),
            _ => Closed(EvidenceError, $"the snapshot-consistency evidence is in an unknown state ({item.State}).")
        };
    }
}
