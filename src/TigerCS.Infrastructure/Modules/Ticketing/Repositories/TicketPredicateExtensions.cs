using System.Linq.Expressions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>
/// Composes ticket predicates into ONE expression tree. EF Core can translate a single lambda but not a call to another lambda, so the
/// shared definitions in <see cref="TicketQueryFilters"/> (e.g. <see cref="TicketQueryFilters.AvailableToTake"/>) are combined here by
/// rebinding their parameter to a common one - the whole predicate still becomes a single SQL WHERE clause.
/// </summary>
internal static class TicketPredicateExtensions
{
    public static Expression<Func<Ticket, bool>> OrElse(this Expression<Func<Ticket, bool>> left, Expression<Func<Ticket, bool>> right) =>
        Combine(left, right, Expression.OrElse);

    public static Expression<Func<Ticket, bool>> AndAlso(this Expression<Func<Ticket, bool>> left, Expression<Func<Ticket, bool>> right) =>
        Combine(left, right, Expression.AndAlso);

    public static Expression<Func<Ticket, bool>> Not(this Expression<Func<Ticket, bool>> predicate) =>
        Expression.Lambda<Func<Ticket, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private static Expression<Func<Ticket, bool>> Combine(
        Expression<Func<Ticket, bool>> left, Expression<Func<Ticket, bool>> right,
        Func<Expression, Expression, BinaryExpression> operation)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ParameterRebinder(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<Ticket, bool>>(operation(left.Body, rightBody), parameter);
    }

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
