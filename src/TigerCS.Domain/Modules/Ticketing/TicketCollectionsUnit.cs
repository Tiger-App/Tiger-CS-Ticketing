namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The tower and unit a person selected - and the server verified against the customer's trusted CRM or PACT record - when a ticket was moved into Collections. One row per ticket (the
/// latest selection). It is what automatic tower routing reads, so a customer with several units is routed by the unit this request is about. Never written from typed text.
/// <see cref="UnitRef"/> says where the unit was verified: <c>crm:{unitId}</c> or <c>pact:{unitId}</c>.
/// </summary>
public class TicketCollectionsUnit
{
    public long TicketId { get; private set; }
    public int TowerId { get; private set; }
    public string TowerCode { get; private set; } = string.Empty;
    public string TowerName { get; private set; } = string.Empty;
    public int CompanyId { get; private set; }
    public string UnitRef { get; private set; } = string.Empty;
    public string UnitNumber { get; private set; } = string.Empty;
    public Guid SelectedByEmployeeId { get; private set; }
    public DateTime SelectedAtUtc { get; private set; }

    private TicketCollectionsUnit() { }

    public TicketCollectionsUnit(
        long ticketId, int towerId, string towerCode, string towerName, int companyId, string unitRef, string unitNumber, Guid selectedBy, DateTime selectedAtUtc)
    {
        TicketId = ticketId;
        Apply(towerId, towerCode, towerName, companyId, unitRef, unitNumber, selectedBy, selectedAtUtc);
    }

    public void Apply(int towerId, string towerCode, string towerName, int companyId, string unitRef, string unitNumber, Guid selectedBy, DateTime selectedAtUtc)
    {
        if (towerId <= 0) throw new ArgumentOutOfRangeException(nameof(towerId));
        if (string.IsNullOrWhiteSpace(unitRef)) throw new ArgumentException("UnitRef is required.", nameof(unitRef));
        TowerId = towerId;
        TowerCode = towerCode.Trim();
        TowerName = (towerName ?? string.Empty).Trim();
        CompanyId = companyId;
        UnitRef = unitRef.Trim();
        UnitNumber = (unitNumber ?? string.Empty).Trim();
        SelectedByEmployeeId = selectedBy;
        SelectedAtUtc = selectedAtUtc;
    }
}
