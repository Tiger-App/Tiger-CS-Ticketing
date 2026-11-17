using TigerCS.Application.Modules.Reporting.Abstractions;
using TigerCS.Application.Modules.Reporting.Dto;

namespace TigerCS.Application.Modules.Reporting.Services;

/// <summary>
/// Decides what ONE call offer means for the employee who received it. Pure: it reads only the offer's own facts and the roster verdict at
/// the offer moment, and it never touches the ticket's SLA — AI handling, queue wait, assignment and ticket age are not inputs, so none of
/// them can move an employee's number.
///
/// <list type="number">
///   <item><description>No offer time → <b>Availability Unknown</b> (the offer event was never received; ticket assignment, queue entry and presence are not substitutes).</description></item>
///   <item><description>No verified roster for that instant → <b>Availability Unknown</b>.</description></item>
///   <item><description>Offered outside the shift or during approved leave → <b>Outside Shift</b>, excluded from performance whatever Genesys says happened to it.</description></item>
///   <item><description>In shift, Genesys confirms an answer, with an answer time not before the offer → <b>Answered</b>; response = answer − offer.</description></item>
///   <item><description>In shift, Genesys confirms no answer → <b>Not Responding</b>.</description></item>
///   <item><description>Anything else (outcome unverified, "answered" without a usable time) → <b>Availability Unknown</b>.</description></item>
/// </list>
/// There is deliberately no threshold: a response time is reported as measured, never judged.
/// </summary>
public static class EmployeeOfferClassifier
{
    public static EmployeeOfferVerdict Classify(GenesysOfferRecord offer, ShiftVerdict shift)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (offer.OfferedAtUtc is null)
        {
            return Unknown("The Genesys offer event for this agent was not received, so the offer time is unknown.");
        }

        if (shift == ShiftVerdict.Unknown)
        {
            return Unknown("No historical shift or leave record covers the offer time, so availability cannot be verified.");
        }

        if (shift is ShiftVerdict.OutsideShift or ShiftVerdict.OnApprovedLeave)
        {
            return new EmployeeOfferVerdict(
                EmployeeOfferOutcomes.OutsideShift, null,
                shift == ShiftVerdict.OnApprovedLeave
                    ? "Offered during approved leave. Excluded from employee performance."
                    : "Offered outside the employee's shift. Excluded from employee performance.");
        }

        switch (offer.Outcome)
        {
            case GenesysOfferOutcome.Answered when offer.AnsweredAtUtc is { } answered && answered >= offer.OfferedAtUtc:
                return new EmployeeOfferVerdict(
                    EmployeeOfferOutcomes.Answered,
                    (answered - offer.OfferedAtUtc.Value).TotalSeconds,
                    "Measured from this employee's offer to this employee's answer. AI handling and queue wait before the offer are not included.");

            case GenesysOfferOutcome.NoAnswerConfirmed:
                return new EmployeeOfferVerdict(
                    EmployeeOfferOutcomes.NotResponding, null,
                    "Genesys confirms the employee did not respond to this in-shift offer. Tracked separately from the ticket SLA.");

            // Confirmed, but no scoring policy exists yet: reported as what they are, never as Not Responding, and not performance-counted.
            case GenesysOfferOutcome.Declined:
                return new EmployeeOfferVerdict(EmployeeOfferOutcomes.Declined, null,
                    "Genesys confirms the employee declined this offer. Shown separately and excluded from performance scoring until a policy is agreed.");
            case GenesysOfferOutcome.Redirected:
                return new EmployeeOfferVerdict(EmployeeOfferOutcomes.Redirected, null,
                    "Genesys confirms this offer was redirected away. Shown separately and excluded from performance scoring until a policy is agreed.");
            case GenesysOfferOutcome.CallerAbandoned:
                return new EmployeeOfferVerdict(EmployeeOfferOutcomes.CallerAbandoned, null,
                    "Genesys confirms the caller abandoned during this offer. Shown separately and excluded from performance scoring until a policy is agreed.");

            default:
                return Unknown("Genesys did not confirm the outcome of this offer (or its answer time is missing or precedes the offer).");
        }
    }

    private static EmployeeOfferVerdict Unknown(string reason) =>
        new(EmployeeOfferOutcomes.AvailabilityUnknown, null, reason + " Excluded from employee performance.");
}

/// <summary>The classifier's answer: the outcome label, the measured response seconds (Answered only) and why.</summary>
public readonly record struct EmployeeOfferVerdict(string Outcome, double? ResponseSeconds, string Explanation);
