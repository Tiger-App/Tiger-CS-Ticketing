using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Api.Filters;

/// <summary>
/// Refuses a request that addresses a ticket (<c>{ticketId}</c> in the route) the caller may not see — the server-side half of CS Agent
/// ticket visibility for direct URLs and API calls. A CS Agent sees only tickets assigned to them or on which they handled a Genesys
/// interaction (<see cref="TicketVisibilityRule"/>); without this, every endpoint under <c>api/tickets/{ticketId}</c> would have to
/// remember that, and the CS Agent's cross-department action rights (close, classify, reopen, escalate, …) would reach tickets they cannot
/// even open.
///
/// <para>
/// It is a <i>resource</i> filter, so it runs before model binding and validation: a restricted agent is refused (403) before the request
/// body is even read, and a malformed body cannot turn the answer into a 400 that reveals the ticket exists. It only ever acts for a
/// restricted CS Agent: every other caller passes straight through without a database
/// read, so Admin/Supervisor/Manager/department behaviour is exactly what each endpoint's own checks already make it. A ticket that does
/// not exist is left to the action (404, as before); one that exists but is not visible is <b>403</b> — the same answer the ticket detail
/// has always given for an out-of-scope ticket. The services keep their own checks; this is the net underneath them.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireTicketAccessAttribute() : TypeFilterAttribute(typeof(RequireTicketAccessFilter))
{
}

/// <summary>
/// Marks an action that a caller with read-only access to the ticket may still POST to, because the action does its own, stricter ticket
/// check. Today only Take Ownership: it is refused unless <c>TicketClaimPolicy</c> says the caller may take THIS ticket.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PermitReadOnlyTicketAccessAttribute : Attribute
{
}

internal sealed class RequireTicketAccessFilter(TicketQueryAppService ticketQueryAppService) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!TryGetTicketId(context, out var ticketId))
        {
            await next();
            return;
        }

        var roles = context.HttpContext.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        var idValue = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            if (!TicketVisibilityRule.IsRestrictedCsAgent(roles))
            {
                await next();
                return;
            }

            context.Result = new UnauthorizedResult();
            return;
        }

        // Only a human CS Agent (per-ticket rule) and the Genesys integration account (Genesys-created tickets only) are scoped here;
        // everyone else passes straight through without a database read.
        if (!ticketQueryAppService.IsRestrictedAgent(employeeId, roles)
            && !ticketQueryAppService.IsGenesysIntegration(employeeId, roles))
        {
            await next();
            return;
        }

        var access = await ticketQueryAppService.CheckAccessAsync(employeeId, roles, ticketId, context.HttpContext.RequestAborted);
        if (access == TicketAccess.Denied)
        {
            context.Result = Forbidden(
                "Ticket not accessible.",
                "You can only access tickets assigned to you, tickets on which you handled a Genesys interaction, or tickets you created (the Genesys integration account: Genesys-created tickets).");
            return;
        }

        // The creator of a ticket in another department may read it and nothing else: every request that is not a safe read is refused.
        // The one exception is an action that is its own gate and explicitly says so (Take Ownership): an unassigned ticket is opened
        // read-only precisely so the agent can decide to take it, and that action then applies the full TicketClaimPolicy.
        if (access == TicketAccess.ReadOnly
            && !IsSafeRead(context.HttpContext.Request.Method)
            && !context.ActionDescriptor.EndpointMetadata.OfType<PermitReadOnlyTicketAccessAttribute>().Any())
        {
            context.Result = Forbidden(
                "Read-only access.",
                "You created this ticket, so you can follow it, but it belongs to another department: you cannot change it.");
            return;
        }

        await next();
    }

    private static bool IsSafeRead(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static ObjectResult Forbidden(string title, string detail) =>
        new(new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = title, Detail = detail })
        { StatusCode = StatusCodes.Status403Forbidden };

    private static bool TryGetTicketId(ResourceExecutingContext context, out long ticketId)
    {
        ticketId = 0;
        return context.RouteData.Values.TryGetValue("ticketId", out var raw)
            && long.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), out ticketId);
    }
}
