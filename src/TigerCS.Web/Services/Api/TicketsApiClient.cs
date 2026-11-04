using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/tickets</c> endpoints.</summary>
public sealed class TicketsApiClient(HttpClient httpClient, ILogger<TicketsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<TicketListResultDto>> GetQueueAsync(TicketListRequestDto request, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (request.DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString();
        if (request.CategoryId is int categoryId) query["categoryId"] = categoryId.ToString();
        if (request.PriorityId is byte priorityId) query["priorityId"] = priorityId.ToString();
        if (!string.IsNullOrWhiteSpace(request.TicketStatus)) query["ticketStatus"] = request.TicketStatus;
        if (!string.IsNullOrWhiteSpace(request.VerificationStatus)) query["verificationStatus"] = request.VerificationStatus;
        if (request.OwnerEmployeeId is Guid ownerId) query["ownerEmployeeId"] = ownerId.ToString();
        if (!string.IsNullOrWhiteSpace(request.Search)) query["search"] = request.Search;
        if (!string.IsNullOrWhiteSpace(request.SortBy)) query["sortBy"] = request.SortBy;
        if (!string.IsNullOrWhiteSpace(request.SortDir)) query["sortDir"] = request.SortDir;
        // Dashboard drill-down filters (Dashboard Phase 1).
        if (request.ChannelId is byte channelId) query["channelId"] = channelId.ToString();
        if (request.RequestTypeId is int requestTypeId) query["requestTypeId"] = requestTypeId.ToString();
        if (request.ActiveOnly == true) query["activeOnly"] = "true";
        if (request.InDepartmentQueue == true) query["inDepartmentQueue"] = "true";
        if (request.SlaBreached == true) query["slaBreached"] = "true";
        if (request.DueToday == true) query["dueToday"] = "true";
        if (!string.IsNullOrWhiteSpace(request.BacklogAge)) query["backlogAge"] = request.BacklogAge;
        if (request.PendingApproval == true) query["pendingApproval"] = "true";
        if (request.Unclassified == true) query["unclassified"] = "true";
        if (request.CreatedByMe == true) query["createdByMe"] = "true";
        if (request.Escalated == true) query["escalated"] = "true";
        if (request.AvailableToTake == true) query["availableToTake"] = "true";
        if (!string.IsNullOrWhiteSpace(request.ClosureCategory)) query["closureCategory"] = request.ClosureCategory;
        if (request.ClosedFrom is DateOnly closedFrom) query["closedFrom"] = closedFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (request.ClosedTo is DateOnly closedTo) query["closedTo"] = closedTo.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (request.CreatedFrom is DateOnly createdFrom) query["createdFrom"] = createdFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (request.CreatedTo is DateOnly createdTo) query["createdTo"] = createdTo.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        query["page"] = request.Page.ToString();
        query["pageSize"] = request.PageSize.ToString();

        return GetAsync<TicketListResultDto>($"api/tickets?{query}", cancellationToken);
    }

    public Task<ApiResult<TicketDetailDto>> GetByIdAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TicketDetailDto>($"api/tickets/{ticketId}", cancellationToken);

    public Task<ApiResult<TicketDetailDto>> AssignAsync(long ticketId, AssignTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<AssignTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/assignment", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> TakeOwnershipAsync(long ticketId, TakeOwnershipRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<TakeOwnershipRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/ownership", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> ClassifyAsync(long ticketId, ClassifyTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<ClassifyTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/classification", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> TransferAsync(long ticketId, TransferTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<TransferTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/transfer", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> ChangeStatusAsync(long ticketId, ChangeStatusRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<ChangeStatusRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/status", request, cancellationToken);

    public Task<ApiResult<TicketApprovalsViewDto>> GetApprovalsAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TicketApprovalsViewDto>($"api/tickets/{ticketId}/approvals", cancellationToken);

    /// <summary>The ticket's lifecycle history (ADR-0018) — what the Activity feed renders status changes and their recorded reasons from.</summary>
    public Task<ApiResult<TicketLifecycleHistoryDto>> GetLifecycleHistoryAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TicketLifecycleHistoryDto>($"api/tickets/{ticketId}/history", cancellationToken);

    public Task<ApiResult<TicketApprovalDto>> RequestApprovalAsync(
        long ticketId, RequestApprovalRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<RequestApprovalRequestDto, TicketApprovalDto>($"api/tickets/{ticketId}/approvals", request, cancellationToken);

    public Task<ApiResult<TicketApprovalDto>> DecideApprovalAsync(
        long ticketId, long approvalId, DecideApprovalRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<DecideApprovalRequestDto, TicketApprovalDto>(
            $"api/tickets/{ticketId}/approvals/{approvalId}/decision", request, cancellationToken);

    public Task<ApiResult> RecordWorkflowEventAsync(
        long ticketId, RecordWorkflowEventRequestDto request, CancellationToken cancellationToken) =>
        PostAsync($"api/tickets/{ticketId}/workflow-events", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> ResolveAsync(long ticketId, ResolveTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<ResolveTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/resolution", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> CloseAsync(long ticketId, CloseTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CloseTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/close", request, cancellationToken);

    public Task<ApiResult<TicketDetailDto>> ReopenAsync(long ticketId, ReopenTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<ReopenTicketRequestDto, TicketDetailDto>($"api/tickets/{ticketId}/reopen", request, cancellationToken);

    public Task<ApiResult<TicketNoteResponseDto>> AddNoteAsync(long ticketId, CreateNoteRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CreateNoteRequestDto, TicketNoteResponseDto>($"api/tickets/{ticketId}/notes", request, cancellationToken);

    public Task<ApiResult<TicketNoteListResultDto>> GetNotesAsync(
        long ticketId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["page"] = page.ToString();
        query["pageSize"] = pageSize.ToString();

        return GetAsync<TicketNoteListResultDto>($"api/tickets/{ticketId}/notes?{query}", cancellationToken);
    }

    public Task<ApiResult<TicketResponseDto>> CreateAsync(
        CreateTicketRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CreateTicketRequestDto, TicketResponseDto>("api/tickets", request, cancellationToken);

    /// <summary>Customer History for this ticket's customer — verified (CrmBuyerCustomerId) when the ticket has one, otherwise the phone-snapshot fallback. Never a live CRM call.</summary>
    public Task<ApiResult<CustomerHistoryDto>> GetCustomerHistoryAsync(long ticketId, int? limit, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (limit is int l) query["limit"] = l.ToString();

        return GetAsync<CustomerHistoryDto>($"api/tickets/{ticketId}/customer-history?{query}", cancellationToken);
    }

    /// <summary>The ticket's conversation history — every Genesys call/chat recorded against it, with transcripts. Read-only; never a live Genesys call.</summary>
    public Task<ApiResult<TicketInteractionHistoryDto>> GetInteractionsAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TicketInteractionHistoryDto>($"api/tickets/{ticketId}/interactions", cancellationToken);

    /// <summary>The ticket's full Activity timeline (creation, AI/Genesys/human milestones, handoffs, ownership, lifecycle, closures) — read-only, built by the Api from stored evidence.</summary>
    public Task<ApiResult<TigerCS.Application.Modules.Ticketing.ActivityTimeline.TicketActivityTimelineDto>> GetActivityTimelineAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TigerCS.Application.Modules.Ticketing.ActivityTimeline.TicketActivityTimelineDto>($"api/tickets/{ticketId}/activity-timeline", cancellationToken);

    /// <summary>The Tower and Unit choices for moving this ticket into Collections (the customer's verified towers and their units).</summary>
    public Task<ApiResult<TigerCS.Application.Modules.Collections.TowerAssignments.CollectionsTransferOptionsDto>> GetCollectionsTransferOptionsAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TigerCS.Application.Modules.Collections.TowerAssignments.CollectionsTransferOptionsDto>($"api/tickets/{ticketId}/collections-transfer-options", cancellationToken);

    /// <summary>The ticket's Collections assignment history (automatic and manual) - append-only, read-only; empty for a ticket that never had one.</summary>
    public Task<ApiResult<IReadOnlyList<TicketAssignmentHistoryItemDto>>> GetAssignmentHistoryAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<TicketAssignmentHistoryItemDto>>($"api/tickets/{ticketId}/assignment-history", cancellationToken);

    /// <summary>The ticket's customer satisfaction review cycles — read from the persisted review store; never contains a review token.</summary>
    public Task<ApiResult<TigerCS.Application.Modules.CustomerReviews.Dto.TicketReviewsDto>> GetReviewsAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<TigerCS.Application.Modules.CustomerReviews.Dto.TicketReviewsDto>($"api/tickets/{ticketId}/reviews", cancellationToken);

    /// <summary>Customer Details/Profile (Overview/Contact Info/Units) for this ticket's customer — live CRM data via the same CRM Buyer Lookup the New Ticket wizard uses.</summary>
    public Task<ApiResult<CustomerProfileDto>> GetCustomerProfileAsync(long ticketId, CancellationToken cancellationToken) =>
        GetAsync<CustomerProfileDto>($"api/tickets/{ticketId}/customer-profile", cancellationToken);
}
