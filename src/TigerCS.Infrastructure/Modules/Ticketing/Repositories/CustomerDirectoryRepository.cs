using TigerCS.Application.Modules.Ticketing.CustomerNames;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>
/// The Customers directory, computed by the database from what TigerCS has
/// persisted: every visible ticket is attributed to one customer identity —
/// CRM Buyer id, else the external-verification pair, else the intake phone
/// (<see cref="CustomerIdentityKind"/>) — and the identities are grouped,
/// counted, ordered by most recent ticket and paged in SQL. The page's rows
/// are then dressed with what each customer's latest ticket snapshotted
/// (name, project/unit, phone). A ticket with no identity at all is not a
/// customer and is left out; nothing here calls CRM.
///
/// <para>
/// The phone fallback groups on the number's <b>canonical</b> form, not on
/// the string the agent typed: "+971501234567", "971501234567",
/// "+971 50 123 4567" and "971-50-123-4567" are one customer with one row
/// and one key, however they were captured. SQL cannot run
/// <see cref="CustomerPhoneNumber.Normalize"/>, so the query drops
/// <see cref="CustomerPhoneNumber.SeparatorCharacters"/> with chained
/// <c>REPLACE()</c> — the same result for a value made of digits and
/// separators — and <see cref="CustomerPhoneNumber.Normalize"/> is applied
/// once more, in memory, to every key this repository emits, so a key is
/// canonical whatever the column held. Stored numbers are never rewritten:
/// the row still shows the customer's number as it was captured.
/// </para>
/// </summary>
public sealed class CustomerDirectoryRepository(TigerCsDbContext dbContext, TigerCS.Application.Modules.Ticketing.Services.ReopenPolicy? reopenPolicy = null, TimeProvider? clock = null) : ICustomerDirectoryRepository
{
    private const int CrmKind = (int)CustomerIdentityKind.Crm;
    private const int ExternalKind = (int)CustomerIdentityKind.External;
    private const int PhoneKind = (int)CustomerIdentityKind.Phone;

    /// <summary>One visible ticket with its resolved identity columns — exactly one of the three identity slots is populated, by kind. <see cref="Phone"/> is the canonical number; <see cref="IntakePhone"/> is what was captured.</summary>
    private sealed class IdentifiedTicket
    {
        public Ticket Ticket { get; init; } = null!;
        public string? IntakePhone { get; init; }
        public string? CanonicalIntakePhone { get; init; }
        public int Kind { get; init; }
        public int? CrmId { get; init; }
        public string? Source { get; init; }
        public string? ExternalId { get; init; }
        public string? Phone { get; init; }
        public DateTime? LastInteractionAtUtc { get; init; }

        /// <summary>The ticket's own phone placed by <see cref="CustomerPhoneLocation"/> (0 Unknown, 1 Within UAE, 2 Outside UAE). Only computed when the caller asks for it (<c>withLocation</c>); 0 otherwise.</summary>
        public int LocationCode { get; init; }
    }

