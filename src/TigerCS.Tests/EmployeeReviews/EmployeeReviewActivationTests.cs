using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.EmployeeReviews;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Application.Modules.EmployeeReviews.Services;
using TigerCS.Domain.Modules.EmployeeReviews;

namespace TigerCS.Tests.EmployeeReviews;

/// <summary>
/// The 8-character activation code: format, 24-hour expiry, single use, attempt limits and the separate, long device token it produces —
/// over the real schema and the real services (SQLite in memory), so the atomic updates and unique indexes are proven as SQL.
/// </summary>
public sealed class EmployeeReviewActivationTests : IDisposable
{
    private readonly EmployeeReviewFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private async Task<EmployeeReviewDevice> LoadDeviceAsync(Guid deviceId)
    {
        using var context = _fx.CreateContext();
        return await context.EmployeeReviewDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
    }

    private async Task<SubmitEmployeeReviewOutcome> SubmitAsync(string? token)
    {
        using var context = _fx.CreateContext();
        return (await _fx.CreateTabletService(context).SubmitAsync(token, new(Guid.NewGuid(), 5, null))).Outcome;
    }

    private async Task<TabletSessionStatus> SessionAsync(string token)
    {
        using var context = _fx.CreateContext();
        return (await _fx.CreateTabletService(context).GetSessionAsync(token)).Status;
    }

    /// <summary>A well-formed code that is certainly not the one issued.</summary>
    private static string WrongCodeFor(string real) => real[0] == 'A' ? "CCCCCCCC" : "AAAAAAAA";

    // ------------------------------------------------------------------------------------------------ the code itself

    [Fact]
    public void GeneratedCodes_AreEightUpperCaseLettersOrDigits_WithNoHyphenAndNoAmbiguousCharacter()
    {
        const string ambiguous = "0O1ILSZ258B";
        var seen = new HashSet<string>();

        for (var i = 0; i < 2000; i++)
        {
            var code = ActivationCode.Generate();
            seen.Add(code);

            Assert.Equal(8, code.Length);
            Assert.True(ActivationCode.IsWellFormed(code), code);
            Assert.All(code, c => Assert.True(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c), code));
            Assert.DoesNotContain('-', code);
            Assert.All(code, c => Assert.DoesNotContain(c, ambiguous));
        }

