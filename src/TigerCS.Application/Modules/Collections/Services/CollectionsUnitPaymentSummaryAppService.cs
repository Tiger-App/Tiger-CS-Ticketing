using System.Data.Common;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>A CRM customer a caller already identified for a unit (name, mobile and e-mail as CRM holds them, before normalisation by the linker).</summary>
public sealed record KnownCrmCustomer(int CustomerId, int? CrmUnitId, SourceContact Contact);

/// <summary>
/// Payment Summary of one unit - the same identity linking as the Receivables and Campaigns lists (<see cref="CollectionsContactLinker"/> over the same CRM owner data
/// and the same PACT snapshot rows), so a unit shows the same customer, contact and amounts everywhere. The search is the unit key (tower + unit code), a missing PACT
/// mobile never matters, only that unit's instalments are returned (never another apartment of the customer), and "PACT has nothing" is not shown as zero.
/// </summary>
public sealed class CollectionsUnitPaymentSummaryAppService(
    CollectionsOptions options, PactReceivablesOptions sourceOptions, ReceivablesSnapshotOptions snapshotOptions, CollectionsAuthorizationService authorization, CollectionsClock clock,
    IPactReceivablesSource source, ILogger<CollectionsUnitPaymentSummaryAppService> logger, CollectionsUnitLinkService? links = null)
{
    public async Task<CollectionsResult<CollectionsUnitPaymentSummaryDto>> GetAsync(
        CollectionsCaller caller, string? unitCode, int? companyId = null, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<CollectionsUnitPaymentSummaryDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled || !sourceOptions.Enabled || source is not IUnitReceivablesSource unitSource)
            return CollectionsResult<CollectionsUnitPaymentSummaryDto>.Fail(CollectionsOutcome.Disabled, "The unit summary needs the local receivables snapshot.");
        var key = CollectionsUnitKey.Normalize(unitCode);
        if (key is null || !CollectionsUnitKey.IsListable(key) || companyId is not (null or 4 or 32))
            return CollectionsResult<CollectionsUnitPaymentSummaryDto>.Fail(CollectionsOutcome.InvalidRequest, "Give a unit code such as TP140-101 (a cancelled or empty code is not a unit) and company 4 or 32.");
        var today = clock.BusinessDate;
        PactUnitReceivables data;
        try { data = await unitSource.ReadUnitAsync(key, companyId, today, cancellationToken); }
        catch (PactReceivablesSourceException ex) { return CollectionsResult<CollectionsUnitPaymentSummaryDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message); }
        catch (Exception ex) when (ex is DbException)
        {
            logger.LogWarning("Unit summary read failed ({ExceptionType}).", ex.GetType().Name);
            return CollectionsResult<CollectionsUnitPaymentSummaryDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The unit could not be read. Please retry.");
        }
        var availability = links is null ? null : await links.GetAvailabilityAsync(cancellationToken);
        return CollectionsResult<CollectionsUnitPaymentSummaryDto>.Ok(Compose(key, today, data, snapshotOptions.MaxAgeMinutes, availability: availability));
    }

    /// <summary>Pure composition of the unit summary from the one bulk read (unit-tested without a database).</summary>
    /// <param name="key">The normalised unit key.</param>
    /// <param name="today">The Dubai business date.</param>
    /// <param name="data">The one bulk read of the unit.</param>
    /// <param name="maxAgeMinutes">Snapshot freshness limit.</param>
    /// <param name="knownCrm">
    /// The CRM customer a caller already identified (phone lookup, ticket): its contact is the CRM side of the link. If the bulk CRM owner data says somebody else (or several people)
    /// holds the unit, the result is flagged <c>CrmOwnershipConflict</c> / <c>CrmCustomerAmbiguous</c> instead of choosing.
    /// </param>
    /// <param name="tenantId">When the caller selected one PACT tenant (a contract found by phone), only that tenant's account of the unit is used.</param>
    /// <param name="availability">The state of the CRM owner data; when it cannot be used the unit is flagged <c>CrmUnavailable</c> (its CRM contact is unconfirmed) instead of reading as "CRM has nothing".</param>
    public static CollectionsUnitPaymentSummaryDto Compose(
        string key, DateOnly today, PactUnitReceivables data, int maxAgeMinutes, KnownCrmCustomer? knownCrm = null, string? tenantId = null, CrmAvailability? availability = null)
    {
        if (tenantId is not null)
            data = data with
            {
                Identities = data.Identities.Where(i => string.Equals(i.TenantId, tenantId, StringComparison.Ordinal)).ToList(),
                Instalments = data.Instalments.Where(i => string.Equals(i.TenantId, tenantId, StringComparison.Ordinal)).ToList()
            };
        var reasons = new List<string>();
        var accounts = data.Identities.GroupBy(i => (i.CompanyId, i.TenantId)).Select(g => g.OrderBy(i => i.UnitId).First()).ToList();
        var oneAccount = accounts.Count == 1 ? accounts[0] : null;
        if (accounts.Select(a => a.CompanyId).Distinct().Count() > 1) reasons.Add("UnitInSeveralCompanies");
        else if (accounts.Count > 1) reasons.Add("SeveralPactCustomers");

        // The company the unit belongs to: the PACT account's, else the only company CRM links it to (never guessed when several).
        var companyId = oneAccount?.CompanyId ?? (data.CrmLinks.Count == 1 ? data.CrmLinks.Keys.Single() : (int?)null);
        data.CrmLinks.TryGetValue(companyId ?? -1, out var crm);
        var status = crm?.Status ?? CrmLinkStatus.None;
        if (knownCrm is not null)
        {
            // The caller already knows the CRM customer. The bulk owner data may only confirm it or raise a review flag - it never replaces the identified customer.
            if (crm is { Status: CrmLinkStatus.Ambiguous }) reasons.Add("CrmCustomerAmbiguous");
            else if (crm is { Status: CrmLinkStatus.Single } && crm.CustomerId != knownCrm.CustomerId) reasons.Add("CrmOwnershipConflict");
            crm = new CrmUnitLink(CrmLinkStatus.Single, 1, knownCrm.CustomerId, knownCrm.CrmUnitId ?? crm?.CrmUnitId ?? 0, crm?.LeadId ?? 0, knownCrm.Contact);
            status = CrmLinkStatus.Single;
        }
        var pact = oneAccount is null ? SourceContact.Empty
            : new SourceContact(oneAccount.FullName.Trim(), CollectionsContactNormalizer.NormalizePhone(oneAccount.Mobile), CollectionsContactNormalizer.NormalizeEmail(oneAccount.Email));
        // CRM first, PACT completes what CRM lacks; several customers / two different people are flagged, never merged.
        var linked = CollectionsContactLinker.Link(pact, crm?.Contact ?? SourceContact.Empty, status);
        if (linked.CrmCustomerAmbiguous && !reasons.Contains("CrmCustomerAmbiguous")) reasons.Add("CrmCustomerAmbiguous");
        if (linked.CrmCompanyAmbiguous) reasons.Add("CrmCompanyAmbiguous");
        // CRM linking is on but its data cannot be used: the contact is PACT-only and UNCONFIRMED - a service problem, not "CRM has no value".
        if (availability is { Blocks: true } && knownCrm is null) reasons.Add("CrmUnavailable");

        var (financial, detail, due, overdue, total, instalments, reason) = Financials(oneAccount, data, today, maxAgeMinutes, accounts.Count > 1);
        var companyStatus = data.Snapshot.Companies.FirstOrDefault(c => c.CompanyId == (oneAccount?.CompanyId ?? companyId));
        var payments = financial is "Available" or "NoDues" ? Figures(oneAccount!, data, today, companyStatus?.LastSuccessUtc) : null;
        var linkStatus = reasons.Count > 0 ? "NeedsReview" : oneAccount is null && crm is null ? "NotFound" : "Linked";
        return new(key, oneAccount?.UnitCode ?? key, oneAccount?.CompanyId ?? companyId, oneAccount?.TowerNumber ?? CollectionsTowerNumber.Parse(key), oneAccount?.TowerName,
            linkStatus, reasons,
            new CollectionsUnitPartyDto(linked.Name, linked.Phone, linked.Email, linked.NameSource, linked.PhoneSource, linked.EmailSource),
            status switch { CrmLinkStatus.Single => "Single", CrmLinkStatus.Ambiguous => "Ambiguous", CrmLinkStatus.CompanyAmbiguous => "CompanyAmbiguous", CrmLinkStatus.None when data.CrmLoaded && availability is not { Blocks: true } => "None", _ => "Unavailable" },
            status == CrmLinkStatus.Single ? crm!.CustomerId : null, status == CrmLinkStatus.Single ? crm!.CrmUnitId : null, oneAccount?.TenantId, oneAccount?.UnitId,
            financial, detail, due, overdue, total, instalments, today, companyStatus?.LastSuccessUtc, companyStatus?.Freshness ?? "Missing", reason, payments);
    }

    /// <summary>
    /// The Payment tab's figures: every unpaid instalment of the unit, classified by the one Dubai month rule (<see cref="CollectionsDueMonth"/>) - Overdue before the month, Due inside it
    /// (any day), Not yet due after it. All sums are taken from this one list, so the cards and the table agree.
    /// </summary>
    private static CollectionsUnitPaymentFiguresDto Figures(PactUnitIdentity account, PactUnitReceivables data, DateOnly today, DateTime? lastUpdatedUtc)
    {
        var rows = data.Instalments.Where(i => i.CompanyId == account.CompanyId && i.TenantId == account.TenantId && i.UnitId == account.UnitId && i.RemainingAmount > 0)
            .OrderBy(i => i.DueDate).ThenBy(i => i.VoucherNumber, StringComparer.Ordinal).ToList();
        var schedule = rows.Select((i, index) => new CollectionsUnitScheduleLineDto(
            i.VoucherNumber.Length > 0 ? $"Voucher {i.VoucherNumber}" : $"Instalment {index + 1}", i.DueDate, i.OriginalAmount, i.PaidAmount, i.RemainingAmount,
            CollectionsDueMonth.Classify(i.DueDate, today) switch { CollectionsDueClass.Overdue => "Overdue", CollectionsDueClass.Due => "Due", _ => "NotYetDue" },
            i.DueDate < today ? today.DayNumber - i.DueDate.DayNumber : null)).ToList();
        return new(CollectionsDueMonth.MonthStart(today), CollectionsDueMonth.MonthEnd(today), schedule, lastUpdatedUtc);
    }

    private static (string Status, string? Detail, decimal? Due, decimal? Overdue, decimal? Total, IReadOnlyList<CollectionsUnitInstalmentDto> Instalments, string? Reason) Financials(
        PactUnitIdentity? account, PactUnitReceivables data, DateOnly today, int maxAgeMinutes, bool ambiguousAccount)
    {
        if (account is null)
            return ambiguousAccount || data.Identities.Count > 0
                ? ("Withheld", "More than one PACT account holds this unit; none is chosen, so no amounts are shown.", null, null, null, [], "SeveralPactAccounts")
                : ("NoFinancialData", "PACT holds no receivable record for this unit. This is not a zero balance.", null, null, null, [], "PactHoldsNoRecord");
        var company = data.Snapshot.Companies.FirstOrDefault(c => c.CompanyId == account.CompanyId);
        if (company is null || !company.HasSnapshot)
            return ("NoFinancialData", "The PACT receivables of this company are not loaded. This is not a zero balance.", null, null, null, [], "CompanySnapshotNotLoaded");
        var rows = data.Instalments.Where(i => i.CompanyId == account.CompanyId && i.TenantId == account.TenantId && i.UnitId == account.UnitId && i.RemainingAmount > 0 && CollectionsDueMonth.IsInScope(i.DueDate, today))
            .OrderBy(i => i.DueDate).ThenBy(i => i.VoucherNumber, StringComparer.Ordinal).ToList();
        var lines = rows.Select((i, index) => new CollectionsUnitInstalmentDto(i.VoucherNumber.Length > 0 ? $"Voucher {i.VoucherNumber}" : $"Instalment {index + 1}", i.DueDate, i.OriginalAmount, i.PaidAmount,
            i.RemainingAmount, CollectionsDueMonth.IsOverdue(i.DueDate, today) ? "Overdue" : "Due", i.DueDate < today ? today.DayNumber - i.DueDate.DayNumber : null)).ToList();
        var due = lines.Where(l => l.Status == "Due").Sum(l => l.RemainingAmount);
        var overdue = lines.Where(l => l.Status == "Overdue").Sum(l => l.RemainingAmount);
        var stale = company.Freshness != "Fresh" ? $" The PACT data is {(company.AgeMinutes is { } age ? age + " minutes" : "not current")} old (limit {maxAgeMinutes})." : "";
        // Unpaid rows exist only when OpenRows > 0; a unit PACT lists with nothing open is confirmed to owe nothing due.
        if (lines.Count == 0)
            return account.AllRows > 0 ? ("NoDues", ("Nothing is due in the current month or earlier." + stale).Trim(), 0m, 0m, 0m, lines, null) : ("NoFinancialData", "PACT holds no receivable record for this unit.", null, null, null, lines, "PactHoldsNoRecord");
        return ("Available", stale.Length > 0 ? stale.Trim() : null, due, overdue, due + overdue, lines, null);
    }
}
