using TigerCS.Application.Modules.Ticketing.CustomerNames;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
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

    /// <summary>
    /// Weights of one ticket's located phone; a customer's <c>SUM</c> of them says how many tickets sit in each place (low digits Within UAE, high
    /// digits Outside UAE): <c>sum % OutsideWeight &gt; 0</c> = some Within ticket, <c>sum / OutsideWeight &gt; 0</c> = some Outside ticket. Only one
    /// side present → that location; none or both (a UAE and a foreign number on one customer: ambiguous) → Unknown.
    /// </summary>
    private const long WithinWeight = 1;

    private const long OutsideWeight = 1_000_000_000;

    /// <summary>One visible ticket with its resolved identity columns — exactly one of the three identity slots is populated, by kind. <see cref="Phone"/> is the canonical number; <see cref="IntakePhone"/> is what was captured.</summary>
    private sealed class IdentifiedTicket
    {
        public Ticket Ticket { get; init; } = null!;
        public string? IntakePhone { get; init; }
        public string? InteractionPhone { get; init; }
        public string? CanonicalIntakePhone { get; init; }
        public int Kind { get; init; }
        public int? CrmId { get; init; }
        public string? Source { get; init; }
        public string? ExternalId { get; init; }
        public string? Phone { get; init; }
        public DateTime? LastInteractionAtUtc { get; init; }

        /// <summary>
        /// The ticket's own phone placed by <see cref="CustomerPhoneLocation"/> as a SUMMABLE weight: <see cref="WithinWeight"/> for a UAE number,
        /// <see cref="OutsideWeight"/> for any other valid international number, 0 for Unknown. A customer's tickets are folded with ONE <c>SUM</c>
        /// (only Within → Within UAE, only Outside → Outside UAE, none or both → Unknown) — a single reference to this column per aggregate, because EF inlines the expression at every reference.
        /// Only computed when the caller asks for it (<c>withLocation</c>); 0 otherwise.
        /// </summary>
        public long LocationWeight { get; init; }

        /// <summary>1 when this ticket matches the Dashboard's ticket filter (<see cref="CustomerDirectoryTicketFilter"/>), else 0 — folded per customer with <c>MAX</c>, in the SAME grouped pass (no correlated subquery). Always 0 when no filter was asked for.</summary>
        public int Qualifies { get; init; }
    }

    private IQueryable<IdentifiedTicket> Identified(IReadOnlyCollection<int>? visibleDepartmentIds, Guid? agentScopeEmployeeId = null, Guid? ownerEmployeeId = null, bool withLocation = false, CustomerDirectoryTicketFilter? ticketFilter = null)
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
        var phoneRows = canonicalized.Select(x => new PhoneRow
        {
            Ticket = x.Ticket, IntakePhone = x.IntakePhone, InteractionPhone = x.InteractionPhone, LastInteractionAtUtc = x.LastInteractionAtUtc,
            CanonicalIntakePhone = x.CanonicalIntakePhone,
            LocationWeight = 0L,   // every member read later must be initialised, or EF cannot translate the read
        });
        var located = withLocation ? WithLocationWeight(phoneRows) : phoneRows;

        var identifiedRows = located.Select(x => new IdentifiedTicket
        {
            Ticket = x.Ticket,
            IntakePhone = x.IntakePhone,
            InteractionPhone = x.InteractionPhone,
            CanonicalIntakePhone = x.CanonicalIntakePhone,
            LastInteractionAtUtc = x.LastInteractionAtUtc,
            LocationWeight = x.LocationWeight,
            Qualifies = 0,   // set by the filter branch below when the Dashboard asked for one
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

        if (ticketFilter is null)
        {
            return identifiedRows;
        }

        // The Dashboard's filters as ONE per-ticket flag, built only when a filter was asked for (so the plain list and profile queries are exactly
        // what they were): the same predicate the Dashboard applies, written once. Customers are then selected by MAX(flag) per identity in the
        // grouped pass — never by a correlated subquery over the whole identification.
        var filter = ticketFilter;
        var createdFrom = filter.CreatedFromUtc;
        var createdTo = filter.CreatedToUtc;
        var filterDepartment = filter.DepartmentId;
        var filterOwner = filter.OwnerEmployeeId;
        var filterRequestType = filter.RequestTypeId;
        var filterPriority = filter.PriorityId;
        var filterStatus = filter.TicketStatus;
        var filterChannel = filter.ChannelId;
        return identifiedRows.Select(x => new IdentifiedTicket
        {
            Ticket = x.Ticket, IntakePhone = x.IntakePhone, InteractionPhone = x.InteractionPhone, CanonicalIntakePhone = x.CanonicalIntakePhone, LastInteractionAtUtc = x.LastInteractionAtUtc,
            LocationWeight = x.LocationWeight, Kind = x.Kind, CrmId = x.CrmId, Source = x.Source, ExternalId = x.ExternalId, Phone = x.Phone,
            Qualifies = (createdFrom == null || x.Ticket.CreatedAtUtc >= createdFrom)
                && (createdTo == null || x.Ticket.CreatedAtUtc < createdTo)
                && (filterDepartment == null || x.Ticket.CurrentDepartmentId == filterDepartment)
                && (filterOwner == null || x.Ticket.CurrentOwnerEmployeeId == filterOwner)
                && (filterRequestType == null || x.Ticket.RequestTypeId == filterRequestType)
                && (filterPriority == null || x.Ticket.PriorityId == filterPriority)
                && (filterStatus == null || x.Ticket.TicketStatus == filterStatus)
                && (filterChannel == null || dbContext.TicketInteractions.Any(i => i.TicketId == x.Ticket.TicketId && i.IsOriginatingInteraction && i.ChannelId == filterChannel))
                    ? 1 : 0,
        });
    }

    /// <summary>
    /// Adds each ticket's <c>LocationWeight</c>: its phone (intake first, else its first interaction's) placed by the rule of
    /// <see cref="CustomerPhoneLocation.Classify"/>. The "digits only" half of that rule is the one part a database must spell differently, and it
    /// is the expensive one, so it is chosen per provider — always the same answer, never the same text:
    /// <list type="bullet">
    /// <item>SQL Server (production): one character-class <c>LIKE '%[^0-9]%'</c>.</item>
    /// <item>SQLite: the portable chain of ten <c>REPLACE</c> calls — larger, but SQLite plans it fine at test scale.</item>
    /// <item>Anything else (the EF InMemory provider the API endpoint tests run on): <see cref="CustomerPhoneLocation.Classify"/> itself, which an
    /// in-memory provider can simply call. The chain makes that provider allocate gigabytes on a handful of rows.</item>
    /// </list>
    /// The SQL branches are the same flat pattern tests on the separator-free digits ("+971" / "971" / "00971" + a 9-digit 5… mobile or an 8-digit
    /// 2/3/4/6/7/9 landline, or a UAE mobile / landline in national form "05…" / "0[234679]…" → Within UAE;
    /// "+" or "00" + 8–15 digits not starting 0/971 → Outside UAE); tests assert every branch agrees with the C# rule on a table of numbers.
    /// </summary>
    private IQueryable<PhoneRow> WithLocationWeight(IQueryable<PhoneRow> rows)
    {
        var provider = dbContext.Database.ProviderName ?? string.Empty;
        var sqlServer = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
        if (!sqlServer && !provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // The EF InMemory provider (API endpoint tests): the C# rule itself.
            return rows.Select(x => new PhoneRow
            {
                Ticket = x.Ticket, IntakePhone = x.IntakePhone, InteractionPhone = x.InteractionPhone, LastInteractionAtUtc = x.LastInteractionAtUtc,
                CanonicalIntakePhone = x.CanonicalIntakePhone,
                LocationWeight = CustomerPhoneLocation.Classify(x.IntakePhone != null && x.IntakePhone != "" ? x.IntakePhone : x.InteractionPhone) == CustomerLocation.WithinUae
                    ? WithinWeight
                    : CustomerPhoneLocation.Classify(x.IntakePhone != null && x.IntakePhone != "" ? x.IntakePhone : x.InteractionPhone) == CustomerLocation.OutsideUae
                        ? OutsideWeight
                        : 0L,
            });
        }

        var staged = rows
            .Select(x => new PhoneRow
            {
                Ticket = x.Ticket, IntakePhone = x.IntakePhone, InteractionPhone = x.InteractionPhone, LastInteractionAtUtc = x.LastInteractionAtUtc,
                CanonicalIntakePhone = x.CanonicalIntakePhone,
                Raw = x.IntakePhone != null && x.IntakePhone != "" ? x.IntakePhone : x.InteractionPhone,
            })
            .Select(x => new PhoneRow
            {
                Ticket = x.Ticket, IntakePhone = x.IntakePhone, InteractionPhone = x.InteractionPhone, LastInteractionAtUtc = x.LastInteractionAtUtc,
                CanonicalIntakePhone = x.CanonicalIntakePhone,
                Stripped = x.Raw == null ? null : x.Raw.Trim().Replace("+", "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("/", ""),
                Plus = x.Raw != null && x.Raw.Trim().StartsWith("+"),
            });

        return sqlServer
            ? staged.Select(x => new PhoneRow
            {
                Ticket = x.Ticket, IntakePhone = x.IntakePhone, InteractionPhone = x.InteractionPhone, LastInteractionAtUtc = x.LastInteractionAtUtc,
                CanonicalIntakePhone = x.CanonicalIntakePhone,
                LocationWeight = x.Stripped == null || EF.Functions.Like(x.Stripped, "%[^0-9]%")
                    ? 0L
                    : EF.Functions.Like(x.Stripped, "9715________") || EF.Functions.Like(x.Stripped, "971[234679]_______") || (!x.Plus && (EF.Functions.Like(x.Stripped, "009715________") || EF.Functions.Like(x.Stripped, "00971[234679]_______") || EF.Functions.Like(x.Stripped, "05________") || EF.Functions.Like(x.Stripped, "0[234679]_______")))
                        ? WithinWeight
                        : (x.Plus && x.Stripped.Length >= 8 && x.Stripped.Length <= 15 && !x.Stripped.StartsWith("0") && !x.Stripped.StartsWith("971"))
                          || (!x.Plus && x.Stripped.StartsWith("00") && x.Stripped.Length >= 10 && x.Stripped.Length <= 17 && !x.Stripped.StartsWith("000") && !x.Stripped.StartsWith("00971"))
                            ? OutsideWeight
                            : 0L,
            })
            : staged.Select(x => new PhoneRow
            {
                Ticket = x.Ticket, IntakePhone = x.IntakePhone, InteractionPhone = x.InteractionPhone, LastInteractionAtUtc = x.LastInteractionAtUtc,
                CanonicalIntakePhone = x.CanonicalIntakePhone,
                LocationWeight = x.Stripped == null
                       || x.Stripped.Replace("0", "").Replace("1", "").Replace("2", "").Replace("3", "").Replace("4", "").Replace("5", "").Replace("6", "").Replace("7", "").Replace("8", "").Replace("9", "") != ""
                    ? 0L
                    : EF.Functions.Like(x.Stripped, "9715________") || EF.Functions.Like(x.Stripped, "9712_______") || EF.Functions.Like(x.Stripped, "9713_______") || EF.Functions.Like(x.Stripped, "9714_______") || EF.Functions.Like(x.Stripped, "9716_______") || EF.Functions.Like(x.Stripped, "9717_______") || EF.Functions.Like(x.Stripped, "9719_______") || (!x.Plus && (EF.Functions.Like(x.Stripped, "009715________") || EF.Functions.Like(x.Stripped, "009712_______") || EF.Functions.Like(x.Stripped, "009713_______") || EF.Functions.Like(x.Stripped, "009714_______") || EF.Functions.Like(x.Stripped, "009716_______") || EF.Functions.Like(x.Stripped, "009717_______") || EF.Functions.Like(x.Stripped, "009719_______") || EF.Functions.Like(x.Stripped, "05________") || EF.Functions.Like(x.Stripped, "02_______") || EF.Functions.Like(x.Stripped, "03_______") || EF.Functions.Like(x.Stripped, "04_______") || EF.Functions.Like(x.Stripped, "06_______") || EF.Functions.Like(x.Stripped, "07_______") || EF.Functions.Like(x.Stripped, "09_______")))
                        ? WithinWeight
                        : (x.Plus && x.Stripped.Length >= 8 && x.Stripped.Length <= 15 && !x.Stripped.StartsWith("0") && !x.Stripped.StartsWith("971"))
                          || (!x.Plus && x.Stripped.StartsWith("00") && x.Stripped.Length >= 10 && x.Stripped.Length <= 17 && !x.Stripped.StartsWith("000") && !x.Stripped.StartsWith("00971"))
                            ? OutsideWeight
                            : 0L,
            });
    }

    /// <summary>One ticket's phone facts on the way to its <see cref="IdentifiedTicket.LocationWeight"/> (a named type, so the provider-specific stages can share it).</summary>
    private sealed class PhoneRow
    {
        public Ticket Ticket { get; init; } = null!;
        public string? IntakePhone { get; init; }
        public string? InteractionPhone { get; init; }
        public DateTime? LastInteractionAtUtc { get; init; }
        public string? CanonicalIntakePhone { get; init; }
        public string? Raw { get; init; }
        public string? Stripped { get; init; }
        public bool Plus { get; init; }
        public long LocationWeight { get; init; }
    }

    /// <summary>
    /// The identified tickets of the customers this query selects — search, department, dashboard ticket filters and verification source.
    /// ONE method for the list and the by-location counts, so a count on the Dashboard and the list its link opens can only differ if
    /// the query itself does.
    /// </summary>
    private IQueryable<IdentifiedTicket> FilteredCandidates(CustomerDirectoryQuery query, bool withLocation)
    {
        // The EXISTS subqueries below only need the identity columns: they use the plain identification, so the (large) location expression is
        // never repeated inside them. Only the outer candidate set carries it.
        var identified = Identified(query.VisibleDepartmentIds, query.AgentScopeEmployeeId, query.OwnerVisibilityEmployeeId);
        // The Dashboard's ticket filter and the location weight ride on the outer candidates only (see IdentifiedTicket.Qualifies / LocationWeight).
        var candidates = withLocation || query.TicketFilter is not null
            ? Identified(query.VisibleDepartmentIds, query.AgentScopeEmployeeId, query.OwnerVisibilityEmployeeId, withLocation, query.TicketFilter)
            : identified;

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
            // A UAE number is found however it was written (local, +971, 00971): the term is also tried in its other spellings.
            var (searchAlt1, searchAlt2) = UaePhoneNumbers.SearchVariants(searchDigits);

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
                || (searchIsPhoneShaped && m.CanonicalIntakePhone != null
                    && (m.CanonicalIntakePhone.Contains(searchDigits) || m.CanonicalIntakePhone.Contains(searchAlt1) || m.CanonicalIntakePhone.Contains(searchAlt2)))
                // A name or e-mail an agent typed in is searchable too (it is what the row will be shown under).
                || (m.CanonicalIntakePhone != null && dbContext.CustomerContactDetails.Any(c =>
                    c.PhoneKey == m.CanonicalIntakePhone && (c.Name.Contains(search) || (c.Email != null && c.Email.Contains(search)))))
                // The phone / project / unit an authorised user corrected are searchable too: they are what the row is shown with.
                || dbContext.CustomerProfileOverrides.Any(o => o.IdentityKind == m.Kind
                    && ((m.Kind == CrmKind && o.CrmBuyerCustomerId == m.CrmId)
                        || (m.Kind == ExternalKind && o.ExternalSource == m.Source && o.ExternalCustomerId == m.ExternalId)
                        || (m.Kind == PhoneKind && o.IdentityPhone == m.Phone))
                    && ((o.ProjectName != null && o.ProjectName.Contains(search))
                        || (o.UnitNumber != null && o.UnitNumber.Contains(search))
                        || (o.PhoneNumber != null && o.PhoneNumber.Contains(search))
                        || (searchIsPhoneShaped && o.PhoneNumber != null
                            && (o.PhoneNumber.Contains(searchDigits) || o.PhoneNumber.Contains(searchAlt1) || o.PhoneNumber.Contains(searchAlt2)))
                        // The profile details a person entered are searchable too: they are what the customer is shown with.
                        || (o.FullName != null && o.FullName.Contains(search))
                        || (o.Email != null && o.Email.Contains(search))
                        || (o.LandlineNumber != null && o.LandlineNumber.Contains(search))
                        || (o.AdditionalPhones != null && o.AdditionalPhones.Contains(search))
                        || (searchIsPhoneShaped && ((o.LandlineNumber != null && (o.LandlineNumber.Contains(searchAlt1) || o.LandlineNumber.Contains(searchAlt2)))
                            || (o.AdditionalPhones != null && (o.AdditionalPhones.Contains(searchAlt1) || o.AdditionalPhones.Contains(searchAlt2)))))
                        || (o.Nationality != null && o.Nationality.Contains(search))
                        || (o.Country != null && o.Country.Contains(search))
                        || (o.Emirate != null && o.Emirate.Contains(search))
                        || (o.Location != null && o.Location.Contains(search))))
                // Manually added properties are searchable by project and unit (EXISTS only: a TOP(1) here is the correlated shape that once
                // timed /Customers out, and CustomerDirectoryQueryShapeTests guards against it).
                || dbContext.CustomerPropertyAssociations.Any(a => (a.ProjectName.Contains(search) || a.UnitNumber.Contains(search))
                    && dbContext.CustomerProfileOverrides.Any(o => o.CustomerProfileId == a.CustomerProfileId && o.IdentityKind == m.Kind
                        && ((m.Kind == CrmKind && o.CrmBuyerCustomerId == m.CrmId)
                            || (m.Kind == ExternalKind && o.ExternalSource == m.Source && o.ExternalCustomerId == m.ExternalId)
                            || (m.Kind == PhoneKind && o.IdentityPhone == m.Phone))))
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
        var perTicketLocation = FilteredCandidates(query with { Location = null }, withLocation: true)
            .GroupBy(x => new { x.Kind, x.CrmId, x.Source, x.ExternalId, x.Phone })
            .Select(g => new { g.Key.Kind, g.Key.CrmId, g.Key.Source, g.Key.ExternalId, g.Key.Phone, Sum = g.Sum(x => x.LocationWeight), Qualifies = g.Max(x => x.Qualifies) });

        // A customer whose phone an authorised user corrected is placed by that number instead of the ones their tickets captured. The edited
        // number's place was worked out once, when it was saved (CustomerPhoneLocation), so this is a plain per-customer comparison after the
        // grouping, never the per-ticket location expression again.
        var edited = dbContext.CustomerProfileOverrides.AsNoTracking().Where(o => o.PhoneLocation != null);
        var perCustomer =
            from c in perTicketLocation
            join o in edited
                on new { c.Kind, c.CrmId, c.Source, c.ExternalId, c.Phone }
                equals new { Kind = o.IdentityKind, CrmId = o.CrmBuyerCustomerId, Source = o.ExternalSource, ExternalId = o.ExternalCustomerId, Phone = o.IdentityPhone }
                into overrides
            from o in overrides.DefaultIfEmpty()
            select new
            {
                Sum = o == null
                    ? c.Sum
                    : o.PhoneLocation == CustomerLocation.WithinUae ? WithinWeight : o.PhoneLocation == CustomerLocation.OutsideUae ? OutsideWeight : 0L,
                c.Qualifies,
            };
        if (query.TicketFilter is not null)
        {
            // Customers with at least one ticket matching the Dashboard's filters; their location still comes from ALL their visible tickets.
            perCustomer = perCustomer.Where(c => c.Qualifies == 1);
        }

        var counts = await perCustomer
            .GroupBy(c => 1)
            .Select(all => new
            {
                Within = all.Count(c => c.Sum % OutsideWeight > 0 && c.Sum / OutsideWeight == 0),
                Outside = all.Count(c => c.Sum / OutsideWeight > 0 && c.Sum % OutsideWeight == 0),
                Total = all.Count(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return counts is null
            ? new CustomerLocationCounts(0, 0, 0)
            : new CustomerLocationCounts(counts.Within, counts.Outside, counts.Total - counts.Within - counts.Outside);
    }

    public async Task<IReadOnlyList<CustomerLocationDiagnosis>> DiagnoseUnknownAsync(CustomerDirectoryQuery query, bool paged, CancellationToken cancellationToken = default)
    {
        // The Unknown customers exactly as ListAsync selects them (same candidates, same fold, same Unknown predicate), with the two extreme stored
        // phones folded in the same grouped pass. Unknown customers only — never every customer — and only identity + two strings per customer.
        var grouped = FilteredCandidates(query with { Location = CustomerLocation.Unknown }, withLocation: true)
            .GroupBy(x => new { x.Kind, x.CrmId, x.Source, x.ExternalId, x.Phone })
            .Select(g => new
            {
                g.Key.Kind,
                g.Key.CrmId,
                g.Key.Source,
                g.Key.ExternalId,
                g.Key.Phone,
                OpenTickets = g.Count(x =>
                    x.Ticket.TicketStatus == TicketStatus.Open
                    || x.Ticket.TicketStatus == TicketStatus.InProgress
                    || x.Ticket.TicketStatus == TicketStatus.PendingCustomer
                    || x.Ticket.TicketStatus == TicketStatus.PendingThirdParty),
                LastTicketId = g.Max(x => x.Ticket.TicketId),
                LastTicketCreatedAtUtc = g.Max(x => x.Ticket.CreatedAtUtc),
                LocationSum = g.Sum(x => x.LocationWeight),
                Qualifies = g.Max(x => x.Qualifies),
                MinPhone = g.Min(x => x.IntakePhone != null && x.IntakePhone != "" ? x.IntakePhone : x.InteractionPhone),
                MaxPhone = g.Max(x => x.IntakePhone != null && x.IntakePhone != "" ? x.IntakePhone : x.InteractionPhone),
            });

        if (query.TicketFilter is not null)
        {
            grouped = grouped.Where(g => g.Qualifies == 1);
        }

        // The same per-customer adjustment as ListAsync and CountByLocationAsync: a customer whose phone an authorised user corrected is placed by
        // that number, so they are not Unknown and are neither listed nor explained here - the breakdown then always adds up to the Unknown card.
        var editedLocations = dbContext.CustomerProfileOverrides.AsNoTracking().Where(o => o.PhoneLocation != null);
        grouped =
            from g in grouped
            join o in editedLocations
                on new { g.Kind, g.CrmId, g.Source, g.ExternalId, g.Phone }
                equals new { Kind = o.IdentityKind, CrmId = o.CrmBuyerCustomerId, Source = o.ExternalSource, ExternalId = o.ExternalCustomerId, Phone = o.IdentityPhone }
                into overrides
            from o in overrides.DefaultIfEmpty()
            select new
            {
                g.Kind, g.CrmId, g.Source, g.ExternalId, g.Phone, g.OpenTickets, g.LastTicketId, g.LastTicketCreatedAtUtc,
                LocationSum = o == null
                    ? g.LocationSum
                    : o.PhoneLocation == CustomerLocation.WithinUae ? WithinWeight : o.PhoneLocation == CustomerLocation.OutsideUae ? OutsideWeight : 0L,
                g.Qualifies, g.MinPhone, g.MaxPhone,
            };

        grouped = grouped.Where(g => (g.LocationSum % OutsideWeight > 0) == (g.LocationSum / OutsideWeight > 0));
        if (query.OpenOnly)
        {
            grouped = grouped.Where(g => g.OpenTickets > 0);
        }

        var ordered = grouped.OrderByDescending(g => g.LastTicketCreatedAtUtc).ThenByDescending(g => g.LastTicketId);
        var rows = await (paged
                ? ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
                : ordered.Take(ICustomerDirectoryRepository.MaxDiagnosed))
            .ToListAsync(cancellationToken);

        var result = new List<CustomerLocationDiagnosis>(rows.Count);
        foreach (var g in rows)
        {
            var key = ToIdentity(g.Kind, g.CrmId, g.Source, g.ExternalId, g.Phone).Key;
            var first = g.MaxPhone;
            var other = g.MinPhone != g.MaxPhone ? g.MinPhone : null;
            // Both a UAE and a foreign number on the customer's tickets → ambiguous; otherwise the (unplaced) number says why.
            var conflicting = g.LocationSum % OutsideWeight > 0 && g.LocationSum / OutsideWeight > 0;
            var reason = conflicting
                ? CustomerPhoneLocationReason.ConflictingCountries
                : CustomerPhoneLocation.Diagnose(first).Reason;
            result.Add(new CustomerLocationDiagnosis(key, first, other, reason));
        }

        return result;
    }

    public async Task<CustomerDirectoryPage> ListAsync(CustomerDirectoryQuery query, CancellationToken cancellationToken = default)
    {
        var candidates = FilteredCandidates(query, withLocation: query.Location is not null);

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
                // Folded in the same pass: the location weight (only when a location was asked for) and the Dashboard's ticket-filter flag.
                LocationSum = g.Sum(x => x.LocationWeight),
                Qualifies = g.Max(x => x.Qualifies),
            });

        if (query.TicketFilter is not null)
        {
            grouped = grouped.Where(g => g.Qualifies == 1);
        }

        if (query.Location is { } wanted)
        {
            // Same per-customer adjustment as CountByLocationAsync, so the list a Dashboard card opens is the customers it counted.
            var editedLocations = dbContext.CustomerProfileOverrides.AsNoTracking().Where(o => o.PhoneLocation != null);
            grouped =
                from g in grouped
                join o in editedLocations
                    on new { g.Kind, g.CrmId, g.Source, g.ExternalId, g.Phone }
                    equals new { Kind = o.IdentityKind, CrmId = o.CrmBuyerCustomerId, Source = o.ExternalSource, ExternalId = o.ExternalCustomerId, Phone = o.IdentityPhone }
                    into overrides
                from o in overrides.DefaultIfEmpty()
                select new
                {
                    g.Kind, g.CrmId, g.Source, g.ExternalId, g.Phone, g.TotalTickets, g.OpenTickets, g.LastTicketId, g.LastNamedTicketId,
                    g.LastTicketCreatedAtUtc, g.LastInteractionAtUtc,
                    LocationSum = o == null
                        ? g.LocationSum
                        : o.PhoneLocation == CustomerLocation.WithinUae ? WithinWeight : o.PhoneLocation == CustomerLocation.OutsideUae ? OutsideWeight : 0L,
                    g.Qualifies,
                };

            grouped = wanted switch
            {
                CustomerLocation.WithinUae => grouped.Where(g => g.LocationSum % OutsideWeight > 0 && g.LocationSum / OutsideWeight == 0),
                CustomerLocation.OutsideUae => grouped.Where(g => g.LocationSum / OutsideWeight > 0 && g.LocationSum % OutsideWeight == 0),
                _ => grouped.Where(g => (g.LocationSum % OutsideWeight > 0) == (g.LocationSum / OutsideWeight > 0)),
            };
        }

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

        // The phone / project / unit / name an authorised user corrected, one batched read for the page, matched on the directory key.
        var pageKeys = page.Select(g => ToIdentity(g.Kind, g.CrmId, g.Source, g.ExternalId, g.Phone).Key).ToList();
        var edits = await dbContext.CustomerProfileOverrides.AsNoTracking()
            .Where(o => pageKeys.Contains(o.CustomerKey))
            .ToDictionaryAsync(o => o.CustomerKey, cancellationToken);

        // Names/e-mails an agent entered by hand, one batched read for the whole page, matched on every spelling of every number the row is
        // known by (its latest ticket's, the identity's and an edited one) - the same keys the profile and Ticket Details use.
        string?[] RowPhones(string? groupPhone, TicketSnapshot snap, CustomerProfileOverride? e) =>
            [snap.IntakePhone, snap.InteractionPhone, groupPhone, e?.PhoneNumber, e?.LandlineNumber, .. e?.AdditionalPhoneList ?? []];
        var rowKeys = page
            .Select(g => RowPhones(g.Phone, snapshots.GetValueOrDefault(g.LastTicketId), edits.GetValueOrDefault(ToIdentity(g.Kind, g.CrmId, g.Source, g.ExternalId, g.Phone).Key)))
            .SelectMany(CustomerContactResolution.ContactKeys)
            .Distinct()
            .ToList();
        var manualContacts = rowKeys.Count == 0
            ? new Dictionary<string, CustomerContactDetail>()
            : await dbContext.CustomerContactDetails.AsNoTracking()
                .Where(c => rowKeys.Contains(c.PhoneKey))
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
            var derived = DerivedFacts(last, snapshot, identity.PhoneNumber);
            var edit = edits.GetValueOrDefault(identity.Key);
            var manual = CustomerContactResolution.ContactKeys(RowPhones(g.Phone, snapshot, edit))
                .Select(manualContacts.GetValueOrDefault)
                .OfType<CustomerContactDetail>()
                .OrderByDescending(c => c.UpdatedAtUtc)
                .FirstOrDefault();
            var displayName = CustomerContactResolution.Resolve(new CustomerContactInputs(
                ProfileName: edit?.FullName,
                ProfileUpdatedAtUtc: edit?.UpdatedAtUtc,
                ManualContactName: manual?.Name,
                ManualContactUpdatedAtUtc: manual?.UpdatedAtUtc,
                CrmName: CustomerNameResolver.FirstValid(last.CrmBuyerCustomerName, namedTicket?.CrmBuyerCustomerName),
                ExternalName: CustomerNameResolver.FirstValid(last.ExternalCustomerName, namedTicket?.ExternalCustomerName),
                SubmittedNames: [snapshot.InteractionName, snapshot.SnapshotName])).Name;

            rows.Add(new CustomerDirectoryRowDto(
                identity.Key,
                identity.Kind.ToString(),
                displayName,
                // The number as the latest ticket captured it — the canonical
                // form identifies the customer, it is not how they are shown —
                // unless an authorised user corrected it.
                edit?.PhoneNumber ?? derived.Phone,
                edit?.ProjectName ?? derived.Project,
                edit?.UnitNumber ?? derived.Unit,
                VerificationSourceFor(identity),
                g.OpenTickets,
                g.TotalTickets,
                g.LastTicketId,
                last.TicketNumber,
                last.TicketStatus.ToString(),
                g.LastTicketCreatedAtUtc,
                g.LastInteractionAtUtc,
                EditedFields: EditedFieldsOf(edit)));
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
                FirstClean(t.CrmBuyerProjectName, t.ManualProjectName, snapshot.SnapshotProperty),
                FirstClean(t.CrmBuyerUnitNumber, t.ManualUnitNumber, snapshot.SnapshotUnit),
                t.RequestSummary,
                lastActivity,
                resolvedAt.TryGetValue(t.TicketId, out var r) ? r : null,
                t.VerificationStatus.ToString());
        }).ToList();

        var units = tickets
            .Select(x => new
            {
                Project = FirstClean(x.Ticket.CrmBuyerProjectName, x.Ticket.ManualProjectName, snapshots.GetValueOrDefault(x.Ticket.TicketId).SnapshotProperty),
                Unit = FirstClean(x.Ticket.CrmBuyerUnitNumber, x.Ticket.ManualUnitNumber, snapshots.GetValueOrDefault(x.Ticket.TicketId).SnapshotUnit),
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
        // What an authorised user entered over the tickets' own facts is loaded first: its numbers are part of the lookup below, so a name
        // saved under ANY number the customer is known by is found - the same set every surface uses (CustomerContactResolution).
        var edit = await dbContext.CustomerProfileOverrides.AsNoTracking()
            .FirstOrDefaultAsync(o => o.CustomerKey == identity.Key, cancellationToken);
        var phoneKeys = CustomerContactResolution.ContactKeys(
            phones.Concat([edit?.PhoneNumber, edit?.LandlineNumber]).Concat(edit?.AdditionalPhoneList ?? []));
        var manualContact = phoneKeys.Count == 0
            ? null
            : (await dbContext.CustomerContactDetails.AsNoTracking()
                .Where(c => phoneKeys.Contains(c.PhoneKey))
                .ToListAsync(cancellationToken))
                .OrderByDescending(c => c.UpdatedAtUtc)
                .FirstOrDefault();

        // The e-mails the sources hold (an agent's contact record, then the tickets and conversations), WITHOUT the profile's own edit:
        // what would be shown if that edit were cleared. The shown list leads with the edit.
        var derivedEmails = new string?[] { manualContact?.Email }
            .Concat(tickets.Select(x => x.Ticket.ExternalCustomerEmail))
            .Concat(interactions.Select(i => i.CustomerEmail))
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var emails = new string?[] { edit?.Email }
            .Concat(derivedEmails)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var last = tickets[0].Ticket;

        // What an authorised user entered over the phone / project / unit the tickets say. The numbers lead with the edited one (one entry
        // per real number), the units lead with an "Edited" entry (the tickets' own units stay below it, as history), and the derived values
        // are returned next to the edit so the Edit form and the audit trail can say what was shown before.
        var derived = DerivedFacts(last, snapshots.GetValueOrDefault(last.TicketId), identity.PhoneNumber);
        var shownPhones = edit?.PhoneNumber is { } editedPhone
            ? new[] { editedPhone }.Concat(phones.Where(p => CustomerPhoneNumber.Normalize(p) != CustomerPhoneNumber.Normalize(editedPhone))).ToList()
            : phones;
        var shownUnits = edit is { ProjectName: not null } or { UnitNumber: not null }
            ? new[] { new CustomerDirectoryUnitDto(edit.ProjectName, edit.UnitNumber, "Edited", 0, edit.UpdatedAtUtc, null, null) }.Concat(units).ToList()
            : units;
        var editState = new CustomerProfileEditStateDto(
            derived.Phone, derived.Project, derived.Unit, edit?.PhoneNumber, edit?.ProjectName, edit?.UnitNumber, edit?.UpdatedAtUtc);

        // The one rule for the name / e-mail every surface shows (CustomerContactResolution): an entered value first, then the evidence
        // ranking across the latest ticket and, so a name captured once is not lost, every ticket and conversation this customer has.
        var lastSnapshot = snapshots.GetValueOrDefault(last.TicketId);
        var resolved = CustomerContactResolution.Resolve(new CustomerContactInputs(
            ProfileName: edit?.FullName,
            ProfileEmail: edit?.Email,
            ProfileUpdatedAtUtc: edit?.UpdatedAtUtc,
            ManualContactName: manualContact?.Name,
            ManualContactEmail: manualContact?.Email,
            ManualContactUpdatedAtUtc: manualContact?.UpdatedAtUtc,
            CrmName: CustomerNameResolver.FirstValid(tickets.Select(x => x.Ticket.CrmBuyerCustomerName).ToArray()),
            ExternalName: CustomerNameResolver.FirstValid(tickets.Select(x => x.Ticket.ExternalCustomerName).ToArray()),
            SubmittedNames: new[] { lastSnapshot.InteractionName, lastSnapshot.SnapshotName }.Concat(interactions.Select(i => i.CustomerName)).ToList(),
            DerivedEmails: emails));
        var displayName = resolved.Name;

        // What would be shown if the profile's own name / e-mail were cleared: the audit needs it to say what a clear changes the value to.
        var withoutProfile = CustomerContactResolution.Resolve(new CustomerContactInputs(
            ManualContactName: manualContact?.Name,
            ManualContactEmail: manualContact?.Email,
            ManualContactUpdatedAtUtc: manualContact?.UpdatedAtUtc,
            CrmName: CustomerNameResolver.FirstValid(tickets.Select(x => x.Ticket.CrmBuyerCustomerName).ToArray()),
            ExternalName: CustomerNameResolver.FirstValid(tickets.Select(x => x.Ticket.ExternalCustomerName).ToArray()),
            SubmittedNames: new[] { lastSnapshot.InteractionName, lastSnapshot.SnapshotName }.Concat(interactions.Select(i => i.CustomerName)).ToList(),
            DerivedEmails: derivedEmails));

        var details = new CustomerProfileDetailsDto(
            resolved.Name, resolved.Email, edit?.LandlineNumber, edit?.AdditionalPhoneList ?? [], edit?.Nationality, edit?.Country,
            edit?.Emirate, edit?.Location, edit?.PreferredLanguage, DetailFieldsEditedIn(edit), edit?.UpdatedAtUtc,
            withoutProfile.Name, withoutProfile.Email);
        var manualProperties = edit is null
            ? []
            : (await dbContext.CustomerPropertyAssociations.AsNoTracking()
                .Where(a => a.CustomerProfileId == edit.CustomerProfileId)
                .OrderBy(a => a.ProjectName).ThenBy(a => a.UnitNumber)
                .ToListAsync(cancellationToken))
                .Select(a => new CustomerPropertyDto(a.CustomerPropertyAssociationId, a.ProjectName, a.UnitNumber, a.Relationship.ToString(), false, a.UpdatedAtUtc))
                .ToList();

        return new CustomerDirectoryProfileDto(
            identity.Key,
            identity.Kind.ToString(),
            displayName,
            shownPhones,
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
            shownUnits,
            ticketDtos,
            interactions.Select(i => new CustomerDirectoryInteractionDto(
                i.TicketInteractionId, i.TicketId, ticketNumbers.GetValueOrDefault(i.TicketId, string.Empty), i.ChannelId,
                channelNames.GetValueOrDefault(i.ChannelId), i.Direction, i.InteractionStartedAtUtc ?? i.CreatedAtUtc, i.EndedAtUtc,
                i.EndedAtUtc is null ? "Active" : "Ended", i.GenesysAgentName, i.IsOriginatingInteraction)).ToList(),
            editState,
            edit?.CustomerProfileId,
            details,
            manualProperties);
    }

    // ---- helpers ----

    /// <summary>
    /// The phone, project and unit a customer's latest ticket says, each with a source's placeholder ("NOT_REGISTERED", "UNKNOWN", "N/A")
    /// treated as missing - cleaned candidate by candidate, so a placeholder from one source never hides a real value from the next.
    /// </summary>
    private static (string? Phone, string? Project, string? Unit) DerivedFacts(Ticket last, TicketSnapshot snapshot, string? identityPhone) =>
        (FirstClean(snapshot.IntakePhone, snapshot.InteractionPhone, identityPhone),
         FirstClean(last.CrmBuyerProjectName, last.ManualProjectName, snapshot.SnapshotProperty),
         FirstClean(last.CrmBuyerUnitNumber, last.ManualUnitNumber, snapshot.SnapshotUnit));

    private static string? FirstClean(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (CustomerFieldText.Clean(candidate) is { } clean)
            {
                return clean;
            }
        }

        return null;
    }

    /// <summary>The profile details (beyond phone / project / unit) a person entered, by field name.</summary>
    private static IReadOnlyList<string> DetailFieldsEditedIn(CustomerProfileOverride? edit)
    {
        var fields = new List<string>();
        if (edit is null) return fields;
        if (edit.FullName is not null) fields.Add(CustomerProfileFields.FullName);
        if (edit.Email is not null) fields.Add(CustomerProfileFields.Email);
        if (edit.LandlineNumber is not null) fields.Add(CustomerProfileFields.LandlineNumber);
        if (edit.AdditionalPhones is not null) fields.Add(CustomerProfileFields.AdditionalPhone);
        if (edit.Nationality is not null) fields.Add(CustomerProfileFields.Nationality);
        if (edit.Country is not null) fields.Add(CustomerProfileFields.Country);
        if (edit.Emirate is not null) fields.Add(CustomerProfileFields.Emirate);
        if (edit.Location is not null) fields.Add(CustomerProfileFields.Location);
        if (edit.PreferredLanguage is not null) fields.Add(CustomerProfileFields.PreferredLanguage);
        return fields;
    }

    private static IReadOnlyList<string>? EditedFieldsOf(CustomerProfileOverride? edit)
    {
        if (edit is null)
        {
            return null;
        }

        var edited = new List<string>(3);
        if (edit.PhoneNumber is not null) edited.Add("PhoneNumber");
        if (edit.ProjectName is not null) edited.Add("ProjectName");
        if (edit.UnitNumber is not null) edited.Add("UnitNumber");
        return edited.Count == 0 ? null : edited;
    }

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
