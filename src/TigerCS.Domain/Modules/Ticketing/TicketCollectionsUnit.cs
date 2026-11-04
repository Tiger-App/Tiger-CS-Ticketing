namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The tower and unit a person selected - and the server verified against CRM and the CRM project mapping - when a ticket was moved into Collections. One row per ticket (the latest
/// selection). It is what automatic tower routing reads, so a customer with several units is routed by the unit this request is about. Never written from typed text: only from a
/// verified unit of the ticket's own customer.
/// </summary>
public class TicketCollectionsUnit
{
    public long TicketId { get; private set; }
    public int TowerId { get; private set; }
    public string TowerCode { get; private set; } = string.Empty;
    public string TowerName { get; private set; } = string.Empty;
    public int CompanyId { get; private set; }
    public int CrmUnitId { get; private set; }
    public int CrmProjectId { get; private set; }
    public string UnitNumber { get; private set; } = string.Empty;
    public Guid SelectedByEmployeeId { get; private set; }
    public DateTime SelectedAtUtc { get; private set; }

    private TicketCollectionsUnit() { }

    public TicketCollectionsUnit(
        long ticketId, int towerId, string towerCode, string towerName, int companyId, int crmUnitId, int crmProjectId, string unitNumber, Guid selectedBy, DateTime selectedAtUtc)
    {
        TicketId = ticketId;
        Apply(towerId, towerCode, towerName, companyId, crmUnitId, crmProjectId, unitNumber, selectedBy, selectedAtUtc);
    }

    public void Apply(
        int towerId, string towerCode, string towerName, int companyId, int crmUnitId, int crmProjectId, string unitNumber, Guid selectedBy, DateTime selectedAtUtc)
    {
        if (towerId <= 0) throw new ArgumentOutOfRangeException(nameof(towerId));
        if (crmUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(crmUnitId));
        TowerId = towerId;
        TowerCode = towerCode.Trim();
        TowerName = (towerName ?? string.Empty).Trim();
        CompanyId = companyId;
        CrmUnitId = crmUnitId;
        CrmProjectId = crmProjectId;
        UnitNumber = (unitNumber ?? string.Empty).Trim();
        SelectedByEmployeeId = selectedBy;
        SelectedAtUtc = selectedAtUtc;
    }
}