        // A cryptographic draw over 25^8 values: 2 000 codes are all different, and every alphabet symbol shows up.
        Assert.Equal(2000, seen.Count);
        Assert.All(ActivationCode.Alphabet, symbol => Assert.Contains(seen, code => code.Contains(symbol)));
    }

    [Theory]
    [InlineData("ACDEFGHJ", true)]
    [InlineData("34679ACD", true)]
    [InlineData("ACDEFGH", false)]       // 7 characters
    [InlineData("ACDEFGHJK", false)]     // 9 characters
    [InlineData("ACDE-FGH", false)]      // hyphen
    [InlineData("ACDE FGH", false)]      // space inside
    [InlineData("ACDEFGH0", false)]      // ambiguous digit
    [InlineData("ACDEFGHO", false)]      // ambiguous letter
    [InlineData("acdefghj", false)]      // lower case must be normalized first
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyExactlyEightAlphabetCharacters_AreWellFormed(string? code, bool expected) =>
        Assert.Equal(expected, ActivationCode.IsWellFormed(code));

    [Fact]
    public void Normalize_TrimsAndUpperCases_ButNeverRepairsHyphensOrInnerSpaces()
    {
        Assert.Equal("ACDEFGHJ", ActivationCode.Normalize("  acdefghj \n"));
        Assert.False(ActivationCode.IsWellFormed(ActivationCode.Normalize("acde-fghj")));
        Assert.False(ActivationCode.IsWellFormed(ActivationCode.Normalize("acde fghj")));
        Assert.Null(ActivationCode.Normalize("   "));
    }

    [Fact]
    public void TheDeviceToken_IsStillTheLongPermanentCredential_NotTheShortCode()
    {
        Assert.Equal(47, DeviceToken.EncodedLength);
        Assert.True(DeviceToken.IsWellFormed(DeviceToken.Generate()));
        Assert.False(DeviceToken.IsWellFormed(ActivationCode.Generate()));
        Assert.NotEqual(DeviceToken.Generate(), DeviceToken.Generate());
    }

    // ------------------------------------------------------------------------------------------------ registering and storing

    [Fact]
    public async Task Register_ReturnsAnEightCharacterCode_ExpiringIn24Hours_AndNoCredentialYet()
    {
        using var context = _fx.CreateContext();
        var result = await _fx.CreateAdminService(context).RegisterAsync(_fx.Admin, new("Desk 1", _fx.Amal));

        Assert.True(result.IsSuccess);
        Assert.True(ActivationCode.IsWellFormed(result.Value!.ActivationCode));
        Assert.Equal(EmployeeReviewFixture.Now + Day, result.Value.ExpiresAtUtc);
        Assert.Equal(result.Value.ExpiresAtUtc, result.Value.Device.ActivationCodeExpiresAtUtc);
        Assert.True(result.Value.Device.ActivationPending);
        Assert.False(result.Value.Device.HasCredential);
    }

    [Fact]
    public async Task TheCode_IsStoredOnlyAsASaltedHash_AndNeverInTheAudit()
    {
        var (deviceId, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);

        var device = await LoadDeviceAsync(deviceId);
        Assert.Equal(32, device.ActivationCodeHash!.Length);
        Assert.Equal(16, device.ActivationCodeSalt!.Length);
        Assert.NotEqual(System.Text.Encoding.ASCII.GetBytes(code), device.ActivationCodeHash);
        Assert.Equal(ActivationCode.Hash(code, device.ActivationCodeSalt), device.ActivationCodeHash);
        Assert.Null(device.TokenHash);

        using var context = _fx.CreateContext();
        Assert.DoesNotContain(context.AuditEntries.ToList(), a => (a.AfterValue ?? "").Contains(code) || (a.BeforeValue ?? "").Contains(code));
    }

    [Fact]
    public async Task TwoTablets_GetDifferentSalts_SoEqualCodesWouldStillHashDifferently()
    {
        var (first, _) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        var (second, _) = await _fx.RegisterPendingAsync("Desk 2", _fx.Bilal);

        Assert.NotEqual((await LoadDeviceAsync(first)).ActivationCodeSalt, (await LoadDeviceAsync(second)).ActivationCodeSalt);
    }

    // ------------------------------------------------------------------------------------------------ activation and the separate credential

    [Fact]
    public async Task ValidCode_ActivatesTheTablet_WithADifferentLongToken_AndOnlyItsHashIsStored()
    {
        var (deviceId, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);

        var result = await _fx.ActivateAsync(code);

        Assert.Equal(ActivateTabletOutcome.Activated, result.Outcome);
        var token = result.DeviceToken!;
        Assert.True(DeviceToken.IsWellFormed(token));
        Assert.Equal(47, token.Length);
        Assert.NotEqual(code, token);
        Assert.DoesNotContain(code, token);

        var device = await LoadDeviceAsync(deviceId);
        Assert.Equal(DeviceToken.Hash(token), device.TokenHash);
        Assert.Null(device.ActivationCodeHash);
        Assert.Null(device.ActivationCodeSalt);
        Assert.Null(device.ActivationCodeExpiresAtUtc);
        Assert.Equal(TabletSessionStatus.Ready, await SessionAsync(token));
        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, await SubmitAsync(token));
    }

    [Fact]
    public async Task TheCode_IsNotAcceptedAsADeviceToken_AndTheTokenIsNotAcceptedAsACode()
    {
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        Assert.Equal(TabletSessionStatus.Invalid, await SessionAsync(code));
        Assert.Equal(SubmitEmployeeReviewOutcome.InvalidDevice, await SubmitAsync(code));

        var token = (await _fx.ActivateAsync(code)).DeviceToken!;
        Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(token)).Outcome);
    }

    [Fact]
    public async Task CodeTypedInLowerCaseWithSurroundingSpaces_StillActivates()
    {
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);

        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync("  " + code.ToLowerInvariant() + " ")).Outcome);
    }

    [Theory]
    [InlineData("ACDE-FGH")]
    [InlineData("ACDEFGH")]
    [InlineData("ACDEFGHJK")]
    [InlineData("tgr-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("")]
    [InlineData(null)]
    public async Task MalformedCodes_AreRefusedAsInvalid_AndNeverActivateAnything(string? submitted)
    {
        var (deviceId, _) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);

        Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(submitted)).Outcome);

        Assert.Null((await LoadDeviceAsync(deviceId)).TokenHash);
    }

    [Fact]
    public async Task EachCode_ActivatesOnlyItsOwnTablet()
    {
        var (first, firstCode) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        var (second, secondCode) = await _fx.RegisterPendingAsync("Desk 2", _fx.Bilal);

        var secondToken = (await _fx.ActivateAsync(secondCode)).DeviceToken!;

        Assert.Null((await LoadDeviceAsync(first)).TokenHash);
        Assert.Equal(DeviceToken.Hash(secondToken), (await LoadDeviceAsync(second)).TokenHash);
        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(firstCode)).Outcome);
    }

    [Fact]
    public async Task ATabletSwitchedOffBeforeActivation_ReportsInactive_WithoutUsingUpTheCode()
    {
        var (deviceId, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        using (var context = _fx.CreateContext())
        {
            Assert.True((await _fx.CreateAdminService(context).SetActiveAsync(_fx.Admin, deviceId, false)).IsSuccess);
        }

        var result = await _fx.ActivateAsync(code);

        Assert.Equal(ActivateTabletOutcome.Inactive, result.Outcome);
        Assert.Null(result.DeviceToken);
        Assert.NotNull((await LoadDeviceAsync(deviceId)).ActivationCodeHash);
    }

    // ------------------------------------------------------------------------------------------------ expiration

    [Fact]
    public async Task CodeWorks_UntilTheLastMomentBefore24Hours()
    {
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        _fx.Time.UtcNow = EmployeeReviewFixture.Now + Day - TimeSpan.FromSeconds(1);

        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(code)).Outcome);
    }

    [Theory]
    [InlineData(0)]          // exactly 24 hours
    [InlineData(1)]          // a second later
    [InlineData(60 * 60 * 24 * 30)]
    public async Task ExpiredCode_IsRefused_LeavesTheTabletWithoutACredential_AndCanBeReplaced(int secondsAfterExpiry)
    {
        var (deviceId, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        _fx.Time.UtcNow = EmployeeReviewFixture.Now + Day + TimeSpan.FromSeconds(secondsAfterExpiry);

        var result = await _fx.ActivateAsync(code);

        Assert.Equal(ActivateTabletOutcome.InvalidCode, result.Outcome);
        Assert.Null(result.DeviceToken);
        Assert.Null((await LoadDeviceAsync(deviceId)).TokenHash);

        // The administrator sees it as expired, not as pending, and can issue a fresh code that works.
        using (var context = _fx.CreateContext())
        {
            var listed = (await _fx.CreateAdminService(context).ListAsync()).Single();
            Assert.False(listed.ActivationPending);
            Assert.NotNull(listed.ActivationCodeExpiresAtUtc);
            var fresh = (await _fx.CreateAdminService(context).IssueActivationCodeAsync(_fx.Admin, deviceId)).Value!;
            Assert.Equal(_fx.Time.UtcNow + Day, fresh.ExpiresAtUtc);
            Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(fresh.ActivationCode)).Outcome);
        }
    }

    // ------------------------------------------------------------------------------------------------ single use

    [Fact]
    public async Task ACode_WorksOnce_ThenNeverAgain_EvenForTheSameClient()
    {
        var (deviceId, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);

        var first = await _fx.ActivateAsync(code, "kiosk");
        var second = await _fx.ActivateAsync(code, "kiosk");
        var elsewhere = await _fx.ActivateAsync(code);

        Assert.Equal(ActivateTabletOutcome.Activated, first.Outcome);
        Assert.Equal(ActivateTabletOutcome.InvalidCode, second.Outcome);
        Assert.Null(second.DeviceToken);
        Assert.Equal(ActivateTabletOutcome.InvalidCode, elsewhere.Outcome);
        Assert.Equal(DeviceToken.Hash(first.DeviceToken), (await LoadDeviceAsync(deviceId)).TokenHash);
    }

    [Fact]
    public async Task TwoSimultaneousRedemptions_OfOneCode_HaveExactlyOneWinner_BecauseTheWriteIsConditional()
    {
        var (deviceId, _) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        var expected = (await LoadDeviceAsync(deviceId)).ActivationCodeHash!;
        var firstToken = DeviceToken.Hash(DeviceToken.Generate())!;
        var secondToken = DeviceToken.Hash(DeviceToken.Generate())!;

        using var first = _fx.CreateContext();
        using var second = _fx.CreateContext();
        var secondRepository = _fx.CreateRepository(second);

        // The second request has already READ the pending code (its context tracks the row)...
        await secondRepository.GetDeviceAsync(deviceId);
        // ...when the first request redeems it and commits.
        Assert.True(await _fx.CreateRepository(first).TryRedeemActivationAsync(deviceId, expected, firstToken, _fx.Time.UtcNow));

        // The second still believes the code is pending. Only the database's conditional UPDATE stops it from minting a second token.
        Assert.False(await secondRepository.TryRedeemActivationAsync(deviceId, expected, secondToken, _fx.Time.UtcNow));

        Assert.Equal(firstToken, (await LoadDeviceAsync(deviceId)).TokenHash);
    }

    [Fact]
    public async Task TwoSimultaneousAttempts_BothCount_BecauseTheCounterIsConditionalToo()
    {
        var key = EmployeeReviewActivationService.ClientKey("shared-client");
        using var first = _fx.CreateContext();
        using var second = _fx.CreateContext();
        var firstRepository = _fx.CreateRepository(first);
        var secondRepository = _fx.CreateRepository(second);

        Assert.Equal(1, await firstRepository.RegisterActivationAttemptAsync(key, EmployeeReviewActivationService.ClientWindow, _fx.Time.UtcNow));
        // The second context reads the row (count 1), then the first adds another attempt behind its back...
        Assert.Equal(2, await secondRepository.RegisterActivationAttemptAsync(key, EmployeeReviewActivationService.ClientWindow, _fx.Time.UtcNow));
        Assert.Equal(3, await firstRepository.RegisterActivationAttemptAsync(key, EmployeeReviewActivationService.ClientWindow, _fx.Time.UtcNow));

        // ...and nothing was lost: three attempts are stored.
        using var check = _fx.CreateContext();
        Assert.Equal(3, (await check.EmployeeReviewActivationThrottles.AsNoTracking().SingleAsync(t => t.Key == key)).AttemptCount);
    }

    [Fact]
    public async Task RedemptionAtTheDatabase_RefusesAnExpiredOrReplacedCode_EvenIfTheServiceCheckWasRacedPast()
    {
        var (deviceId, _) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        var stale = (await LoadDeviceAsync(deviceId)).ActivationCodeHash!;
        using (var context = _fx.CreateContext())
        {
            await _fx.CreateAdminService(context).IssueActivationCodeAsync(_fx.Admin, deviceId);
        }

        using var late = _fx.CreateContext();
        Assert.False(await _fx.CreateRepository(late).TryRedeemActivationAsync(deviceId, stale, DeviceToken.Hash(DeviceToken.Generate())!, _fx.Time.UtcNow));

        var current = (await LoadDeviceAsync(deviceId)).ActivationCodeHash!;
        Assert.False(await _fx.CreateRepository(late).TryRedeemActivationAsync(
            deviceId, current, DeviceToken.Hash(DeviceToken.Generate())!, _fx.Time.UtcNow + Day + TimeSpan.FromSeconds(1)));
        Assert.Null((await LoadDeviceAsync(deviceId)).TokenHash);
    }

    [Fact]
    public async Task IssuingANewCode_ReplacesTheOldOne_WhichStopsWorking()
    {
        var (deviceId, oldCode) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        string newCode;
        using (var context = _fx.CreateContext())
        {
            newCode = (await _fx.CreateAdminService(context).IssueActivationCodeAsync(_fx.Admin, deviceId)).Value!.ActivationCode;
        }

        Assert.NotEqual(oldCode, newCode);
        Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(oldCode)).Outcome);
        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(newCode)).Outcome);
    }

    [Fact]
    public async Task RevokingATablet_DiscardsItsPendingCode()
    {
        var (deviceId, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        using (var context = _fx.CreateContext())
        {
            await _fx.CreateAdminService(context).RevokeAsync(_fx.Admin, deviceId);
        }

        var device = await LoadDeviceAsync(deviceId);
        Assert.Null(device.ActivationCodeHash);
        Assert.Null(device.ActivationCodeExpiresAtUtc);
        Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(code)).Outcome);
    }

    // ------------------------------------------------------------------------------------------------ brute-force protection

    [Fact]
    public async Task AfterTheClientLimitOfWrongGuesses_AClientIsLockedOut_EvenWithTheCorrectCode()
    {
        var (deviceId, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        var wrong = WrongCodeFor(code);

        for (var i = 0; i < EmployeeReviewActivationService.MaxAttemptsPerClient; i++)
        {
            Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(wrong, "attacker")).Outcome);
        }

        var locked = await _fx.ActivateAsync(code, "attacker");
        Assert.Equal(ActivateTabletOutcome.TooManyAttempts, locked.Outcome);
        Assert.Null(locked.DeviceToken);
        Assert.Null((await LoadDeviceAsync(deviceId)).TokenHash);
        Assert.NotNull((await LoadDeviceAsync(deviceId)).ActivationCodeHash);   // the code was not even looked at, so it is not used up

        // Still locked on every further try within the window.
        _fx.Time.UtcNow += TimeSpan.FromMinutes(14);
        Assert.Equal(ActivateTabletOutcome.TooManyAttempts, (await _fx.ActivateAsync(code, "attacker")).Outcome);
    }

    [Fact]
    public async Task TheLockout_BelongsToTheClient_AnotherClientWithTheRightCodeIsNotAffected()
    {
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        for (var i = 0; i < EmployeeReviewActivationService.MaxAttemptsPerClient + 2; i++)
        {
            await _fx.ActivateAsync(WrongCodeFor(code), "attacker");
        }

        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(code, "the-real-kiosk")).Outcome);
    }

    [Fact]
    public async Task TheLockout_EndsWhenTheWindowPasses()
    {
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        for (var i = 0; i < EmployeeReviewActivationService.MaxAttemptsPerClient + 1; i++)
        {
            await _fx.ActivateAsync(WrongCodeFor(code), "kiosk");
        }

        Assert.Equal(ActivateTabletOutcome.TooManyAttempts, (await _fx.ActivateAsync(code, "kiosk")).Outcome);

        _fx.Time.UtcNow += EmployeeReviewActivationService.ClientWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(code, "kiosk")).Outcome);
    }

    [Fact]
    public async Task MalformedInput_CountsAsAGuess_SoJunkCannotBeUsedToProbeForFree()
    {
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        for (var i = 0; i < EmployeeReviewActivationService.MaxAttemptsPerClient; i++)
        {
            Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync("not a code", "kiosk")).Outcome);
        }

        Assert.Equal(ActivateTabletOutcome.TooManyAttempts, (await _fx.ActivateAsync(code, "kiosk")).Outcome);
    }

    [Fact]
    public async Task EachAttempt_IsCountedAtomicallyBeforeItIsChecked_SoTheCounterRisesByExactlyOnePerCall()
    {
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);

        // The service registers an attempt (one atomic increment) before it looks at the code, so the sixth is refused whatever order the earlier ones ran in.
        var counts = new List<int>();
        for (var i = 0; i < EmployeeReviewActivationService.MaxAttemptsPerClient + 3; i++)
        {
            using var context = _fx.CreateContext();
            counts.Add(await _fx.CreateRepository(context).RegisterActivationAttemptAsync(
                EmployeeReviewActivationService.ClientKey("attacker"), EmployeeReviewActivationService.ClientWindow, _fx.Time.UtcNow));
        }

        Assert.Equal(Enumerable.Range(1, EmployeeReviewActivationService.MaxAttemptsPerClient + 3), counts);
        Assert.Equal(ActivateTabletOutcome.TooManyAttempts, (await _fx.ActivateAsync(code, "attacker")).Outcome);
    }

    [Fact]
    public async Task AnAttackerRotatingAddresses_HitsTheOverallLimit_AndEvenTheRightCodeIsRefusedUntilItEnds()
    {
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        var wrong = WrongCodeFor(code);

        for (var i = 0; i < EmployeeReviewActivationService.MaxAttemptsOverall; i++)
        {
            Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(wrong, "address-" + i)).Outcome);
        }

        Assert.Equal(ActivateTabletOutcome.TooManyAttempts, (await _fx.ActivateAsync(code, "yet-another-address")).Outcome);

        _fx.Time.UtcNow += EmployeeReviewActivationService.OverallWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(code, "yet-another-address")).Outcome);
    }

    [Fact]
    public async Task AGenuineActivation_IsNotAGuess_ItClearsTheClientAndRefundsTheSharedBudget()
    {
        var (_, first) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        var (_, second) = await _fx.RegisterPendingAsync("Desk 2", _fx.Bilal);

        for (var i = 0; i < EmployeeReviewActivationService.MaxAttemptsPerClient - 1; i++)
        {
            await _fx.ActivateAsync(WrongCodeFor(first), "kiosk");
        }

        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(first, "kiosk")).Outcome);

        // The same person set up a second tablet straight away: their earlier typos no longer count against them.
        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(second, "kiosk")).Outcome);

        using var context = _fx.CreateContext();
        var overall = await context.EmployeeReviewActivationThrottles.AsNoTracking().SingleAsync(t => t.Key == EmployeeReviewActivationThrottle.GlobalKey);
        Assert.Equal(EmployeeReviewActivationService.MaxAttemptsPerClient - 1, overall.AttemptCount);   // only the wrong guesses remain
    }

    [Fact]
    public async Task TheAttemptCounters_KeepNoClientAddressInTheClear()
    {
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        await _fx.ActivateAsync(WrongCodeFor(code), "203.0.113.77");

        using var context = _fx.CreateContext();
        var keys = await context.EmployeeReviewActivationThrottles.AsNoTracking().Select(t => t.Key).ToListAsync();

        Assert.Contains(EmployeeReviewActivationThrottle.GlobalKey, keys);
        Assert.Contains(keys, k => k.StartsWith("c:", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.Contains("203.0.113.77"));
        Assert.All(keys, k => Assert.True(k.Length <= EmployeeReviewActivationThrottle.KeyMaxLength));
    }

    // ------------------------------------------------------------------------------------------------ several tablets behind one office address

    [Fact]
    public async Task SeveralTablets_BehindOneOfficeAddress_CanAllBeActivated_EvenWithTypos()
    {
        var tablets = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            tablets.Add((await _fx.RegisterPendingAsync($"Desk {i}", i % 2 == 0 ? _fx.Amal : _fx.Bilal)).Code);
        }

        // Each tablet is set up by someone who mistypes twice first, all from the same office address, one after the other.
        foreach (var code in tablets)
        {
            Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(WrongCodeFor(code), "office")).Outcome);
            Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(WrongCodeFor(code), "office")).Outcome);
            var activated = await _fx.ActivateAsync(code, "office");
            Assert.Equal(ActivateTabletOutcome.Activated, activated.Outcome);
            Assert.True(DeviceToken.IsWellFormed(activated.DeviceToken));
        }

        using var context = _fx.CreateContext();
        Assert.Equal(6, await context.EmployeeReviewDevices.AsNoTracking().CountAsync(d => d.TokenHash != null));
    }

    [Fact]
    public async Task TabletsBehindOneAddress_ActivatedInTurns_NeverLockEachOtherOut_BecauseEachSuccessStartsFresh()
    {
        var codes = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            codes.Add((await _fx.RegisterPendingAsync($"Desk {i}", _fx.Amal)).Code);
        }

        // Nine mistakes short of the limit before every single activation, four times over: 36 typos in all, none of them locks the office.
        foreach (var code in codes)
        {
            for (var typo = 0; typo < EmployeeReviewActivationService.MaxAttemptsPerClient - 1; typo++)
            {
                Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(WrongCodeFor(code), "office")).Outcome);
            }

            Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(code, "office")).Outcome);
        }
    }

    [Fact]
    public async Task TheOverallBudget_LeavesRoomForALegitimateOffice_ThatDoesAFewDozenTypos()
    {
        // The overall ceiling (100/hour) must sit well above what real set-up work needs, or it would lock everybody out on a busy day.
        Assert.True(EmployeeReviewActivationService.MaxAttemptsOverall >= 5 * 6 * 3, "room for a handful of offices activating several tablets each");

        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        for (var i = 0; i < 40; i++)
        {
            await _fx.ActivateAsync(WrongCodeFor(code), "address-" + i % 8);
        }

        Assert.Equal(ActivateTabletOutcome.Activated, (await _fx.ActivateAsync(code, "address-new")).Outcome);
    }

    // ------------------------------------------------------------------------------------------------ persistence

    [Fact]
    public async Task ActivationPersists_AcrossNewContexts_ReviewsAreStored_AndHistorySurvivesReProvisioning()
    {
        var (deviceId, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        var token = (await _fx.ActivateAsync(code)).DeviceToken!;

        // Brand-new contexts (a restart of the Api) still recognise the tablet.
        Assert.Equal(TabletSessionStatus.Ready, await SessionAsync(token));
        await _fx.SubmitAsync(token, 5, "Great", EmployeeReviewFixture.Now.AddMinutes(1));
        await _fx.SubmitAsync(token, 4, null, EmployeeReviewFixture.Now.AddMinutes(2));

        // The administrator re-provisions the tablet and it is activated again: its reviews and their employee are untouched.
        string secondCode;
        using (var context = _fx.CreateContext())
        {
            secondCode = (await _fx.CreateAdminService(context).IssueActivationCodeAsync(_fx.Admin, deviceId, confirmReplaceActiveCredential: true)).Value!.ActivationCode;
        }

        var secondToken = (await _fx.ActivateAsync(secondCode)).DeviceToken!;
        Assert.NotEqual(token, secondToken);
        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, await SubmitAsync(secondToken));

        using var check = _fx.CreateContext();
        var reviews = await check.EmployeeReviews.AsNoTracking().Where(r => r.DeviceId == deviceId).ToListAsync();
        Assert.Equal(3, reviews.Count);
        Assert.All(reviews, r => Assert.Equal(_fx.Amal, r.EmployeeId));
    }

    [Fact]
    public async Task AnAlreadyActivatedTablet_WithNoActivationColumns_KeepsWorking_AndItsHistoryIsIntact()
    {
        // A tablet registered before this change: it holds only a device token; every activation column is NULL.
        var token = DeviceToken.Generate();
        Guid legacyId;
        using (var context = _fx.CreateContext())
        {
            var legacy = new EmployeeReviewDevice("Legacy desk", _fx.Bilal, DeviceToken.Hash(token)!, EmployeeReviewFixture.Now.AddDays(-30));
            legacyId = legacy.Id;
            await _fx.CreateRepository(context).AddDeviceAsync(legacy);
            await _fx.CreateRepository(context).SaveChangesAsync();
        }

        await _fx.SubmitAsync(token, 5, "Before the change", EmployeeReviewFixture.Now.AddDays(-2));

        // New tablets are registered, activated, attacked and expired around it.
        var (_, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        for (var i = 0; i < 8; i++)
        {
            await _fx.ActivateAsync(WrongCodeFor(code), "attacker");
        }

        _fx.Time.UtcNow += Day * 3;

        var legacyDevice = await LoadDeviceAsync(legacyId);
        Assert.True(legacyDevice.CanCollectReviews);
        Assert.Null(legacyDevice.ActivationCodeHash);
        Assert.Equal(TabletSessionStatus.Ready, await SessionAsync(token));
        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, await SubmitAsync(token));

        using var check = _fx.CreateContext();
        Assert.Equal(2, await check.EmployeeReviews.AsNoTracking().CountAsync(r => r.DeviceId == legacyId));
        var listed = (await _fx.CreateAdminService(check).ListAsync()).Single(d => d.Id == legacyId);
        Assert.True(listed.HasCredential && listed.IsActive);
        Assert.False(listed.ActivationPending);
    }

    [Fact]
    public async Task ALegacyTablet_CannotBeActivatedByGuessingItsToken_AsAShortCode()
    {
        var token = DeviceToken.Generate();
        using (var context = _fx.CreateContext())
        {
            await _fx.CreateRepository(context).AddDeviceAsync(new EmployeeReviewDevice("Legacy desk", _fx.Bilal, DeviceToken.Hash(token)!, EmployeeReviewFixture.Now));
            await _fx.CreateRepository(context).SaveChangesAsync();
        }

        // Presenting its own (long) token to the activation endpoint is not a valid code: no new token is minted from it.
        Assert.Equal(ActivateTabletOutcome.InvalidCode, (await _fx.ActivateAsync(token)).Outcome);
        Assert.Equal(TabletSessionStatus.Ready, await SessionAsync(token));
    }

    [Fact]
    public async Task ActivationIsAudited_WithoutTheCodeOrTheToken()
    {
        var (deviceId, code) = await _fx.RegisterPendingAsync("Desk 1", _fx.Amal);
        var token = (await _fx.ActivateAsync(code)).DeviceToken!;

        using var context = _fx.CreateContext();
        var entries = context.AuditEntries.Where(a => a.EntityType == "EmployeeReviewDevice").ToList();
        Assert.Contains(entries, a => a.Action == "ActivateReviewDevice" && a.EntityId == deviceId.ToString());
        Assert.DoesNotContain(entries, a => (a.AfterValue ?? "").Contains(code) || (a.AfterValue ?? "").Contains(token));
    }
}
