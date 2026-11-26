namespace TigerCS.Domain.Modules.EmployeeReviews;

/// <summary>
/// One counter of tablet-activation attempts inside a fixed window. Kept in the database (not in memory) so the limit survives restarts and
/// holds across several Api instances. A row is keyed either by a hashed client address or by the shared <see cref="GlobalKey"/>.
/// The counter is an optimistic-concurrency token, so concurrent guesses are serialized by the database and none can slip past uncounted.
/// </summary>
public class EmployeeReviewActivationThrottle
{
    public const int KeyMaxLength = 64;

    /// <summary>The counter shared by every caller: a ceiling on guessing even when an attacker rotates client addresses.</summary>
    public const string GlobalKey = "global";

    public string Key { get; private set; } = string.Empty;

    /// <summary>Attempts counted in the current window (an attempt is counted before its code is checked).</summary>
    public int AttemptCount { get; private set; }

    public DateTime WindowStartUtc { get; private set; }

    private EmployeeReviewActivationThrottle()
    {
    }

    /// <summary>
    /// Counts one more attempt and returns the total in the current window, first starting a new window when the last one has run its course.
    /// <see cref="AttemptCount"/> is a concurrency token: if another request changed it first, saving this one fails and the caller reloads and
    /// counts again — so every attempt adds exactly one and none is lost.
    /// </summary>
    public int Register(DateTime nowUtc, TimeSpan window)
    {
        if (nowUtc - WindowStartUtc >= window)
        {
            AttemptCount = 0;
            WindowStartUtc = nowUtc;
        }

        return ++AttemptCount;
    }

    /// <summary>Forgets the attempts (a successful activation by this client).</summary>
    public void Clear() => AttemptCount = 0;

    /// <summary>Gives one attempt back (a successful activation must not use up the shared budget).</summary>
    public void Refund()
    {
        if (AttemptCount > 0)
        {
            AttemptCount--;
        }
    }

    public EmployeeReviewActivationThrottle(string key, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > KeyMaxLength)
        {
            throw new ArgumentException($"A throttle key is 1 to {KeyMaxLength} characters.", nameof(key));
        }

        Key = key;
        WindowStartUtc = nowUtc;
    }
}
