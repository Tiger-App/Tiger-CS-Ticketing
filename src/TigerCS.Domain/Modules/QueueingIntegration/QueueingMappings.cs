namespace TigerCS.Domain.Modules.QueueingIntegration;

/// <summary>Queueing System company id -> TigerCS department. One company routes to exactly one department; a re-point is an edit.</summary>
public class QueueingCompanyMapping
{
    public const int ExternalIdMaxLength = 100;
    public int QueueingCompanyMappingId { get; private set; }
    public string CompanyId { get; private set; } = string.Empty;
    public int DepartmentId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private QueueingCompanyMapping() { }

    public QueueingCompanyMapping(string companyId, int departmentId, DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companyId);
        CompanyId = companyId.Trim();
        DepartmentId = departmentId;
        IsActive = true;
        CreatedAtUtc = createdAtUtc;
    }

    public void Repoint(int departmentId, bool isActive)
    {
        DepartmentId = departmentId;
        IsActive = isActive;
    }
}

/// <summary>(company, queue ticket type) -> TigerCS request type. The request type decides category, workflow and SLA through the existing request-type routing.</summary>
public class QueueingTicketTypeMapping
{
    public int QueueingTicketTypeMappingId { get; private set; }
    public string CompanyId { get; private set; } = string.Empty;
    public string TicketTypeId { get; private set; } = string.Empty;
    public int RequestTypeId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private QueueingTicketTypeMapping() { }

    public QueueingTicketTypeMapping(string companyId, string ticketTypeId, int requestTypeId, DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketTypeId);
        CompanyId = companyId.Trim();
        TicketTypeId = ticketTypeId.Trim();
        RequestTypeId = requestTypeId;
        IsActive = true;
        CreatedAtUtc = createdAtUtc;
    }

    public void Repoint(int requestTypeId, bool isActive)
    {
        RequestTypeId = requestTypeId;
        IsActive = isActive;
    }
}

/// <summary>Queueing System employee id -> TigerCS employee. The queue's own id is the key; the TigerCS employee id is what an assignment is written against.</summary>
public class QueueingUserMapping
{
    public int QueueingUserMappingId { get; private set; }
    public string ExternalUserId { get; private set; } = string.Empty;
    public Guid EmployeeId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private QueueingUserMapping() { }

    public QueueingUserMapping(string externalUserId, Guid employeeId, DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("A mapping must name a real employee.", nameof(employeeId));
        }

        ExternalUserId = externalUserId.Trim();
        EmployeeId = employeeId;
        IsActive = true;
        CreatedAtUtc = createdAtUtc;
    }

    public void Repoint(Guid employeeId, bool isActive)
    {
        EmployeeId = employeeId;
        IsActive = isActive;
    }
}
