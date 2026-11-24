namespace TigerCS.Domain.Modules.EmployeeReviews;

/// <summary>
/// A customer's 1–5 star review of an employee, collected on a review tablet. Independent of the ticket review: it never touches a ticket.
/// <see cref="EmployeeId"/> is copied from the tablet's assignment at submission time, so reassigning the tablet later never moves history.
/// <see cref="SubmissionId"/> (client-generated, unique) makes a retried submission harmless.
/// </summary>
public class EmployeeReview
{
    public const int MinRating = 1;
    public const int MaxRating = 5;
    public const int CommentMaxLength = 1000;

    public Guid Id { get; private set; }

    public Guid DeviceId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public byte Rating { get; private set; }

    public string? Comment { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public Guid SubmissionId { get; private set; }

    private EmployeeReview()
    {
    }

    public EmployeeReview(EmployeeReviewDevice device, Guid submissionId, int rating, string? comment, DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (submissionId == Guid.Empty)
        {
            throw new ArgumentException("A submission id is required.", nameof(submissionId));
        }

        if (rating is < MinRating or > MaxRating)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "A rating must be between 1 and 5.");
        }

        var trimmed = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (trimmed is { Length: > CommentMaxLength })
        {
            throw new ArgumentException($"A comment is at most {CommentMaxLength} characters.", nameof(comment));
        }

        Id = Guid.NewGuid();
        DeviceId = device.Id;
        EmployeeId = device.EmployeeId;
        Rating = (byte)rating;
        Comment = trimmed;
        CreatedAtUtc = createdAtUtc;
        SubmissionId = submissionId;
    }
}
