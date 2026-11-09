using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Exceptions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The two unit-level exclusion policies of Collections campaigns and the manual inclusion of GULF_CUSTOMERS units:
/// permanent special cases (never), GULF_CUSTOMERS (Manual on the 14th Dubai, or manually included in one Manual campaign; Auto never), overlap (special wins), the date and the inclusion read again
/// right before the upload, and the audit. The harness clock is noon in Dubai on the stated day; the units are TP140-n (Customer n, tenant T000n).
/// </summary>
public sealed class ExceptionPoliciesTests
{
    private const string Special = "TP140-1", Gulf = "TP140-2", OtherGulf = "TP140-4";

    private static DateTime DubaiNoon(int month, int day) => new(2026, month, day, 8, 0, 0, DateTimeKind.Utc);

    private static PactReceivablesOptions Configured(params (string Special, string Gulf)[] _) => new()
    {
        SpecialCasesConfigured = true, SpecialCaseUnitCodes = [Special], GulfCustomersConfigured = true, GulfCustomerUnitCodes = [Gulf, OtherGulf]
    };

    /// <summary>
    /// Customer 1 = special case, 2 and 4 = GULF_CUSTOMERS, 3 = ordinary. Unit 5 is customer 2's OTHER unit (same tenant, not in any list); unit 6 is customer 3's other unit on the SAME phone number.
    /// </summary>
    private static PactReceivableInstalment[] Rows() =>
    [
        ReviewHarness.Row(1, 100m), ReviewHarness.Row(2, 200m), ReviewHarness.Row(3, 300m), ReviewHarness.Row(4, 400m),
        ReviewHarness.Row(2, 250m) with { UnitId = 1005, UnitCode = "TP140-5", VoucherNumber = "INV-5", Mobile = "971500000005", DueDate = new DateTime(2026, 10, 25) },
        ReviewHarness.Row(3, 350m) with { UnitId = 1006, UnitCode = "TP140-6", VoucherNumber = "INV-6", DueDate = new DateTime(2026, 10, 25) }
    ];

    private static async Task<ReviewHarness> SeededAsync(DateTime utcNow, Action<ReviewHarness>? configure = null, bool configurePolicies = true)
    {
        var h = new ReviewHarness();
        h.Time.Set(utcNow); h.Source.ReadAt = utcNow;
        if (configurePolicies)
        {
            var c = Configured();
            h.Sql.SpecialCasesConfigured = c.SpecialCasesConfigured; h.Sql.SpecialCaseUnitCodes = c.SpecialCaseUnitCodes;
            h.Sql.GulfCustomersConfigured = c.GulfCustomersConfigured; h.Sql.GulfCustomerUnitCodes = c.GulfCustomerUnitCodes;
        }
        configure?.Invoke(h);
        h.Source.Items.AddRange(Rows());
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual() => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m);
    private static ReviewFilter Auto() => new(ReminderType: "CurrentMonth", SendingMode: "Auto", MinRemaining: 0m);
    private static SelectionRequest All(ReviewFilter filter) => new(filter, ReviewQueryService.ModeAllMatching);

