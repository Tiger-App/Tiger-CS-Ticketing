namespace TigerCS.Domain.Modules.Collections;

/// <summary>How the CRM side of a unit looks: no eligible sale, exactly one customer, several customers, or CRM not loaded at all.</summary>
public enum CrmLinkStatus
{
    /// <summary>The CRM owner data was never loaded (or is disabled): PACT alone is used, nothing is implied about CRM.</summary>
    Unavailable = 0,
    /// <summary>CRM is loaded and holds no eligible (Sold / Contract, not cancelled) sale of this unit.</summary>
    None = 1,
    /// <summary>Exactly one distinct CRM customer holds an eligible sale of the unit.</summary>
    Single = 2,
    /// <summary>More than one distinct CRM customer holds an eligible sale: nobody is chosen, the match needs review.</summary>
    Ambiguous = 3,
    /// <summary>The CRM sale exists but cannot be tied to ONE company: the same unit key is held by PACT accounts of both companies (or the tower is in both). Nobody is chosen; the unit is flagged.</summary>
    CompanyAmbiguous = 4
}

/// <summary>A contact as one source holds it. Phone / Email are already normalised (empty = missing or invalid); Name is trimmed text (empty = missing).</summary>
public sealed record SourceContact(string Name, string Phone, string Email)
{
    public static readonly SourceContact Empty = new("", "", "");
}

/// <summary>The contact to use and where each value came from (<c>Crm</c>, <c>Pact</c> or empty), plus the review reasons the linking raised.</summary>
public sealed record LinkedContact(
    string Name, string Phone, string Email, string NameSource, string PhoneSource, string EmailSource,
    CrmLinkStatus CrmStatus)
{
    public bool CrmCustomerAmbiguous => CrmStatus == CrmLinkStatus.Ambiguous;
    public bool CrmCompanyAmbiguous => CrmStatus == CrmLinkStatus.CompanyAmbiguous;
    public bool HasContact => Phone.Length > 0 || Email.Length > 0;
}

/// <summary>
/// The single definition of how CRM and PACT contact data are combined (Collections lists, Campaigns and Payment Summary all use it; SQL mirrors it in
/// <c>usp_Collections_GetCampaignUnits</c>). The rules:
/// <list type="number">
/// <item>CRM first: when exactly one eligible CRM customer holds the unit, its name / phone / e-mail win.</item>
/// <item>PACT completes only what CRM lacks (a missing name, phone or e-mail) and only for a uniquely identified CRM customer; an existing CRM value is never replaced.</item>
/// <item>Several CRM customers: nobody is chosen - CRM is not used at all and the unit is flagged; PACT's own contact stays.</item>
/// <item>Different CRM and PACT phones or e-mails are NOT a conflict: CRM wins and nothing is flagged. Customers are never merged by phone, e-mail or name.</item>
/// </list>
/// </summary>
public static class CollectionsContactLinker
{
    public static LinkedContact Link(SourceContact pact, SourceContact crm, CrmLinkStatus status)
    {
        if (status != CrmLinkStatus.Single)
            return new(pact.Name, pact.Phone, pact.Email, pact.Name.Length > 0 ? "Pact" : "", pact.Phone.Length > 0 ? "Pact" : "", pact.Email.Length > 0 ? "Pact" : "", status);

        // CRM is the authoritative contact source: every CRM value wins, even when PACT holds a different phone or e-mail (that is not a conflict and raises no review flag).
        // PACT is only a fallback for a value CRM lacks, and only here, where exactly one CRM customer is tied to the unit (a reliable identity).
        string Pick(string fromCrm, string fromPact) => fromCrm.Length > 0 ? fromCrm : fromPact;
        string Source(string value, string fromCrm) => value.Length == 0 ? "" : fromCrm.Length > 0 ? "Crm" : "Pact";
        var name = Pick(crm.Name, pact.Name); var phone = Pick(crm.Phone, pact.Phone); var email = Pick(crm.Email, pact.Email);
        return new(name, phone, email, Source(name, crm.Name), Source(phone, crm.Phone), Source(email, crm.Email), status);
    }

    /// <summary>
    /// The contact for a CAMPAIGN call (preview, review records, exports, Genesys send). CRM is the only source of the MOBILE NUMBER: the phone is the CRM customer's own mobile when exactly one
    /// eligible CRM buyer holds the unit, and is otherwise empty (no CRM buyer, several buyers, ambiguous company, CRM unavailable). The PACT phone is never used, not even as a fallback, so a
    /// missing or invalid PACT phone cannot hold a customer back and a PACT phone can never be called. Name and e-mail keep the <see cref="Link"/> rules. An empty phone becomes NoValidContact
    /// (Needs review) in the caller; CRM's value is validated here, so an invalid CRM mobile is empty too.
    /// </summary>
    public static LinkedContact LinkForCampaign(SourceContact pact, SourceContact crm, CrmLinkStatus status)
    {
        var linked = Link(pact, crm, status);
        var crmPhone = status == CrmLinkStatus.Single ? Review.PhoneNormalizer.Normalize(crm.Phone).E164 : "";
        return linked with { Phone = crmPhone, PhoneSource = crmPhone.Length > 0 ? "Crm" : "" };
    }

    /// <summary>The review flags a linking raises (CrmCustomerAmbiguous, CrmCompanyAmbiguous) and NoValidContact when the linked contact has neither phone nor e-mail.</summary>
    public static CollectionsCampaignFlags Flags(LinkedContact linked, bool contactRequired)
    {
        var flags = CollectionsCampaignFlags.None;
        if (linked.CrmCustomerAmbiguous) flags |= CollectionsCampaignFlags.CrmCustomerAmbiguous;
        if (linked.CrmCompanyAmbiguous) flags |= CollectionsCampaignFlags.CrmCompanyAmbiguous;
        if (contactRequired && !linked.HasContact) flags |= CollectionsCampaignFlags.NoValidContact;
        return flags;
    }
}
