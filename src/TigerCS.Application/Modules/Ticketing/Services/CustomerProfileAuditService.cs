using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>Who, where from and why - the context shared by every field change of one operation.</summary>
/// <param name="ActorEmployeeId">The employee (or the service identity of an integration) making the change.</param>
/// <param name="Source">One of <see cref="CustomerChangeSources"/>; anything else is recorded as the API.</param>
/// <param name="Reason">Why, when the caller said.</param>
/// <param name="RelatedTicketId">The ticket the change was made from, when there is one.</param>
/// <param name="CorrelationId">Groups every change of one operation; also the idempotency key of a retried request.</param>
public sealed record CustomerChangeContext(Guid ActorEmployeeId, string Source, string? Reason, long? RelatedTicketId, Guid CorrelationId);

/// <summary>One field's before and after, as the customer showed them (null = nothing).</summary>
public sealed record CustomerFieldChange(string Field, string? OldValue, string? NewValue);

/// <summary>
/// The single place customer-profile changes are audited, whatever made them: the Customers pages, the API, the AI Agent, a CRM sync or a
/// background process all go through <see cref="RecordAsync"/>, so none can change a customer unaudited.
///
/// <para>
/// It records only actual changes (a field whose value is the same before and after, once blank is read as nothing, writes nothing), one
/// row per changed field, all sharing the operation's correlation id. It only <i>adds</i> the rows to the caller's unit of work and never
/// saves: the caller commits the customer change and its audit rows in one transaction, so there is never a change without its history or
/// history of a change that did not happen. Old and new values are personal data and go only into the history table - never to a log.
/// </para>
/// </summary>
public sealed class CustomerProfileAuditService(
    ICustomerProfileChangeRepository changes,
    IEmployeeRepository employees,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public const string AuditAction = "UpdateCustomerProfile";
    public const string AuditEntityType = "Customer";

    /// <summary>True when this operation (correlation id) was already recorded for the customer: a retry, to be answered as already done.</summary>
    public Task<bool> AlreadyRecordedAsync(Guid customerProfileId, Guid correlationId, CancellationToken cancellationToken = default) =>
        changes.OperationExistsAsync(customerProfileId, correlationId, cancellationToken);

    /// <returns>How many field changes were recorded (0 when nothing actually changed).</returns>
    public async Task<int> RecordAsync(
        Guid customerProfileId, string customerKey, CustomerChangeContext context, IEnumerable<CustomerFieldChange> fieldChanges,
        CancellationToken cancellationToken = default)
    {
        var actual = fieldChanges
            .Select(c => new CustomerFieldChange(c.Field, Blank(c.OldValue), Blank(c.NewValue)))
            .Where(c => !string.Equals(c.OldValue, c.NewValue, StringComparison.Ordinal))
            .DistinctBy(c => (c.Field, c.OldValue, c.NewValue))
            .ToList();
        if (actual.Count == 0)
        {
            return 0;
        }

        var names = await employees.ResolveDisplayNamesAsync([context.ActorEmployeeId], cancellationToken);
        var actorName = names.GetValueOrDefault(context.ActorEmployeeId) ?? "Unknown";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var source = CustomerChangeSources.Normalize(context.Source);

        await changes.AddRangeAsync(
            actual.Select(c => new CustomerProfileChange(
                customerProfileId, customerKey, c.Field, c.OldValue, c.NewValue, context.ActorEmployeeId, actorName, now, source,
                context.Reason, context.RelatedTicketId, context.CorrelationId)),
            cancellationToken);

        // The general audit trail gets one summary row per operation: which fields, never their values.
        await auditWriter.WriteAsync(
            context.ActorEmployeeId, AuditAction, AuditEntityType, customerKey, null,
            string.Join(',', actual.Select(c => c.Field).Distinct()), context.CorrelationId, cancellationToken);

        return actual.Count;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