    private static async Task<List<string>> ManualUnitsAsync(ReviewHarness h)
    {
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 100, CancellationToken.None)).Value!;
        return [.. page.Items.Select(i => i.UnitCode).Order()];
    }

    private static async Task<List<string>> ApprovableUnitsAsync(ReviewHarness h, ReviewFilter filter)
    {
        using var scope = h.NewScope();
        var resolved = await scope.Query.ResolveAsync(All(filter), CancellationToken.None);
        Assert.Null(resolved.Error);
        return [.. resolved.Records.Select(r => r.UnitCode).Order()];
    }

    private static string Key(int n) => ReviewQueryService.UnitKey(4, $"T{n:D4}", $"TP140-{n}");

    private static Task<CollectionsResult<ExceptionChangeResultDto>> IncludeAsync(ReviewHarness h, CollectionsCaller who, params string[] keys) => ChangeAsync(h, who, true, keys);

    private static async Task<CollectionsResult<ExceptionChangeResultDto>> ChangeAsync(ReviewHarness h, CollectionsCaller who, bool include, params string[] keys)
    {
        using var scope = h.NewScope();
        var request = new ExceptionChangeRequest("CurrentMonth", keys);
        return include ? await scope.Exceptions.IncludeAsync(who, request, CancellationToken.None) : await scope.Exceptions.RemoveAsync(who, request, CancellationToken.None);
    }

    // ------------------------------------------------------------ the policy itself

    [Theory]
    [InlineData(13, true)]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(1, true)]
    [InlineData(31, true)]
    public void Gulf_IsExcludedEveryDayInManualExceptTheFourteenth(int day, bool excluded)
    {
        var decision = new CollectionsExceptionPolicy(Configured()).Evaluate(Gulf, new DateOnly(2026, 10, day), manual: true, manuallyIncluded: false);
        Assert.Equal(excluded, decision.Excluded);
        Assert.Equal(excluded ? ReviewReasons.GulfCustomerExcluded : null, decision.ReasonCode);
        Assert.Equal(excluded ? null : ReviewReasons.GulfInclusionDay, decision.Notice);
    }

    [Theory]
    [InlineData("2026-01-14", false)] [InlineData("2026-01-31", true)] [InlineData("2026-02-01", true)] [InlineData("2026-02-14", false)] [InlineData("2026-02-28", true)]
    [InlineData("2026-03-01", true)] [InlineData("2026-03-14", false)] [InlineData("2026-04-30", true)] [InlineData("2026-12-14", false)] [InlineData("2026-12-31", true)]
    [InlineData("2027-01-01", true)] [InlineData("2028-02-29", true)] [InlineData("2028-02-14", false)]
    public void TheWindowFollowsTheCalendarDay_AcrossMonthAndYearBoundaries(string date, bool excluded) =>
        Assert.Equal(excluded, new CollectionsExceptionPolicy(Configured()).Evaluate(Gulf, DateOnly.Parse(date), true, false).Excluded);

    [Fact]
    public void TheFourteenth_IsNeverAnAutomaticSend_AutoStillExcludesGulf_OnEveryDay()
    {
        var policy = new CollectionsExceptionPolicy(Configured());
        for (var day = 1; day <= 28; day++)
        {
            Assert.True(policy.Evaluate(Gulf, new DateOnly(2026, 10, day), manual: false, manuallyIncluded: false).Excluded);
            Assert.True(policy.Evaluate(Gulf, new DateOnly(2026, 10, day), manual: false, manuallyIncluded: true).Excluded);   // a manual inclusion never applies to Auto
        }
        Assert.True(policy.IsExcludedFromAuto(Gulf)); Assert.True(policy.IsExcludedFromAuto(Special)); Assert.False(policy.IsExcludedFromAuto("TP140-3"));
    }

    [Theory]
    [InlineData("2026-10-13T19:59:00Z", false)]   // 23:59 on the 13th in Dubai
    [InlineData("2026-10-13T20:00:00Z", true)]    // midnight: the 14th begins in Dubai (UTC is still the 13th)
    [InlineData("2026-10-14T19:59:00Z", true)]    // 23:59 on the 14th in Dubai
    [InlineData("2026-10-14T20:00:00Z", false)]   // midnight: the 15th in Dubai (UTC is still the 14th)
    public void TheDay_IsTheDubaiDay_NotTheUtcDay(string utc, bool eligible)
    {
        var time = new FakeTimeProvider(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        var today = new CollectionsClock(new CollectionsOptions(), time).BusinessDate;
        Assert.Equal(eligible, !new CollectionsExceptionPolicy(Configured()).Evaluate(Gulf, today, true, false).Excluded);
    }

    [Fact]
    public void AUnitInBothLists_StaysPermanentlyExcluded_OnTheFourteenthAndEvenWhenIncluded()
    {
        var options = Configured(); options.GulfCustomerUnitCodes = [Special, Gulf];
        var policy = new CollectionsExceptionPolicy(options);
        foreach (var included in new[] { false, true })
        {
            var decision = policy.Evaluate(Special, new DateOnly(2026, 10, 14), manual: true, included);
            Assert.True(decision.Excluded); Assert.Equal(ReviewReasons.SpecialCaseExcluded, decision.ReasonCode);
        }
        Assert.True(policy.Evaluate(Special, new DateOnly(2026, 10, 14), manual: false, false).Excluded);
    }

    [Fact]
    public void Matching_IsExactAfterNormalization_NeverPartial()
    {
        var policy = new CollectionsExceptionPolicy(Configured());
        Assert.True(policy.IsGulfCustomer("TP140-2")); Assert.True(policy.IsGulfCustomer("  tp140-2 ")); Assert.True(policy.IsSpecialCase("tp140-1"));
        foreach (var other in new[] { "TP140-20", "TP140-", "TP140", "P140-2", "TP140-2A", "TP140 -2", "", null }) Assert.False(policy.IsGulfCustomer(other));
        Assert.Equal("TP140-2", UnitCodeNormalizer.Normalize(" tp140-2\t"));
    }

    [Fact]
    public void TheGulfList_IsNotConsulted_UntilItIsSwitchedOn_WhileTheSpecialListFollowsItsCodes()
    {
        var off = new CollectionsExceptionPolicy(new PactReceivablesOptions { SpecialCaseUnitCodes = [Special], GulfCustomerUnitCodes = [Gulf] });   // Gulf list present, but not switched on
        Assert.False(off.Evaluate(Gulf, new DateOnly(2026, 10, 15), true, false).Excluded);
        Assert.True(off.Evaluate(Special, new DateOnly(2026, 10, 15), true, false).Excluded);                 // the Campaigns pipeline's special-case list is active whenever it has codes
        Assert.False(new CollectionsExceptionPolicy(new PactReceivablesOptions()).HasExclusions);
    }

    [Fact]
    public void TheGulfList_IsConfiguration_NotCode_AndTheShippedSettingsKeepAutoSendingOff()
    {
        var root = AppContext.BaseDirectory; while (root is not null && !Directory.Exists(Path.Combine(root, "src", "TigerCS.Api"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        foreach (var file in Directory.GetFiles(Path.Combine(root!, "src", "TigerCS.Api"), "appsettings.json"))
        {
            var text = File.ReadAllText(file);
            Assert.Contains("\"GulfCustomerUnitCodes\"", text); Assert.Contains("\"GulfCustomersInclusionDay\": 14", text);
            Assert.DoesNotMatch("\"AutoSendingEnabled\"\\s*:\\s*true", text);
        }
        Assert.False(new CollectionsCampaignOptions().AutoSendingEnabled);
        Assert.Equal(14, new PactReceivablesOptions().GulfCustomersInclusionDay);
    }

    // ------------------------------------------------------------ the stored review records

    [Fact]
    public void TheBuilder_FlagsOnlyTheListedUnits_KeepsEveryFigure_AndNeverMergesUnitsOfOneCustomer()
    {
        var context = new ReviewBuildContext(new DateOnly(2026, 10, 14), ReviewHarness.Now, "PACT test", "AED", true, true, false, MoneyNormalizer.DefaultFloatTolerance,
            new Dictionary<string, string>(), Exceptions: new CollectionsExceptionPolicy(Configured()));
        var plain = ReviewRecordBuilder.Build(Rows(), context with { Exceptions = null }).Records;
        var records = ReviewRecordBuilder.Build(Rows(), context).Records;

        Assert.Equal(plain.Count, records.Count);                                              // nothing is dropped: the units stay in the stored data
        Assert.Equal(plain.Select(r => (r.UnitCode, r.RemainingAmount, r.RawRemainingAmount, r.InstalmentsJson)), records.Select(r => (r.UnitCode, r.RemainingAmount, r.RawRemainingAmount, r.InstalmentsJson)));
        var current = records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToDictionary(r => r.UnitCode);
        Assert.Contains(ReviewReasons.SpecialCaseExcluded, ReviewRecordBuilder.Parse(current["TP140-1"].Reasons)); Assert.Equal(ReviewValidationStatus.Excluded, current["TP140-1"].ValidationStatus);
        Assert.Contains(ReviewReasons.GulfCustomerExcluded, ReviewRecordBuilder.Parse(current["TP140-2"].Reasons));
        Assert.DoesNotContain(ReviewReasons.GulfCustomerExcluded, ReviewRecordBuilder.Parse(current["TP140-5"].Reasons));       // the same customer's other unit
        Assert.Equal(ReviewValidationStatus.Ready, current["TP140-5"].ValidationStatus);
        Assert.Equal(current["TP140-3"].Phone, current["TP140-6"].Phone);                                                       // one phone, two units: two records, nothing merged
        Assert.Equal(ReviewValidationStatus.Ready, current["TP140-6"].ValidationStatus);
        Assert.Equal(ReviewValidationStatus.Ready, current["TP140-3"].ValidationStatus);
    }

    [Fact]
    public void AUnitInBothLists_CarriesBothReasons_InTheStoredRecord()
    {
        var options = Configured(); options.GulfCustomerUnitCodes = [Special];
        var context = new ReviewBuildContext(new DateOnly(2026, 10, 14), ReviewHarness.Now, "PACT test", "AED", true, true, false, MoneyNormalizer.DefaultFloatTolerance,
            new Dictionary<string, string>(), Exceptions: new CollectionsExceptionPolicy(options));
        var record = ReviewRecordBuilder.Build(Rows(), context).Records.First(r => r.UnitCode == Special && r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Equal([ReviewReasons.SpecialCaseExcluded, ReviewReasons.GulfCustomerExcluded], ReviewRecordBuilder.Parse(record.Reasons).Where(c => c is ReviewReasons.SpecialCaseExcluded or ReviewReasons.GulfCustomerExcluded));
    }

    // ------------------------------------------------------------ Manual Preview / approval on day 13, 14, 15

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    public async Task OnDay13And15_ManualPreviewAndApproval_ExcludeGulfAndSpecialUnits_ButKeepTheirOtherUnits(int day)
    {
        using var h = await SeededAsync(DubaiNoon(10, day));
        Assert.Equal(["TP140-3", "TP140-5", "TP140-6"], await ManualUnitsAsync(h));                                  // 2 and 4 (Gulf) and 1 (special) are not listed; 5 is the same customer's other unit
        Assert.Equal(["TP140-3", "TP140-5", "TP140-6"], await ApprovableUnitsAsync(h, Manual()));
    }

    [Fact]
    public async Task OnDay14_ManualIncludesGulfUnits_ButNeverTheSpecialCase_AndAutoStillExcludesGulf()
    {
        using var h = await SeededAsync(DubaiNoon(10, 14));
        Assert.Equal(["TP140-2", "TP140-3", "TP140-4", "TP140-5", "TP140-6"], await ManualUnitsAsync(h));
        Assert.Equal(["TP140-2", "TP140-3", "TP140-4", "TP140-5", "TP140-6"], await ApprovableUnitsAsync(h, Manual()));
        Assert.Equal(["TP140-3", "TP140-5", "TP140-6"], await ApprovableUnitsAsync(h, Auto()));                       // the 14th is an eligibility window for Manual, never an automatic send
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 100, CancellationToken.None)).Value!;
        Assert.All(page.Items.Where(i => i.UnitCode is Gulf or OtherGulf), i => { Assert.Equal("Ready", i.ValidationStatus); Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.GulfInclusionDay); });
    }

    [Fact]
    public async Task OnDay14_AGulfUnitStillNeedsEveryOtherCheck()
    {
        using var h = await SeededAsync(DubaiNoon(10, 14), configure: x => x.Source.Items.Add(ReviewHarness.Row(7, 700m) with { Mobile = "" }), configurePolicies: false);
        h.Sql.GulfCustomersConfigured = true; h.Sql.GulfCustomerUnitCodes = ["TP140-7"];
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 100, CancellationToken.None)).Value!;
        var row = page.Items.Single(i => i.UnitCode == "TP140-7");
        Assert.Equal("NeedsReview", row.ValidationStatus); Assert.Contains(row.Reasons, r => r.Code == ReviewReasons.NoValidContact);   // no contact: not sendable, the date does not waive it
        Assert.DoesNotContain("TP140-7", await ApprovableUnitsAsync(h, Manual()));
    }

    [Fact]
    public async Task SpecialCases_AreExcludedOnEveryDay_InBothModes()
    {
        foreach (var day in new[] { 1, 13, 14, 15, 30 })
        {
            using var h = await SeededAsync(DubaiNoon(10, day));
            Assert.DoesNotContain(Special, await ApprovableUnitsAsync(h, Manual()));
            Assert.DoesNotContain(Special, await ApprovableUnitsAsync(h, Auto()));
            Assert.DoesNotContain(Special, await ManualUnitsAsync(h));
        }
    }

    [Fact]
    public async Task WithTheFlagsOff_TheCampaignsBehaveAsBefore()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15), configurePolicies: false);
        Assert.Equal(["TP140-1", "TP140-2", "TP140-3", "TP140-4", "TP140-5", "TP140-6"], await ManualUnitsAsync(h));
    }

    // ------------------------------------------------------------ manual inclusion

    [Fact]
    public async Task ManualInclusion_AddsOnlyTheSelectedUnit_ToThatManualCampaign_AndCanBeRemoved()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15));
        var added = await IncludeAsync(h, h.Manager, Key(2));
        Assert.True(added.IsSuccess); Assert.Equal(1, added.Value!.Changed); Assert.Empty(added.Value.Rejected);
        Assert.Equal("Manual:2026-10-15:CurrentMonth", added.Value.CampaignKey);

        Assert.Equal(["TP140-2", "TP140-3", "TP140-5", "TP140-6"], await ManualUnitsAsync(h));                         // TP140-4 (also Gulf) was not selected: it stays out
        Assert.Equal(["TP140-2", "TP140-3", "TP140-5", "TP140-6"], await ApprovableUnitsAsync(h, Manual()));
        using (var scope = h.NewScope())
        {
            var row = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 100, CancellationToken.None)).Value!.Items.Single(i => i.UnitCode == Gulf);
            Assert.Equal("Ready", row.ValidationStatus); Assert.Contains(row.Reasons, r => r.Code == ReviewReasons.ManuallyIncluded);   // the indicator the page shows
            Assert.Contains(row.Reasons, r => r.Code == ReviewReasons.ManuallyIncluded && r.Kind == "Warning");
        }
        Assert.DoesNotContain(Gulf, await ApprovableUnitsAsync(h, Auto()));                                    // Auto never takes a manual inclusion

        var removed = await ChangeAsync(h, h.Manager, include: false, Key(2));
        Assert.Equal(1, removed.Value!.Changed);
        Assert.Equal(["TP140-3", "TP140-5", "TP140-6"], await ManualUnitsAsync(h));
        Assert.Equal(["TP140-3", "TP140-5", "TP140-6"], await ApprovableUnitsAsync(h, Manual()));
    }

    [Fact]
    public async Task ManualInclusion_IsPerCampaign_NotPermanent_AndNotForAnotherDayOrType()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15));
        await IncludeAsync(h, h.Manager, Key(2));
        using (var scope = h.NewScope())   // another campaign type has its own key
        {
            var other = await scope.Exceptions.ListAsync(h.Manager, new ExceptionListRequest("FollowUp"), CancellationToken.None);
            Assert.Equal("Manual:2026-10-15:FollowUp", other.Value!.CampaignKey); Assert.Equal(0, other.Value.IncludedCount);
        }
        h.Time.Set(DubaiNoon(10, 16)); h.Source.ReadAt = DubaiNoon(10, 16); await h.RefreshAsync();           // the next day: a new campaign
        Assert.Equal(["TP140-3", "TP140-5", "TP140-6"], await ManualUnitsAsync(h));
    }

    [Fact]
    public async Task IncludingTwice_IsIdempotent_OneActiveRow_AndRemovalKeepsTheHistory()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15));
        Assert.Equal(1, (await IncludeAsync(h, h.Manager, Key(2))).Value!.Changed);
        var again = await IncludeAsync(h, h.Manager, Key(2));
        Assert.Equal(0, again.Value!.Changed); Assert.Equal(1, again.Value.Unchanged);
        await ChangeAsync(h, h.Manager, false, Key(2));
        Assert.Equal(1, (await IncludeAsync(h, h.Manager, Key(2))).Value!.Changed);                          // can be included again after a removal
        using var check = h.CreateContext();
        var rows = await check.CollectionsExceptionInclusions.AsNoTracking().OrderBy(r => r.CollectionsExceptionInclusionId).ToListAsync();
        Assert.Equal(2, rows.Count); Assert.NotNull(rows[0].RemovedAtUtc); Assert.Null(rows[1].RemovedAtUtc);
    }

    [Fact]
    public async Task OnlyAnAuthorizedUser_CanIncludeRemoveOrList()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15));
        foreach (var caller in new[] { h.Agent, h.Reporting })
        {
            Assert.Equal(CollectionsOutcome.Forbidden, (await IncludeAsync(h, caller, Key(2))).Outcome);
            Assert.Equal(CollectionsOutcome.Forbidden, (await ChangeAsync(h, caller, false, Key(2))).Outcome);
            using var scope = h.NewScope();
            Assert.Equal(CollectionsOutcome.Forbidden, (await scope.Exceptions.ListAsync(caller, new ExceptionListRequest("CurrentMonth"), CancellationToken.None)).Outcome);
        }
        using var check = h.CreateContext();
        Assert.Empty(await check.CollectionsExceptionInclusions.ToListAsync());
        Assert.Equal(["TP140-3", "TP140-5", "TP140-6"], await ManualUnitsAsync(h));
    }

    [Fact]
    public async Task ASpecialCase_ANonGulfUnit_AndAnUnknownOrTamperedKey_CanNeverBeIncluded()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15), x => { x.Sql.GulfCustomerUnitCodes = [Special, Gulf, OtherGulf]; });   // TP140-1 is in BOTH lists
        var result = await IncludeAsync(h, h.Manager, Key(1), Key(3), ReviewQueryService.UnitKey(4, "T9999", "TP140-2"), "4|T0002|TP140-99", "garbage", "4|T0002");
        Assert.True(result.IsSuccess); Assert.Equal(0, result.Value!.Changed); Assert.Equal(6, result.Value.Rejected.Count);
        Assert.Contains(result.Value.Rejected, r => r.UnitKey == Key(1) && r.Reason.Contains("special case"));
        Assert.Contains(result.Value.Rejected, r => r.UnitKey == Key(3) && r.Reason.Contains("GULF_CUSTOMERS"));
        using var check = h.CreateContext();
        Assert.Empty(await check.CollectionsExceptionInclusions.ToListAsync());
        Assert.DoesNotContain(Special, await ApprovableUnitsAsync(h, Manual()));
    }

    [Fact]
    public async Task APermanentSpecialCase_IsNotApprovable_EvenIfARowForItWasWrittenDirectlyIntoTheDatabase()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15), x => { x.Sql.GulfCustomerUnitCodes = [Special, Gulf]; });
        using (var context = h.CreateContext())
        {
            context.CollectionsExceptionInclusions.Add(new() { CampaignKey = "Manual:2026-10-15:CurrentMonth", CompanyId = 4, TenantId = "T0001", UnitCode = Special, IncludedByEmployeeId = h.Manager.EmployeeId, IncludedAtUtc = DubaiNoon(10, 15) });
            await context.SaveChangesAsync();
        }
        Assert.DoesNotContain(Special, await ManualUnitsAsync(h));
        Assert.DoesNotContain(Special, await ApprovableUnitsAsync(h, Manual()));
    }

    [Fact]
    public async Task ATickedKeyThatWasNotIncluded_CannotBeSmuggledIntoAnApproval()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15));
        await IncludeAsync(h, h.Manager, Key(2));
        using var scope = h.NewScope();
        var tampered = new SelectionRequest(Manual(), ReviewQueryService.ModeSelectedUnits, UnitKeys: [Key(2), Key(4), Key(1), Key(3)]);   // 4 is Gulf and not included, 1 is special
        var resolved = await scope.Query.ResolveAsync(tampered, CancellationToken.None);
        Assert.Equal(["TP140-2", "TP140-3"], resolved.Records.Select(r => r.UnitCode).Order());
        Assert.Equal(2, resolved.Skipped);
        var auto = await scope.Query.ResolveAsync(tampered with { Filter = Auto() }, CancellationToken.None);
        Assert.DoesNotContain(auto.Records, r => r.UnitCode is Gulf or OtherGulf or Special);
    }

    [Fact]
    public async Task EachChange_IsAudited_WithWhoWhenAndWhichCampaign_AndNoCustomerData()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15));
        await IncludeAsync(h, h.Manager, Key(2));
        await ChangeAsync(h, h.Manager, false, Key(2));
        using var check = h.CreateContext();
        var inclusion = await check.CollectionsExceptionInclusions.AsNoTracking().SingleAsync();
        Assert.Equal(h.Manager.EmployeeId, inclusion.IncludedByEmployeeId); Assert.Equal(DubaiNoon(10, 15), inclusion.IncludedAtUtc);
        Assert.Equal("Manual:2026-10-15:CurrentMonth", inclusion.CampaignKey); Assert.Equal(h.Manager.EmployeeId, inclusion.RemovedByEmployeeId); Assert.NotNull(inclusion.RemovedAtUtc);
        Assert.Equal(Gulf, inclusion.UnitCode);
        var audit = await check.AuditEntries.AsNoTracking().Where(a => a.EntityType == ExceptionInclusionService.AuditEntityType).OrderBy(a => a.AuditEntryId).ToListAsync();
        Assert.Equal([ExceptionInclusionService.AddedAction, ExceptionInclusionService.RemovedAction], audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.Equal(h.Manager.EmployeeId, a.ActorEmployeeId));
        var details = audit[0].AfterValue!;
        Assert.Contains("Manual:2026-10-15:CurrentMonth", details); Assert.Contains(Gulf, details);
        Assert.DoesNotContain("Customer 2", details); Assert.DoesNotContain("97150", details);
    }

    [Fact]
    public async Task TheManageList_ShowsGulfUnits_SearchableByUnitTowerAndCustomer_WithTheirState()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15), x => x.Sql.GulfCustomerUnitCodes = [Special, Gulf, OtherGulf]);
        await IncludeAsync(h, h.Manager, Key(2));
        async Task<ExceptionListDto> List(string? search = null)
        {
            using var scope = h.NewScope();
            return (await scope.Exceptions.ListAsync(h.Manager, new ExceptionListRequest("CurrentMonth", search), CancellationToken.None)).Value!;
        }
        var all = await List();
        Assert.Equal(["TP140-2", "TP140-4"], all.Items.Select(i => i.UnitCode)); Assert.Equal(2, all.TotalCount); Assert.Equal(1, all.IncludedCount);   // a special case has no campaign record at all, so it is never offered
        Assert.True(all.Items.Single(i => i.UnitCode == Gulf).ManuallyIncluded); Assert.False(all.Items.Single(i => i.UnitCode == OtherGulf).ManuallyIncluded);
        Assert.False(all.InclusionDay);
        Assert.Equal([Gulf], (await List("TP140-2")).Items.Select(i => i.UnitCode));                         // unit code
        Assert.Equal([OtherGulf], (await List("Customer 4")).Items.Select(i => i.UnitCode));                 // customer
        Assert.Empty((await List("no such customer")).Items);
        Assert.All(all.Items, i => Assert.DoesNotContain("Customer 3", i.CustomerName));                     // only listed units
    }

    // ------------------------------------------------------------ Export

    [Theory]
    [InlineData(13, false, false)]
    [InlineData(14, true, true)]
    [InlineData(15, false, false)]
    [InlineData(15, true, false)]
    public async Task Export_FollowsTheSameRules_AndNeverContainsASpecialCase(int day, bool expectedGulf, bool _)
    {
        using var h = await SeededAsync(DubaiNoon(10, day), x => { });
        if (day == 15 && expectedGulf) await IncludeAsync(h, h.Manager, Key(2));
        using var scope = h.NewScope();
        var export = await scope.Export.ExportAsync(h.Manager, All(Manual()), CancellationToken.None);
        Assert.True(export.IsSuccess, export.Detail);
        var csv = export.Value!.Csv;
        Assert.Contains("+971500000003", csv);                                                               // an ordinary unit is exported
        Assert.DoesNotContain("+971500000001", csv);                                                         // the special case never
        Assert.Equal(expectedGulf, csv.Contains("+971500000002", StringComparison.Ordinal));
        if (day == 15 && expectedGulf) Assert.DoesNotContain("+971500000004", csv);                          // only the selected Gulf unit
    }

    // ------------------------------------------------------------ revalidated right before the upload

    private static async Task<long> ApproveAsync(ReviewHarness h, string key = "k1", ReviewFilter? filter = null)
    {
        var (_, confirm) = await h.PrepareAsync(All(filter ?? Manual()), key);
        using var scope = h.NewScope();
        var result = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, result.Outcome);
        return h.Scheduler.Dispatches.Last();
    }

    private static async Task ExecuteAsync(ReviewHarness h, long id)
    {
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task AUnitApprovedOnTheFourteenth_IsNotUploadedAfterMidnight_WhenTheWindowHasClosed()
    {
        using var h = await SeededAsync(DubaiNoon(10, 14));
        var id = await ApproveAsync(h);
        h.Time.Set(new DateTime(2026, 10, 14, 20, 30, 0, DateTimeKind.Utc));                                  // 00:30 on the 15th in Dubai (still the 14th in UTC)

        await ExecuteAsync(h, id);

        var sent = Assert.Single(h.Client.Calls).Contacts;
        Assert.DoesNotContain(sent, c => c.Phone is "+971500000002" or "+971500000004");                       // the Gulf units are not uploaded
        Assert.Contains(sent, c => c.Phone == "+971500000003");                                                // the others are
        using var check = h.CreateContext();
        var excluded = await check.CollectionsDispatchItems.AsNoTracking().Where(i => i.Status == DispatchItemStatus.Excluded).OrderBy(i => i.UnitCode).ToListAsync();
        Assert.Equal([Gulf, OtherGulf], excluded.Select(i => i.UnitCode));
        Assert.All(excluded, i => Assert.Contains("GULF_CUSTOMERS", i.StatusReason));
    }

    [Fact]
    public async Task AManualInclusionRemovedAfterApproval_IsNotUploaded()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15));
        await IncludeAsync(h, h.Manager, Key(2));
        var id = await ApproveAsync(h);
        await ChangeAsync(h, h.Manager, false, Key(2));                                                        // withdrawn before the upload

        await ExecuteAsync(h, id);

        var sent = Assert.Single(h.Client.Calls).Contacts;
        Assert.DoesNotContain(sent, c => c.Phone == "+971500000002");
        Assert.Contains(sent, c => c.Phone == "+971500000003");
    }

    [Fact]
    public async Task AManuallyIncludedUnit_IsUploaded_WhenTheInclusionStands()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15));
        await IncludeAsync(h, h.Manager, Key(2));
        var id = await ApproveAsync(h);
        await ExecuteAsync(h, id);
        var sent = Assert.Single(h.Client.Calls).Contacts;
        Assert.Contains(sent, c => c.Phone == "+971500000002");
        Assert.DoesNotContain(sent, c => c.Phone is "+971500000004" or "+971500000001");
    }

    [Fact]
    public async Task ASpecialCaseAddedAfterApproval_IsNotUploaded_AndEveryRemainingSafetyCheckStillRuns()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15), configurePolicies: false);
        var id = await ApproveAsync(h);                                                                       // approved while nothing was excluded
        h.Sql.SpecialCasesConfigured = true; h.Sql.SpecialCaseUnitCodes = [Special];                           // the business adds a special case afterwards

        await ExecuteAsync(h, id);

        var sent = Assert.Single(h.Client.Calls).Contacts;
        Assert.DoesNotContain(sent, c => c.Phone == "+971500000001");
        Assert.Equal(5, sent.Count);
        using var check = h.CreateContext();
        Assert.Contains("Special case", (await check.CollectionsDispatchItems.AsNoTracking().SingleAsync(i => i.UnitCode == Special)).StatusReason);
    }

    // ------------------------------------------------------------ Auto dispatcher and Auto preview

    [Fact]
    public async Task TheAutoDispatcher_NeverDispatchesGulfOrSpecialUnits_EvenOnTheFourteenth_AndStaysOffByDefault()
    {
        using var h = await SeededAsync(DubaiNoon(10, 14), x => x.Campaign.AutoSendingEnabled = true);
        using (var scope = h.NewScope()) Assert.True((await scope.Auto.RunAsync(CancellationToken.None)).Ran);
        await ExecuteAsync(h, h.Scheduler.Dispatches.Single());                                               // the queued dispatch runs its revalidation and upload
        var sent = Assert.Single(h.Client.Calls).Contacts;
        Assert.Equal(["+971500000003", "+971500000003", "+971500000005"], sent.Select(c => c.Phone).Order());   // units 3, 6 (the same phone, two contacts: nothing is merged) and 5
        Assert.False(new CollectionsCampaignOptions().AutoSendingEnabled);
    }

    [Fact]
    public async Task AutoPreview_DropsTheUnitsFromTheCampaign_OnTheFourteenthToo_WithoutChangingTheReceivables()
    {
        using var h = await SeededAsync(DubaiNoon(10, 14));
        var campaigns = new CollectionsCampaignAppService(h.Options, h.Campaign, h.Sql, new(h.Options, new TigerCS.Tests.IdentityAndAccess.Fakes.FakeDepartmentRepository()),
            new(h.Options, h.Time), h.Source, Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionsCampaignAppService>.Instance, exceptions: new CollectionsExceptionPolicy(h.Sql));
        var preview = (await campaigns.PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 0m, paymentStatus: "Both")).Value!;
        Assert.Equal(["TP140-3", "TP140-5", "TP140-6"], preview.Items.Select(i => i.UnitCode).Order());
        Assert.Equal(["TP140-1", "TP140-2", "TP140-3", "TP140-4", "TP140-5", "TP140-6"], h.Source.Items.Select(r => r.UnitCode).Distinct().Order());   // the source rows (Receivables) are untouched
        var without = new CollectionsCampaignAppService(h.Options, h.Campaign, new PactReceivablesOptions { Enabled = true }, new(h.Options, new TigerCS.Tests.IdentityAndAccess.Fakes.FakeDepartmentRepository()),
            new(h.Options, h.Time), h.Source, Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionsCampaignAppService>.Instance);
        Assert.Equal(6, (await without.PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 0m, paymentStatus: "Both")).Value!.Items.Count);
    }

    // ------------------------------------------------------------ logging

    [Fact]
    public async Task EachExcludedUnit_IsLoggedWithItsReason_AndNoCustomerInformation()
    {
        using var h = await SeededAsync(DubaiNoon(10, 15));
        using (var scope = h.NewScope()) await scope.Query.ResolveAsync(All(Manual()), CancellationToken.None);
        var lines = h.QueryLog.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToList();
        Assert.Contains(lines, l => l.Contains(Gulf) && l.Contains(ReviewReasons.GulfCustomerExcluded));
        Assert.Contains(lines, l => l.Contains(OtherGulf) && l.Contains(ReviewReasons.GulfCustomerExcluded));
        Assert.All(h.QueryLog.Messages, m => { Assert.DoesNotContain("Customer ", m); Assert.DoesNotContain("97150", m); Assert.DoesNotContain("@example.test", m); });
    }
}
