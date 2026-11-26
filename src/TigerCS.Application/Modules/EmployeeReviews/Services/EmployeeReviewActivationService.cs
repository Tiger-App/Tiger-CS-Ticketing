using System.Security.Cryptography;
using System.Text;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.EmployeeReviews.Abstractions;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Domain.Modules.EmployeeReviews;

namespace TigerCS.Application.Modules.EmployeeReviews.Services;

/// <summary>
/// Turns an administrator's one-time activation code into a tablet's permanent device token. Reached without a login, so every protection is here:
/// <list type="bullet">
/// <item><b>Attempt limits, enforced before the code is looked at.</b> Each call is counted first (atomically, in the database) against the
/// calling client and against one shared counter. Over either limit the code is not checked at all — even a correct one is refused — so
/// guessing cannot be sped up by running requests in parallel. Malformed input counts like any other guess.</item>
/// <item><b>Single use.</b> Redemption is one conditional update; of two simultaneous requests with the same code exactly one succeeds.</item>
/// <item><b>Expiry.</b> A code older than 24 hours is never redeemable, even though its hash may still sit in the row.</item>
/// <item><b>Separate credential.</b> The token returned is new and unrelated to the code; the code is erased when it is redeemed.</item>
/// <item><b>No oracle.</b> Wrong, expired, used and replaced codes all answer <see cref="ActivateTabletOutcome.InvalidCode"/>, and the hashes are
/// compared in constant time over every pending tablet.</item>
/// </list>
/// </summary>
public sealed class EmployeeReviewActivationService(
    IEmployeeReviewRepository repository,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Failed guesses one client address may make per window. An office sets several tablets up behind ONE address, and every typo by any of them
    /// counts here, so this leaves room for a whole afternoon of typos; a successful activation clears it. The real ceiling on guessing is
    /// <see cref="MaxAttemptsOverall"/>, which no change of address can get around.
    /// </summary>
    public const int MaxAttemptsPerClient = 10;

    public static readonly TimeSpan ClientWindow = TimeSpan.FromMinutes(15);

    /// <summary>Guesses all clients together may make per window: with 25^8 codes, a day of the maximum is about 2 400 guesses against 1.5 × 10^11.</summary>
    public const int MaxAttemptsOverall = 100;

    public static readonly TimeSpan OverallWindow = TimeSpan.FromHours(1);

    public async Task<ActivateTabletResultDto> ActivateAsync(string? clientAddress, string? submittedCode, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var clientKey = ClientKey(clientAddress);

        // Count the attempt BEFORE checking anything, so a burst of parallel guesses is counted as it arrives rather than after the fact.
        var clientAttempts = await repository.RegisterActivationAttemptAsync(clientKey, ClientWindow, now, cancellationToken);
        var overallAttempts = await repository.RegisterActivationAttemptAsync(EmployeeReviewActivationThrottle.GlobalKey, OverallWindow, now, cancellationToken);
        if (clientAttempts > MaxAttemptsPerClient || overallAttempts > MaxAttemptsOverall)
        {
            return new ActivateTabletResultDto(ActivateTabletOutcome.TooManyAttempts);
        }

        var code = ActivationCode.Normalize(submittedCode);
        var wellFormed = ActivationCode.IsWellFormed(code);
        var device = await FindMatchingDeviceAsync(wellFormed ? code! : null, now, cancellationToken);
        if (device is null)
        {
            return new ActivateTabletResultDto(ActivateTabletOutcome.InvalidCode);
        }

        var employee = await repository.GetEmployeeInfoAsync(device.EmployeeId, cancellationToken);
        if (!device.IsActive || employee is not { IsActive: true })
        {
            return new ActivateTabletResultDto(ActivateTabletOutcome.Inactive);
        }

        var token = DeviceToken.Generate();
        if (!await repository.TryRedeemActivationAsync(device.Id, device.ActivationCodeHash!, DeviceToken.Hash(token)!, now, cancellationToken))
        {
            // Lost a race for the same code (or an administrator replaced it a moment ago): to this caller the code simply no longer works.
            return new ActivateTabletResultDto(ActivateTabletOutcome.InvalidCode);
        }

        // A genuine activation is not a guess: give the shared budget back and let this client start fresh.
        await repository.ClearActivationAttemptsAsync(clientKey, cancellationToken);
        await repository.RefundActivationAttemptAsync(EmployeeReviewActivationThrottle.GlobalKey, cancellationToken);
        await auditWriter.WriteAsync(
            null, "ActivateReviewDevice", "EmployeeReviewDevice", device.Id.ToString(), null, "activation code redeemed", Guid.NewGuid(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return new ActivateTabletResultDto(ActivateTabletOutcome.Activated, token);
    }

    /// <summary>
    /// The pending tablet whose code equals <paramref name="code"/>, or <c>null</c>. Every pending tablet is always hashed and compared (and one
    /// dummy hash is done when there are none or the input is malformed), so how long the answer takes says nothing about which codes exist.
    /// </summary>
    private async Task<EmployeeReviewDevice?> FindMatchingDeviceAsync(string? code, DateTime now, CancellationToken cancellationToken)
    {
        var pending = await repository.ListRedeemableActivationDevicesAsync(now, cancellationToken);
        var probe = code ?? new string(ActivationCode.Alphabet[0], ActivationCode.Length);

        EmployeeReviewDevice? match = null;
        foreach (var device in pending)
        {
            if (ActivationCode.Matches(probe, device.ActivationCodeSalt!, device.ActivationCodeHash!) && code is not null)
            {
                match = device;
            }
        }

        if (pending.Count == 0)
        {
            _ = ActivationCode.Hash(probe, new byte[ActivationCode.SaltLength]);
        }

        return match;
    }

    /// <summary>A fixed-length, non-reversible key for a client address, so no address is stored in the clear.</summary>
    public static string ClientKey(string? clientAddress)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("review-activation|" + (string.IsNullOrWhiteSpace(clientAddress) ? "unknown" : clientAddress.Trim())));
        return "c:" + Convert.ToHexString(bytes, 0, 16);
    }
}