    private IQueryable<IdentifiedTicket> Identified(IReadOnlyCollection<int>? visibleDepartmentIds, Guid? agentScopeEmployeeId = null, Guid? ownerEmployeeId = null, bool withLocation = false)
    {
        var tickets = dbContext.Tickets.AsNoTracking().VisibleTo(
            dbContext, visibleDepartmentIds, agentScopeEmployeeId, ownerEmployeeId: ownerEmployeeId, inactivityReopenCutoffUtc: TicketQueryFilters.InactivityReopenCutoff(reopenPolicy, clock));

        // Each ticket's captured number is its FIRST linked intake with a
        // non-empty phone (lowest IntakeRecordId). It is resolved ONCE, as a
        // single pre-aggregated derived table LEFT JOINed to Tickets — not as
        // a correlated TOP(1) subquery in the projection. EF Core inlines a
        // projected subquery everywhere the projection is referenced, and the
        // identity Kind/Phone columns, the WHERE and the canonicalization
        // reference it ~14 times, so the correlated form cost ~14 IntakeRecords
        // look-ups per ticket across EVERY visible ticket — for the list's
        // count query, its page query and, because the phone-keyed profile can
        // only filter after the identity is computed, for a profile too. That
        // grew with the ticket table until the default 30 s command timeout
        // failed both /Customers and /Customers/phone:… (see
        // docs/Customers-Pages-Failure-Investigation.md). Same row, same
        // result, one join.
        var firstIntakePhones =
            from first in dbContext.IntakeRecords
                .Where(i => i.LinkedTicketId != null && i.PhoneNumber != "")
                .GroupBy(i => i.LinkedTicketId!.Value)
                .Select(g => new { TicketId = g.Key, IntakeRecordId = g.Min(i => i.IntakeRecordId) })
            join intake in dbContext.IntakeRecords on first.IntakeRecordId equals intake.IntakeRecordId
            select new { first.TicketId, intake.PhoneNumber };

        // Customers by Location only: the ticket's first interaction phone, resolved like the intake phone — one derived table, one join —
        // for the tickets that have no intake (a call promoted straight from Genesys). Never joined for the list or a profile.
        var firstInteractionPhones =
            from first in dbContext.TicketInteractions
                .Where(i => i.CustomerPhone != "")
                .GroupBy(i => i.TicketId)
                .Select(g => new { TicketId = g.Key, TicketInteractionId = g.Min(i => i.TicketInteractionId) })
            join interaction in dbContext.TicketInteractions on first.TicketInteractionId equals interaction.TicketInteractionId
            select new { first.TicketId, interaction.CustomerPhone };

        var withPhone = withLocation
            ? from t in tickets
              join p in firstIntakePhones on t.TicketId equals p.TicketId into phones
              from p in phones.DefaultIfEmpty()
              join ip in firstInteractionPhones on t.TicketId equals ip.TicketId into interactionPhones
              from ip in interactionPhones.DefaultIfEmpty()
              select new
              {
                  Ticket = t,
                  IntakePhone = p.PhoneNumber,
                  InteractionPhone = ip.CustomerPhone,
                  LastInteractionAtUtc = dbContext.TicketInteractions
                      .Where(i => i.TicketId == t.TicketId)
                      .Max(i => (DateTime?)i.CreatedAtUtc),
              }
            : from t in tickets
              join p in firstIntakePhones on t.TicketId equals p.TicketId into phones
              from p in phones.DefaultIfEmpty()
              select new
              {
                  Ticket = t,
                  IntakePhone = p.PhoneNumber,
                  InteractionPhone = (string?)null,
                  LastInteractionAtUtc = dbContext.TicketInteractions
                      .Where(i => i.TicketId == t.TicketId)
                      .Max(i => (DateTime?)i.CreatedAtUtc),
              };

        // The captured number, canonicalized in SQL. This is what the phone
        // fallback groups, filters and searches on — never the raw column,
        // which differs by however the agent typed the same number. The
        // chain is written out because EF Core translates string.Replace to
        // SQL REPLACE() but cannot call CustomerPhoneNumber.Normalize; it
        // drops exactly CustomerPhoneNumber.SeparatorCharacters, and the
        // keys this repository emits are normalized again in memory.
        var canonicalized = withPhone.Select(x => new
        {
            x.Ticket,
            x.IntakePhone,
            x.InteractionPhone,
            x.LastInteractionAtUtc,
            CanonicalIntakePhone = x.IntakePhone == null
                ? null
                : x.IntakePhone.Trim().Replace("+", "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("/", ""),
        });

        // Customers by Location: the ticket's phone as captured (intake first, else its first interaction's), reduced to its digits in SQL and
        // placed by the SQL twin of CustomerPhoneLocation.Classify. Skipped entirely — constant 0 — for the list and the profile.
        var located = withLocation
            ? canonicalized.Select(x => new { Row = x, Raw = (x.IntakePhone != null && x.IntakePhone != "" ? x.IntakePhone : x.InteractionPhone) })
                .Select(x => new { x.Row, Stripped = x.Raw == null ? null : x.Raw.Trim().Replace("+", "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("/", ""), Plus = x.Raw != null && x.Raw.Trim().StartsWith("+") })
                .Select(x => new { x.Row, x.Stripped, x.Plus, Intl = x.Plus || (x.Stripped != null && x.Stripped.StartsWith("00")) })
                .Select(x => new { x.Row, x.Stripped, x.Intl, Core = x.Stripped == null ? "" : (x.Intl && !x.Plus ? x.Stripped.Substring(2) : x.Stripped) })
                .Select(x => new
                {
                    x.Row,
                    Code = x.Stripped == null || x.Stripped == ""
                           // a letter or any other non-separator character makes the number invalid: nothing but digits may remain
                           || x.Stripped.Replace("0", "").Replace("1", "").Replace("2", "").Replace("3", "").Replace("4", "").Replace("5", "").Replace("6", "").Replace("7", "").Replace("8", "").Replace("9", "") != ""
                        ? 0
                        : x.Intl
                            ? (x.Core.Length >= CustomerPhoneLocation.MinInternationalDigits && x.Core.Length <= CustomerPhoneLocation.MaxInternationalDigits && !x.Core.StartsWith("0")
                                ? (x.Core.StartsWith("971") ? (x.Core.Length == 12 ? 1 : 0) : 2)
                                : 0)
                            : (x.Core.StartsWith("971") && x.Core.Length == 12 ? 1 : 0),
                })
                .Select(x => new { x.Row.Ticket, x.Row.IntakePhone, x.Row.InteractionPhone, x.Row.LastInteractionAtUtc, x.Row.CanonicalIntakePhone, x.Code })
            : canonicalized.Select(x => new { x.Ticket, x.IntakePhone, x.InteractionPhone, x.LastInteractionAtUtc, x.CanonicalIntakePhone, Code = 0 });

        return located.Select(x => new IdentifiedTicket
        {
            Ticket = x.Ticket,
            IntakePhone = x.IntakePhone,
            CanonicalIntakePhone = x.CanonicalIntakePhone,
            LastInteractionAtUtc = x.LastInteractionAtUtc,
            LocationCode = x.Code,
            Kind = x.Ticket.CrmBuyerCustomerId != null ? CrmKind
                : x.Ticket.CustomerVerificationSource != null && x.Ticket.ExternalCustomerId != null ? ExternalKind
                // A number that is nothing but separators ("+") canonicalizes
                // to nothing and is no more an identity than a missing one.
                : x.CanonicalIntakePhone != null && x.CanonicalIntakePhone != "" ? PhoneKind
                : 0,
            CrmId = x.Ticket.CrmBuyerCustomerId,
            Source = x.Ticket.CrmBuyerCustomerId == null && x.Ticket.ExternalCustomerId != null ? x.Ticket.CustomerVerificationSource : null,
            ExternalId = x.Ticket.CrmBuyerCustomerId == null && x.Ticket.CustomerVerificationSource != null ? x.Ticket.ExternalCustomerId : null,
            Phone = x.Ticket.CrmBuyerCustomerId == null && (x.Ticket.CustomerVerificationSource == null || x.Ticket.ExternalCustomerId == null) ? x.CanonicalIntakePhone : null,
        }).Where(x => x.Kind != 0);
    }

    /// <summary>
    /// The identified tickets of the customers this query selects — search, department, dashboard ticket filters and verification source.
    /// ONE method for the list and the by-location counts, so a count on the Dashboard and the list its link opens can only differ if
    /// the query itself does.
    /// </summary>
    private IQueryable<IdentifiedTicket> FilteredCandidates(CustomerDirectoryQuery query, bool withLocation)
    {
        var identified = Identified(query.VisibleDepartmentIds, query.AgentScopeEmployeeId, query.OwnerVisibilityEmployeeId, withLocation);
        var candidates = identified;

        // Search and the department filter select CUSTOMERS (any of whose
        // tickets match), never individual tickets — the counts on a row are
        // always the customer's whole footprint. Expressed as a correlated
        // EXISTS over the same identity columns.
        if (query.Search is { } search)
        {
            // A search term SHAPED LIKE A PHONE NUMBER is also matched as one,
            // canonically: "+971501234567", "971501234567" and
            // "+971 50 123 4567" all find the same customer, whichever of
            // those forms was captured. The gate is LooksLikeNumber rather
            // than "has a digit", so a unit code like "M-401" is still matched
            // as the text it is and not against every number containing 401.
            var searchIsPhoneShaped = CustomerPhoneNumber.LooksLikeNumber(search);
            var searchDigits = searchIsPhoneShaped ? CustomerPhoneNumber.Normalize(search) : string.Empty;

            var matching = identified.Where(m =>
                m.Ticket.TicketNumber.Contains(search)
                || (m.Ticket.CrmBuyerCustomerName != null && m.Ticket.CrmBuyerCustomerName.Contains(search))
                || (m.Ticket.ExternalCustomerName != null && m.Ticket.ExternalCustomerName.Contains(search))
                || (m.Ticket.ExternalCustomerEmail != null && m.Ticket.ExternalCustomerEmail.Contains(search))
                || (m.Ticket.CrmBuyerUnitNumber != null && m.Ticket.CrmBuyerUnitNumber.Contains(search))
                || (m.Ticket.CrmBuyerProjectName != null && m.Ticket.CrmBuyerProjectName.Contains(search))
                || (m.Ticket.ManualUnitNumber != null && m.Ticket.ManualUnitNumber.Contains(search))
                || (m.Ticket.ManualProjectName != null && m.Ticket.ManualProjectName.Contains(search))
                || (m.Ticket.ExternalCustomerId != null && m.Ticket.ExternalCustomerId.Contains(search))
                || (m.IntakePhone != null && m.IntakePhone.Contains(search))
                || (searchIsPhoneShaped && m.CanonicalIntakePhone != null && m.CanonicalIntakePhone.Contains(searchDigits))
                // A name or e-mail an agent typed in is searchable too (it is what the row will be shown under).
                || (m.CanonicalIntakePhone != null && dbContext.CustomerContactDetails.Any(c =>
                    c.PhoneKey == m.CanonicalIntakePhone && (c.Name.Contains(search) || (c.Email != null && c.Email.Contains(search)))))
                || dbContext.TicketInteractions.Any(i => i.TicketId == m.Ticket.TicketId
                    && ((i.CustomerName != null && i.CustomerName.Contains(search))
                        || i.CustomerPhone.Contains(search)
                        || (searchIsPhoneShaped && i.CustomerPhone.Trim().Replace("+", "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("/", "").Contains(searchDigits)))));
            candidates = candidates.Where(x => matching.Any(m =>
                m.Kind == x.Kind
                && ((x.Kind == CrmKind && m.CrmId == x.CrmId)
                    || (x.Kind == ExternalKind && m.Source == x.Source && m.ExternalId == x.ExternalId)
                    || (x.Kind == PhoneKind && m.Phone == x.Phone))));
        }

        if (query.DepartmentId is { } departmentId)
        {
            var inDepartment = identified.Where(m => m.Ticket.CurrentDepartmentId == departmentId);
            candidates = candidates.Where(x => inDepartment.Any(m =>
                m.Kind == x.Kind
                && ((x.Kind == CrmKind && m.CrmId == x.CrmId)
                    || (x.Kind == ExternalKind && m.Source == x.Source && m.ExternalId == x.ExternalId)
                    || (x.Kind == PhoneKind && m.Phone == x.Phone))));
        }

        // The Dashboard's own filters (date range, department, agent, channel, request type, status, priority) select customers exactly as
        // the Dashboard's Customers by Location card counts them: customers with at least one visible ticket matching ALL of them at once.
        if (query.TicketFilter is { } ticketFilter)
        {
            var qualifying = identified.Where(m => true);
            if (ticketFilter.CreatedFromUtc is { } createdFrom) qualifying = qualifying.Where(m => m.Ticket.CreatedAtUtc >= createdFrom);
            if (ticketFilter.CreatedToUtc is { } createdTo) qualifying = qualifying.Where(m => m.Ticket.CreatedAtUtc < createdTo);
            if (ticketFilter.DepartmentId is { } filterDepartment) qualifying = qualifying.Where(m => m.Ticket.CurrentDepartmentId == filterDepartment);
            if (ticketFilter.OwnerEmployeeId is { } owner) qualifying = qualifying.Where(m => m.Ticket.CurrentOwnerEmployeeId == owner);
            if (ticketFilter.RequestTypeId is { } requestType) qualifying = qualifying.Where(m => m.Ticket.RequestTypeId == requestType);
            if (ticketFilter.PriorityId is { } priority) qualifying = qualifying.Where(m => m.Ticket.PriorityId == priority);
            if (ticketFilter.TicketStatus is { } status) qualifying = qualifying.Where(m => m.Ticket.TicketStatus == status);
            if (ticketFilter.ChannelId is { } channel)
            {
                qualifying = qualifying.Where(m => dbContext.TicketInteractions.Any(i => i.TicketId == m.Ticket.TicketId && i.IsOriginatingInteraction && i.ChannelId == channel));
            }

            candidates = candidates.Where(x => qualifying.Any(m =>
                m.Kind == x.Kind
                && ((x.Kind == CrmKind && m.CrmId == x.CrmId)
                    || (x.Kind == ExternalKind && m.Source == x.Source && m.ExternalId == x.ExternalId)
                    || (x.Kind == PhoneKind && m.Phone == x.Phone))));
        }

        if (query.VerificationSource is { } source)
        {
            candidates = source.Equals("Crm", StringComparison.OrdinalIgnoreCase) ? candidates.Where(x => x.Kind == CrmKind)
                : source.Equals("Unverified", StringComparison.OrdinalIgnoreCase) ? candidates.Where(x => x.Kind == PhoneKind)
                : candidates.Where(x => x.Kind == ExternalKind && x.Source == source);
        }


        return candidates;
    }

    public async Task<CustomerLocationCounts> CountByLocationAsync(CustomerDirectoryQuery query, CancellationToken cancellationToken = default)
    {
        // One row per customer, placed once: every located ticket must agree (CustomerPhoneLocation.Resolve), so a customer is in exactly
        // one of the three buckets however many tickets or numbers they have. The whole thing is one grouped COUNT in the database.
        var perCustomer = FilteredCandidates(query with { Location = null }, withLocation: true)
            .GroupBy(x => new { x.Kind, x.CrmId, x.Source, x.ExternalId, x.Phone })
            .Select(g => new
            {
                Within = g.Count(x => x.LocationCode == 1),
                Outside = g.Count(x => x.LocationCode == 2),
            })
            .Select(c => c.Within > 0 && c.Outside == 0 ? 1 : c.Outside > 0 && c.Within == 0 ? 2 : 0);

        var rows = await perCustomer
            .GroupBy(location => location)
            .Select(g => new { Location = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int Of(int location) => rows.Where(r => r.Location == location).Sum(r => r.Count);
        return new CustomerLocationCounts(Of(1), Of(2), Of(0));
    }

    public async Task<CustomerDirectoryPage> ListAsync(CustomerDirectoryQuery query, CancellationToken cancellationToken = default)
    {
        var candidates = FilteredCandidates(query, withLocation: query.Location is not null);

        if (query.Location is { } wanted)
        {
            var wantedCode = (int)wanted;
            candidates = candidates.Where(x => x.Kind != 0);
            var located = candidates
                .GroupBy(x => new { x.Kind, x.CrmId, x.Source, x.ExternalId, x.Phone })
                .Select(g => new
                {
                    g.Key.Kind, g.Key.CrmId, g.Key.Source, g.Key.ExternalId, g.Key.Phone,
                    Location = g.Count(x => x.LocationCode == 1) > 0 && g.Count(x => x.LocationCode == 2) == 0 ? 1
                        : g.Count(x => x.LocationCode == 2) > 0 && g.Count(x => x.LocationCode == 1) == 0 ? 2 : 0,
                })
                .Where(c => c.Location == wantedCode);
            candidates = candidates.Where(x => located.Any(c =>
                c.Kind == x.Kind
                && ((x.Kind == CrmKind && c.CrmId == x.CrmId)
                    || (x.Kind == ExternalKind && c.Source == x.Source && c.ExternalId == x.ExternalId)
                    || (x.Kind == PhoneKind && c.Phone == x.Phone))));
        }

        var grouped = candidates
            .GroupBy(x => new { x.Kind, x.CrmId, x.Source, x.ExternalId, x.Phone })
            .Select(g => new
            {
                g.Key.Kind,
                g.Key.CrmId,
                g.Key.Source,
                g.Key.ExternalId,
                g.Key.Phone,
                TotalTickets = g.Count(),
                OpenTickets = g.Count(x =>
                    x.Ticket.TicketStatus == TicketStatus.Open
                    || x.Ticket.TicketStatus == TicketStatus.InProgress
                    || x.Ticket.TicketStatus == TicketStatus.PendingCustomer
                    || x.Ticket.TicketStatus == TicketStatus.PendingThirdParty),
                // Ticket ids are assigned in creation order, so the highest id
                // is the latest ticket — the row's name/unit/phone snapshot.
                LastTicketId = g.Max(x => x.Ticket.TicketId),
                // The newest ticket that actually snapshotted a customer name.
                // Usually the same ticket; it differs when the latest one came
                // from a source that returned no name (PACT's customerName is
                // routinely null), and without it the row would fall back to a
                // placeholder for a customer the profile can name — the list
                // and the profile would show two different people.
                // Blank counts as no name, exactly as FirstNonEmpty below
                // decides: CrmBuyerCustomerName is stored verbatim, so a
                // whitespace-only one must not shadow an older real name and
                // put the list back out of step with the profile.
                LastNamedTicketId = g.Max(x =>
                    (x.Ticket.CrmBuyerCustomerName != null && x.Ticket.CrmBuyerCustomerName.Trim() != "")
                    || (x.Ticket.ExternalCustomerName != null && x.Ticket.ExternalCustomerName.Trim() != "")
                        ? (long?)x.Ticket.TicketId
                        : null),
                LastTicketCreatedAtUtc = g.Max(x => x.Ticket.CreatedAtUtc),
                LastInteractionAtUtc = g.Max(x => x.LastInteractionAtUtc),
            });

        if (query.OpenOnly)
        {
            grouped = grouped.Where(g => g.OpenTickets > 0);
        }

        var totalCount = await grouped.CountAsync(cancellationToken);
        var page = await grouped
            .OrderByDescending(g => g.LastTicketCreatedAtUtc)
            .ThenByDescending(g => g.LastTicketId)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        if (page.Count == 0)
        {
            return new CustomerDirectoryPage([], totalCount);
        }

        // Dress each row with its latest ticket's snapshot — one query per
        // fact table for the whole page, never one per row. The newest NAMED
        // ticket is read in the same batch, so widening the name costs no
        // extra round trip.
        var lastTicketIds = page.Select(g => g.LastTicketId).ToList();
        var ticketIdsToLoad = lastTicketIds
            .Concat(page.Select(g => g.LastNamedTicketId).OfType<long>())
            .Distinct()
            .ToList();
        var lastTickets = await dbContext.Tickets.AsNoTracking()
            .Where(t => ticketIdsToLoad.Contains(t.TicketId))
            .ToDictionaryAsync(t => t.TicketId, cancellationToken);
        var snapshots = await LoadSnapshotsAsync(lastTicketIds, cancellationToken);

        // Names/e-mails an agent entered by hand, one batched read for the whole page, matched on canonical phone.
        var rowPhoneKeys = page
            .Select(g => snapshots.GetValueOrDefault(g.LastTicketId) is var snap
                ? CustomerPhoneNumber.Normalize(snap.IntakePhone ?? snap.InteractionPhone ?? g.Phone)
                : string.Empty)
            .Where(k => k.Length > 0)
            .Distinct()
            .ToList();
        var manualContacts = rowPhoneKeys.Count == 0
            ? new Dictionary<string, CustomerContactDetail>()
            : await dbContext.CustomerContactDetails.AsNoTracking()
                .Where(c => rowPhoneKeys.Contains(c.PhoneKey))
                .ToDictionaryAsync(c => c.PhoneKey, cancellationToken);

        var rows = new List<CustomerDirectoryRowDto>(page.Count);
        foreach (var g in page)
        {
            var identity = ToIdentity(g.Kind, g.CrmId, g.Source, g.ExternalId, g.Phone);
            var last = lastTickets[g.LastTicketId];
            var snapshot = snapshots.GetValueOrDefault(g.LastTicketId);

            // The latest ticket's name, else the newest ticket that
            // snapshotted one. This matches the profile for every name a
            // TICKET carries. The profile additionally scans conversation
            // names across all of a customer's tickets, which a list page
            // cannot do without a query per row; the row sees the latest
            // ticket's conversation name only, as it always has.
            var namedTicket = g.LastNamedTicketId is { } namedId ? lastTickets.GetValueOrDefault(namedId) : null;
            var manual = manualContacts.GetValueOrDefault(CustomerPhoneNumber.Normalize(snapshot.IntakePhone ?? snapshot.InteractionPhone ?? g.Phone));
            var displayName = manual?.Name
                ?? DisplayNameFor(last, snapshot)
                ?? CustomerNameResolver.Resolve(namedTicket?.CrmBuyerCustomerName, namedTicket?.ExternalCustomerName).Name;

            rows.Add(new CustomerDirectoryRowDto(
                identity.Key,
                identity.Kind.ToString(),
                displayName,
                // The number as the latest ticket captured it — the canonical
                // form identifies the customer, it is not how they are shown.
                snapshot.IntakePhone ?? snapshot.InteractionPhone ?? identity.PhoneNumber,
                last.CrmBuyerProjectName ?? last.ManualProjectName ?? snapshot.SnapshotProperty,
                last.CrmBuyerUnitNumber ?? last.ManualUnitNumber ?? snapshot.SnapshotUnit,
                VerificationSourceFor(identity),
                g.OpenTickets,
                g.TotalTickets,
                g.LastTicketId,
                last.TicketNumber,
                last.TicketStatus.ToString(),
                g.LastTicketCreatedAtUtc,
                g.LastInteractionAtUtc));
        }

        return new CustomerDirectoryPage(rows, totalCount);
    }

    public async Task<CustomerDirectoryProfileDto?> GetProfileAsync(
        CustomerIdentity identity, IReadOnlyCollection<int>? visibleDepartmentIds, CancellationToken cancellationToken = default,
        Guid? agentScopeEmployeeId = null, Guid? ownerEmployeeId = null)
    {
        var identified = Identified(visibleDepartmentIds, agentScopeEmployeeId, ownerEmployeeId);
        var own = identity.Kind switch
        {
            CustomerIdentityKind.Crm => identified.Where(x => x.Kind == CrmKind && x.CrmId == identity.CrmBuyerCustomerId),
            CustomerIdentityKind.External => identified.Where(x => x.Kind == ExternalKind && x.Source == identity.ExternalSource && x.ExternalId == identity.ExternalCustomerId),
            // identity.PhoneNumber is canonical (CustomerIdentity.Phone
            // normalizes) and x.Phone is the column canonicalized in SQL, so
            // phone:971501234567 and a bookmarked phone:%2B971501234567 open
            // the same profile.
            _ => identified.Where(x => x.Kind == PhoneKind && x.Phone == identity.PhoneNumber),
        };

        var tickets = await own
            .OrderByDescending(x => x.Ticket.CreatedAtUtc)
            .Select(x => new { x.Ticket, x.IntakePhone })
            .ToListAsync(cancellationToken);
        if (tickets.Count == 0)
        {
            return null;
        }

        var ticketIds = tickets.Select(x => x.Ticket.TicketId).ToList();
        var snapshots = await LoadSnapshotsAsync(ticketIds, cancellationToken);

        var lastStatusChange = await dbContext.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => ticketIds.Contains(h.TicketId))
            .GroupBy(h => h.TicketId)
            .Select(g => new { TicketId = g.Key, At = g.Max(h => h.OccurredAtUtc) })
            .ToDictionaryAsync(x => x.TicketId, x => x.At, cancellationToken);
        var lastNote = await dbContext.TicketNotes.AsNoTracking()
            .Where(n => ticketIds.Contains(n.TicketId))
            .GroupBy(n => n.TicketId)
            .Select(g => new { TicketId = g.Key, At = g.Max(n => n.CreatedAtUtc) })
            .ToDictionaryAsync(x => x.TicketId, x => x.At, cancellationToken);
        var resolvedAt = await dbContext.TicketResolutions.AsNoTracking()
            .Where(r => ticketIds.Contains(r.TicketId) && r.IsCurrent)
            .Select(r => new { r.TicketId, r.ResolvedAtUtc })
            .ToDictionaryAsync(x => x.TicketId, x => x.ResolvedAtUtc, cancellationToken);

        var channelNames = await dbContext.Channels.AsNoTracking()
            .Select(c => new { c.ChannelId, c.Name })
            .ToDictionaryAsync(c => c.ChannelId, c => c.Name, cancellationToken);
        var ticketNumbers = tickets.ToDictionary(x => x.Ticket.TicketId, x => x.Ticket.TicketNumber);
        var interactions = await dbContext.TicketInteractions.AsNoTracking()
            .Where(i => ticketIds.Contains(i.TicketId))
            .OrderByDescending(i => i.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        var ticketDtos = tickets.Select(x =>
        {
            var t = x.Ticket;
            var snapshot = snapshots.GetValueOrDefault(t.TicketId);
            var lastActivity = new[]
            {
                t.CreatedAtUtc,
                lastStatusChange.GetValueOrDefault(t.TicketId),
                lastNote.GetValueOrDefault(t.TicketId),
                resolvedAt.TryGetValue(t.TicketId, out var resolved) ? resolved : default,
            }.Max();

            return new CustomerDirectoryTicketDto(
                t.TicketId, t.TicketNumber, t.TicketStatus.ToString(), t.PriorityId, t.CurrentDepartmentId, t.CreatedAtUtc,
                t.CrmBuyerProjectName ?? t.ManualProjectName ?? snapshot.SnapshotProperty,
                t.CrmBuyerUnitNumber ?? t.ManualUnitNumber ?? snapshot.SnapshotUnit,
                t.RequestSummary,
                lastActivity,
                resolvedAt.TryGetValue(t.TicketId, out var r) ? r : null,
                t.VerificationStatus.ToString());
        }).ToList();

        var units = tickets
            .Select(x => new
            {
                Project = x.Ticket.CrmBuyerProjectName ?? x.Ticket.ManualProjectName ?? snapshots.GetValueOrDefault(x.Ticket.TicketId).SnapshotProperty,
                Unit = x.Ticket.CrmBuyerUnitNumber ?? x.Ticket.ManualUnitNumber ?? snapshots.GetValueOrDefault(x.Ticket.TicketId).SnapshotUnit,
                Source = x.Ticket.CrmBuyerUnitId != null ? "Crm" : x.Ticket.ExternalUnitId != null ? x.Ticket.CustomerVerificationSource ?? "External" : "Manual",
                x.Ticket.CrmBuyerUnitId,
                x.Ticket.ExternalUnitId,
                x.Ticket.CreatedAtUtc,
            })
            .Where(u => u.Project is not null || u.Unit is not null)
            .GroupBy(u => new { Project = u.Project?.Trim().ToUpperInvariant(), Unit = u.Unit?.Trim().ToUpperInvariant() })
            .Select(g =>
            {
                var latest = g.OrderByDescending(u => u.CreatedAtUtc).First();
                return new CustomerDirectoryUnitDto(latest.Project, latest.Unit, latest.Source, g.Count(), latest.CreatedAtUtc, latest.CrmBuyerUnitId, latest.ExternalUnitId);
            })
            .OrderByDescending(u => u.LastTicketAtUtc)
            .ToList();

        // One entry per real number, newest capture first: the same number
        // written "+971501234567" on one ticket and "971501234567" on the
        // next is one phone, shown the way it was most recently captured.
        var phones = tickets.Select(x => x.IntakePhone)
            .Concat(interactions.Select(i => i.CustomerPhone))
            .Concat([identity.PhoneNumber])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .DistinctBy(p => CustomerPhoneNumber.Normalize(p) is { Length: > 0 } canonical ? canonical : p, StringComparer.Ordinal)
            .ToList();
        // Emails, best-known first: the address the verifying source holds
        // for this customer, then whatever a conversation captured. Newest
        // ticket first (tickets is already ordered that way), deduplicated
        // case-insensitively because "F.Noor@Example.com" and
        // "f.noor@example.com" are one mailbox.
        // An agent-entered record wins for the name and leads the e-mails: it is the most deliberate, most recent statement about
        // who this is. Matched on the canonical form of every number the customer was seen on; the freshest record is used.
        var phoneKeys = phones.Select(CustomerPhoneNumber.Normalize).Where(k => k.Length > 0).Distinct().ToList();
        var manualContact = phoneKeys.Count == 0
            ? null
            : (await dbContext.CustomerContactDetails.AsNoTracking()
                .Where(c => phoneKeys.Contains(c.PhoneKey))
                .ToListAsync(cancellationToken))
                .OrderByDescending(c => c.UpdatedAtUtc)
                .FirstOrDefault();

        var emails = (manualContact?.Email is { } manualEmail ? new string?[] { manualEmail } : Array.Empty<string?>())
            .Concat(tickets.Select(x => x.Ticket.ExternalCustomerEmail))
            .Concat(interactions.Select(i => i.CustomerEmail))
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var last = tickets[0].Ticket;
        // The latest ticket first; then the same precedence widened across
        // every ticket this customer has, so a name captured once is not lost
        // because their most recent ticket happened not to carry one.
        var displayName = manualContact?.Name
            ?? DisplayNameFor(last, snapshots.GetValueOrDefault(last.TicketId))
            ?? CustomerNameResolver.FirstValid(tickets.Select(x => x.Ticket.CrmBuyerCustomerName).ToArray())
            ?? CustomerNameResolver.FirstValid(tickets.Select(x => x.Ticket.ExternalCustomerName).ToArray())
            ?? CustomerNameResolver.FirstValid(interactions.Select(i => i.CustomerName).ToArray());

        return new CustomerDirectoryProfileDto(
            identity.Key,
            identity.Kind.ToString(),
            displayName,
            phones,
            emails,
            VerificationSourceFor(identity),
            identity.CrmBuyerCustomerId,
            identity.ExternalSource,
            identity.ExternalCustomerId,
            ticketDtos.Count(t => t.TicketStatus is "Open" or "InProgress" or "PendingCustomer" or "PendingThirdParty"),
            ticketDtos.Count,
            tickets[^1].Ticket.CreatedAtUtc,
            last.CreatedAtUtc,
            last.TicketId,
            units,
            ticketDtos,
            interactions.Select(i => new CustomerDirectoryInteractionDto(
                i.TicketInteractionId, i.TicketId, ticketNumbers.GetValueOrDefault(i.TicketId, string.Empty), i.ChannelId,
                channelNames.GetValueOrDefault(i.ChannelId), i.Direction, i.InteractionStartedAtUtc ?? i.CreatedAtUtc, i.EndedAtUtc,
                i.EndedAtUtc is null ? "Active" : "Ended", i.GenesysAgentName, i.IsOriginatingInteraction)).ToList());
    }

    // ---- helpers ----

    /// <summary>The per-ticket facts that are not on the Ticket row itself: intake phone, originating interaction name/phone, requester snapshot.</summary>
    private readonly record struct TicketSnapshot(string? IntakePhone, string? InteractionName, string? InteractionPhone, string? SnapshotName, string? SnapshotProperty, string? SnapshotUnit);

    private async Task<Dictionary<long, TicketSnapshot>> LoadSnapshotsAsync(List<long> ticketIds, CancellationToken cancellationToken)
    {
        var intakes = await dbContext.IntakeRecords.AsNoTracking()
            .Where(i => i.LinkedTicketId != null && ticketIds.Contains(i.LinkedTicketId.Value) && i.PhoneNumber != "")
            .OrderBy(i => i.IntakeRecordId)
            .Select(i => new { TicketId = i.LinkedTicketId!.Value, i.PhoneNumber })
            .ToListAsync(cancellationToken);
        var originating = await dbContext.TicketInteractions.AsNoTracking()
            .Where(i => ticketIds.Contains(i.TicketId))
            .OrderByDescending(i => i.IsOriginatingInteraction).ThenBy(i => i.TicketInteractionId)
            .Select(i => new { i.TicketId, i.CustomerName, i.CustomerPhone })
            .ToListAsync(cancellationToken);
        var requester = await dbContext.TicketRequesterSnapshots.AsNoTracking()
            .Where(s => ticketIds.Contains(s.TicketId))
            .Select(s => new { s.TicketId, s.SnapshotContactDisplayName, s.SnapshotPropertyName, s.SnapshotUnitNumber })
            .ToListAsync(cancellationToken);

        var intakeByTicket = intakes.GroupBy(i => i.TicketId).ToDictionary(g => g.Key, g => g.First().PhoneNumber);
        var interactionByTicket = originating.GroupBy(i => i.TicketId).ToDictionary(g => g.Key, g => g.First());
        var requesterByTicket = requester.GroupBy(s => s.TicketId).ToDictionary(g => g.Key, g => g.First());

        return ticketIds.Distinct().ToDictionary(id => id, id => new TicketSnapshot(
            intakeByTicket.GetValueOrDefault(id),
            interactionByTicket.GetValueOrDefault(id)?.CustomerName,
            interactionByTicket.GetValueOrDefault(id)?.CustomerPhone,
            requesterByTicket.GetValueOrDefault(id)?.SnapshotContactDisplayName,
            requesterByTicket.GetValueOrDefault(id)?.SnapshotPropertyName,
            requesterByTicket.GetValueOrDefault(id)?.SnapshotUnitNumber));
    }

    /// <summary>The grouped columns as an identity. <see cref="CustomerIdentity.Phone"/> normalizes once more, so the emitted key is canonical whatever SQL left in the column.</summary>
    private static CustomerIdentity ToIdentity(int kind, int? crmId, string? source, string? externalId, string? phone) => kind switch
    {
        CrmKind => CustomerIdentity.Crm(crmId!.Value),
        ExternalKind => CustomerIdentity.External(source!, externalId!),
        _ => CustomerIdentity.Phone(phone!),
    };

    private static string VerificationSourceFor(CustomerIdentity identity) => identity.Kind switch
    {
        CustomerIdentityKind.Crm => "Crm",
        CustomerIdentityKind.External => identity.ExternalSource!,
        _ => "Unverified",
    };

    /// <summary>
    /// The best REAL name this customer's latest ticket persisted, in
    /// descending order of how well the source knew them:
    /// <list type="number">
    /// <item>the CRM Buyer snapshot — a name from the system of record;</item>
    /// <item>the verified external snapshot (PACT/Tasleeh's own
    /// <c>customerName</c>) — a name from a source that verified them;</item>
    /// <item>what the originating call/chat reported — a name a human typed
    /// or a channel supplied;</item>
    /// <item>the legacy requester snapshot.</item>
    /// </list>
    /// Never an id dressed as a name, and never a manufactured one: when all
    /// four are empty this returns null and the Web decides what placeholder
    /// to show.
    /// </summary>
    private static string? DisplayNameFor(Ticket last, TicketSnapshot snapshot) =>
        CustomerNameResolver.Resolve(last.CrmBuyerCustomerName, last.ExternalCustomerName, snapshot.InteractionName, snapshot.SnapshotName).Name;

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
