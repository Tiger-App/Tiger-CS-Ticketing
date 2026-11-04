using TigerCS.Application.Modules.Notifications.Services;

namespace TigerCS.Application.Modules.Ticketing.CustomerNames;

/// <summary>Where a resolved customer name came from — which decides whether it also says anything about who the customer is.</summary>
public enum CustomerNameSource
{
    /// <summary>No supported source holds a valid name.</summary>
    None,

    /// <summary>The name snapshotted from a Tiger CRM buyer match (<c>Ticket.CrmBuyerCustomerName</c>): a verified identity.</summary>
    VerifiedCrm,

    /// <summary>The name snapshotted from an external source that verified the customer (PACT / Tasleeh): a verified identity.</summary>
    VerifiedExternal,

    /// <summary>A name the customer or a channel supplied (website chat form, call, conversation): known, but not verified.</summary>
    Submitted
}

/// <summary>A customer name and the kind of evidence behind it.</summary>
public readonly record struct ResolvedCustomerName(string? Name, CustomerNameSource Source)
{
    public static ResolvedCustomerName None => new(null, CustomerNameSource.None);

    public bool HasName => Name is not null;

    /// <summary>True only when the name came from a source that verified the customer — a known name is not a verified identity.</summary>
    public bool IsVerified => Source is CustomerNameSource.VerifiedCrm or CustomerNameSource.VerifiedExternal;
}

/// <summary>
/// The one rule for which name a customer is shown under, used by Ticket Details, the Customers directory and the Customer Profile so
/// they cannot disagree. In order: the CRM buyer snapshot, the verified external snapshot, then any name that was merely submitted
/// (form, call, conversation). Every candidate must be a real name — a source's placeholder (<c>NOT_REGISTERED</c>, <c>UNKNOWN</c>,
/// <c>N/A</c>), a blank or a failed lookup's empty result is skipped, so it can never replace, or hide, a valid name from another
/// source. When nothing qualifies the result is <see cref="ResolvedCustomerName.None"/> and the caller states that honestly
/// ("Unnamed caller") instead of printing a placeholder.
/// </summary>
public static class CustomerNameResolver
{
    public static ResolvedCustomerName Resolve(string? crmName, string? externalName, params string?[] submittedNames)
    {
        if (CustomerNameValidator.Sanitize(crmName) is { } crm)
        {
            return new(crm, CustomerNameSource.VerifiedCrm);
        }

        if (CustomerNameValidator.Sanitize(externalName) is { } external)
        {
            return new(external, CustomerNameSource.VerifiedExternal);
        }

        return FirstValid(submittedNames) is { } submitted ? new(submitted, CustomerNameSource.Submitted) : ResolvedCustomerName.None;
    }

    /// <summary>The first candidate that is a real name (trimmed), or null.</summary>
    public static string? FirstValid(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (CustomerNameValidator.Sanitize(candidate) is { } name)
            {
                return name;
            }
        }

        return null;
    }
}
