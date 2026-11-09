using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;
using Xunit.Abstractions;

namespace TigerCS.Tests.Collections.Review;

/// <summary>Templates, real-export regressions, the live-dispatch gate, voice eligibility, paid-after-upload suppression, job races and leases.</summary>
public sealed class ReviewUatReadinessTests(ITestOutputHelper output)
{
    private const string CurrentMonthList = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1";

    private static string RepoDir(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine([dir.FullName, "docs", "Collections", .. parts]);
            if (File.Exists(path) || Directory.Exists(path)) return path;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }

    private static async Task<ReviewHarness> SeededAsync(int count = 3, bool file = false, Action<ReviewHarness>? configure = null)
    {
        var h = new ReviewHarness(file);
        configure?.Invoke(h);
        for (var n = 1; n <= count; n++) h.Source.Items.Add(ReviewHarness.Row(n, 100m + n));
        await h.RefreshAsync();
        return h;
    }

    private static async Task<(DispatchDto Dispatch, long Id)> ApproveAsync(ReviewHarness h, string key = "k1")
    {
        var (_, confirm) = await h.PrepareAsync(key: key);
        using var scope = h.NewScope();
        var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome);
        return (accepted.Value!, h.Scheduler.Dispatches.Last());
    }

    private static async Task<DispatchDto> UploadAsync(ReviewHarness h, string key = "k1")
    {
        var (dto, id) = await ApproveAsync(h, key);
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        return (await scope.Dispatch.GetAsync(h.Manager, dto.DispatchId, CancellationToken.None)).Value!;
    }

    private static async Task<List<CollectionsDispatchItem>> ItemsAsync(ReviewHarness h)
    {
        using var context = h.CreateContext();
        return await context.CollectionsDispatchItems.AsNoTracking().OrderBy(i => i.RecordKey).ToListAsync();
    }

    // ================================================================ 1. templates and exports

    [Fact]
    public void TheSuppliedTemplates_HaveExactlyTheSixColumns_AndEachFileNamesItsOwnConfiguredList()
    {
        var dir = RepoDir("genesys-templates");
        var options = new GenesysOutboundOptions();
        var files = Directory.GetFiles(dir, "TH_Collections_*.csv");
        Assert.Equal(5, files.Length);
        var seen = new HashSet<CampaignReminderType>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);               // TH_Collections_<Type>_<listId>
            var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            var listId = parts[^1];
            var type = Enum.Parse<CampaignReminderType>(parts[2]);
            Assert.Equal(options.ContactListIdFor(type), listId);             // file name list id == configured routing
            Assert.True(seen.Add(type));

            var result = GenesysContactTemplate.Verify(File.ReadAllText(file), GenesysContactTemplate.ExpectedReminderTypes(options));
            Assert.Equal(GenesysContactTemplate.Columns, result.Columns);     // exact names, exact order, "Email Address" with a space
            Assert.Empty(result.MissingColumns);
            Assert.Empty(result.MisnamedColumns);
            Assert.Empty(result.UnexpectedColumns);
        }
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void TheSuppliedTemplates_CarryNoReminderTypeValue_SoTheLabelsCannotBeVerifiedFromThem()
    {
        var options = new GenesysOutboundOptions();
        foreach (var file in Directory.GetFiles(RepoDir("genesys-templates"), "TH_Collections_*.csv"))
        {
            var result = GenesysContactTemplate.Verify(File.ReadAllText(file), GenesysContactTemplate.ExpectedReminderTypes(options));
            Assert.Equal(1, result.RowCount);                                   // a single placeholder row: 977272 and blanks
            Assert.Equal([""], result.ReminderTypeValues);                      // the only ReminderType value is blank
            // The placeholder row is not a customer and does not look like one; that is all the verifier reports.
            Assert.All(result.Issues, i => Assert.Equal(1, i.Row));
            Assert.Contains(result.Issues, i => i.Column == "Phone");
        }
        // Until a list holds real rows, the strings are a configuration decision: see ReminderTypeLabels.
        Assert.Equal(["Current Month", "Follow Up", "Overdue", "Legal Notice", "Legal Case"],
            new[] { CampaignReminderType.CurrentMonth, CampaignReminderType.FollowUp, CampaignReminderType.Overdue, CampaignReminderType.LegalNotice, CampaignReminderType.LegalCase }
                .Select(options.LabelFor));
    }

    [Fact]
    public void AGeneratedContactFile_VerifiesCleanAgainstTheTemplate_AndRoundTrips()
    {
        var options = new GenesysOutboundOptions();
        var contacts = new[]
        {
            new GenesysContactPayload(CurrentMonthList, "+971500000001", "Customer, \"One\"", "c1@example.test", "Current Month", "1234.50", "2026-10-20", true),
            new GenesysContactPayload(CurrentMonthList, "+966500000002", "Customer Two", "", "Current Month", "100.00", "2026-10-05", true)
        };
        var csv = GenesysContactTemplate.WriteCsv(contacts);
        Assert.StartsWith("\"Phone\",\"CustomerName\",\"Email Address\",\"ReminderType\",\"AmountDue\",\"DueDate\"\r\n", csv);
        var result = GenesysContactTemplate.Verify(csv, GenesysContactTemplate.ExpectedReminderTypes(options));
        Assert.True(result.IsMatch, string.Join("; ", result.Issues.Select(i => $"{i.Row}/{i.Column}: {i.Message}")));
        Assert.Equal(2, result.RowCount);
        Assert.Equal(["Current Month"], result.ReminderTypeValues);
        var parsed = GenesysContactTemplate.ParseCsv(csv);
        Assert.Equal("Customer, \"One\"", parsed[1][1]);                        // commas and quotes survive
    }

    [Fact]
    public void TheVerifierReportsEveryKindOfMismatch()
    {
        var options = new GenesysOutboundOptions();
        var bad = "Phone,CustomerName,EmailAddress,Reminder Type,Amount Due,DueDate,Extra\r\n"
                  + "0501234567,Name,e@example.test,CurrentMonth,\"1,234.5\",14/10/2026,x\r\n"
                  + "+971500000001,,not-an-email,Overdue,100.00,2026-10-14,x\r\n";
        var result = GenesysContactTemplate.Verify(bad, GenesysContactTemplate.ExpectedReminderTypes(options));
        Assert.False(result.IsMatch);
        Assert.Equal(["Email Address", "ReminderType", "AmountDue"], result.MissingColumns);
        Assert.Equal(3, result.MisnamedColumns.Count);                          // found, but spelled differently
        Assert.Contains("EmailAddress", string.Join(' ', result.MisnamedColumns));
        Assert.Equal(["Extra"], result.UnexpectedColumns);
        var text = result.Issues.Select(i => i.Message);
        Assert.Contains(text, m => m.Contains("0501234567"));                   // phone not E.164
        Assert.Contains(text, m => m.Contains("14/10/2026"));                   // date not yyyy-MM-dd

        // Values are only judged in columns that carry the exact template name.
        var amounts = "Phone,CustomerName,Email Address,ReminderType,AmountDue,DueDate\r\n+971500000001,A,,Overdue,\"1,234.5\",2026-10-14\r\n";
        Assert.Contains(GenesysContactTemplate.Verify(amounts, GenesysContactTemplate.ExpectedReminderTypes(options)).Issues, i => i.Column == "AmountDue" && i.Message.Contains("1,234.5"));

        var wrongLabel = "Phone,CustomerName,Email Address,ReminderType,AmountDue,DueDate\r\n+971500000001,A,,CurrentMonth,100.00,2026-10-14\r\n";
        var labels = GenesysContactTemplate.Verify(wrongLabel, GenesysContactTemplate.ExpectedReminderTypes(options));
        Assert.Equal(["CurrentMonth"], labels.UnknownReminderTypes);            // "CurrentMonth" is not the configured "Current Month"
        Assert.Equal(CollectionsOutcome.Success, CollectionsOutcome.Success);
    }

    [Fact]
    public void TheReviewExport_IsNotTheContactListTemplate_AndItsColumnsMapExplicitly()
    {
        // The internal review CSV (the 22 supplied columns + TowerNumber, TowerName; the Genesys file keeps the 22) vs the 6 contact-list columns.
        var header = "RecordId,CustomerKey,CompanyId,TenantId,CustomerName,Phone,Email,UnitId,UnitCode,ProjectCode,Amount,Currency,DueDate,Stage,CycleKey,ReadAtUtc,Status,Reason,Use,VoiceEligible,SmsEligible,EmailEligible,TowerNumber,TowerName".Split(',');
        var report = new CollectionsCampaignPreviewDto(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 14), DateTime.UtcNow, "PACT", "OverdueReminder", "2026-10:OverdueReminder",
            [new DateOnly(2026, 10, 14)], true, true, false, true, 0, 0, 0, 1, 25, []);
        Assert.Equal(header, CollectionsCampaignCsv.Write(report, review: true).Split("\r\n")[0].Split(','));
        Assert.All(GenesysContactTemplate.LegacyExportColumnMap.Keys, k => Assert.Contains(k, header));
        Assert.All(GenesysContactTemplate.LegacyExportColumnMap.Values, v => Assert.Contains(v, GenesysContactTemplate.Columns));
        Assert.Equal(GenesysContactTemplate.Columns.Count, GenesysContactTemplate.LegacyExportColumnMap.Values.Distinct().Count());
        // Renamed columns: Email -> "Email Address", Amount -> AmountDue, Stage -> ReminderType, and Stage values are not labels.
        var options = new GenesysOutboundOptions();
        Assert.Equal("Overdue", GenesysContactTemplate.LegacyStageToReminderType("OverdueReminder", options));
        Assert.Equal("Current Month", GenesysContactTemplate.LegacyStageToReminderType("CurrentMonthReminder", options));
        Assert.Equal("Follow Up", GenesysContactTemplate.LegacyStageToReminderType("FollowUpReminder", options));
        Assert.Equal("Legal Notice", GenesysContactTemplate.LegacyStageToReminderType("LegalNotice", options));
        Assert.Equal("Legal Case", GenesysContactTemplate.LegacyStageToReminderType("LegalReferral", options));
    }

    // ================================================================ real-export regressions (values observed, no customer data)

    [Theory]
    [InlineData("37797.0800000001", "37797.08")]
    [InlineData("20.9200000000419", "20.92")]
    [InlineData("44915.7600000000093", "44915.76")]
    [InlineData("67111.6699999999", "67111.67")]
    [InlineData("170.630000000005", "170.63")]
    [InlineData("632.979999999981", "632.98")]
    [InlineData("0.760000000009313", "0.76")]
    [InlineData("25795.000000000058207660913467", "25795.00")]
    public void FloatNoiseSeenInTheOverdueExport_IsNormalizedToFils(string raw, string expected)
    {
        var result = MoneyNormalizer.Normalize(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture), sourceIsFloatingPoint: true);
        Assert.True(result.IsResolved);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result.Value);
    }

    [Theory]
    [InlineData("0.397999999985586", "0.40")]          // 0.398 AED: a genuine three-decimal balance is rounded to fils (business decision), the original is kept
    [InlineData("291906.6510000000708", "291906.65")]  // 291,906.651 AED
    public void GenuineSubFilsBalancesSeenInTheExport_AreRoundedToFils_AndTheOriginalIsKept(string raw, string expected)
    {
        var original = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        var result = MoneyNormalizer.Normalize(original, sourceIsFloatingPoint: true);
        Assert.Equal(AmountPrecision.Rounded, result.Precision);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result.Value);
        Assert.Equal(original, result.Raw);
    }

    [Theory]
    [InlineData("0.0000000000727595761418342")]
    [InlineData("0.0000000000582076609134674")]
    [InlineData("0.000000000116415321826935")]
    [InlineData("3.27418092638254E-11")]
    public void SettledInstalmentsThatLookLikePositiveBalances_AreNotReceivables(string raw)
    {
        var row = ReviewHarness.Row(1) with { Amount = decimal.Parse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture), AmountIsFloatingPoint = true };
        var built = ReviewRecordBuilder.Build([row], new ReviewBuildContext(new DateOnly(2026, 10, 14), ReviewHarness.Now, "t", "AED", true, true, false, 0.0001m, new Dictionary<string, string>()));
        Assert.Empty(built.Records);
        Assert.Equal(1, built.SettledRows);
    }

    [Theory]
    [InlineData("+966500000002")]       // Saudi mobile seen in the export
    [InlineData("+79161234567")]        // Russian mobile
    [InlineData("971500000003")]
    public void InternationalNumbersSeenInTheExport_AreAcceptedAsE164(string raw) => Assert.True(PhoneNormalizer.Normalize(raw).IsValid);

    // ================================================================ 2. V2 deployment scripts

    [Theory]
    [InlineData(4, "10-deploy-p4AccountReceivablesV2.sql")]
    [InlineData(32, "11-deploy-p32AccountReceivablesV2.sql")]
    public void TheDeployScript_IsTheReviewedDraftBodyExactly(int company, string deployFile)
    {
        static string Body(string text, string marker) => text[text.IndexOf(marker, StringComparison.Ordinal)..].Replace("\r\n", "\n").TrimEnd();
        var review = File.ReadAllText(RepoDir("pact-sql", $"p{company}AccountReceivablesV2.review.sql"));
        var deploy = File.ReadAllText(RepoDir("pact-sql", "deploy", deployFile));
        var reviewBody = Body(review, "CREATE PROCEDURE [dbo]").Replace("CREATE PROCEDURE", "ALTER PROCEDURE");
        var deployBody = Body(deploy, "ALTER PROCEDURE [dbo]");
        Assert.EndsWith("\nGO", deployBody);
        Assert.Equal(reviewBody, deployBody[..^"\nGO".Length].TrimEnd());
        // Creates only the versioned procedure and never touches the original.
        Assert.DoesNotContain("DROP PROCEDURE", deploy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"ALTER PROCEDURE [dbo].[p{company}AccountReceivables]", deploy);
        Assert.Contains($"p{company}AccountReceivablesV2", deploy);
    }

    [Fact]
    public void TheDeploymentPackage_HasPreflightGrantsSmokeTestAndARollbackThatDropsOnlyV2()
    {
        var dir = RepoDir("pact-sql", "deploy");
        foreach (var f in new[] { "00-preflight.sql", "10-deploy-p4AccountReceivablesV2.sql", "11-deploy-p32AccountReceivablesV2.sql", "15-grant-execute.sql",
                     "20-smoke-and-compare-p4.sql", "21-smoke-and-compare-p32.sql", "90-rollback.sql", "README.md" })
            Assert.True(File.Exists(Path.Combine(dir, f)), f);
        var rollback = File.ReadAllText(Path.Combine(dir, "90-rollback.sql"));
        foreach (var line in rollback.Split('\n').Where(l => l.Contains("DROP PROCEDURE")))
            Assert.Contains("V2", line);
        Assert.DoesNotContain("DROP TABLE", rollback, StringComparison.OrdinalIgnoreCase);
        // Read-only scripts must not write.
        foreach (var f in new[] { "00-preflight.sql", "20-smoke-and-compare-p4.sql", "21-smoke-and-compare-p32.sql" })
        {
            var sql = File.ReadAllText(Path.Combine(dir, f));
            Assert.DoesNotMatch(@"(?im)^\s*(UPDATE|DELETE|DROP|TRUNCATE|ALTER|GRANT)\s", sql);
        }
    }

    [Fact]
    public void TheApplicationReadsOriginalPaidAndRemaining_FromTheDocumentedV2Columns()
    {
        // The V2 draft returns PlanAmount (original), AllocatedAmount (paid) and Amount (remaining) with Status Paid/Installment.
        var v2 = File.ReadAllText(RepoDir("pact-sql", "p4AccountReceivablesV2.review.sql"));
        Assert.Contains("al.PlanAmount AS PlanAmount", v2);
        Assert.Contains("al.AllocatedAmount AS AllocatedAmount", v2);
        Assert.Contains("CONVERT(decimal(19,4), al.PlanAmount - al.AllocatedAmount) AS Amount", v2);
        Assert.Contains("THEN 'Paid' ELSE 'Installment'", v2);
        Assert.Matches(@"@StartDate datetime,\s+@EndDate datetime,\s+@MinAmount decimal\(19, 4\) = 0,\s+@IncludeSettled bit = 0,\s+@StrictIdentity bit = 1", v2);   // the only parameters; none selects a customer
        var reader = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TigerCS.Infrastructure", "Modules", "Collections", "PactSqlReceivablesSource.cs"));
        Assert.Contains("\"AllocatedAmount\"", reader);
        Assert.Contains("\"PlanAmount\"", reader);
    }

    // ================================================================ 4. the live gate and callable

    [Fact]
    public void LiveCustomerDispatchSuppressionAndTheIntegrationAreAllOffByDefault()
    {
        var defaults = new GenesysOutboundOptions();
        Assert.False(defaults.Enabled);
        Assert.False(defaults.LiveCustomerDispatchEnabled);
        Assert.False(defaults.SuppressionEnabled);
        var committed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TigerCS.Api", "appsettings.json"));
        using var json = JsonDocument.Parse(committed);
        var section = json.RootElement.GetProperty("Collections").GetProperty("GenesysOutbound");
        Assert.False(section.GetProperty("Enabled").GetBoolean());
        Assert.False(section.GetProperty("LiveCustomerDispatchEnabled").GetBoolean());
        Assert.False(section.GetProperty("SuppressionEnabled").GetBoolean());
        Assert.Equal("", section.GetProperty("ClientId").GetString());
        Assert.Equal("", section.GetProperty("ClientSecret").GetString());
    }

    [Fact]
    public async Task ConfirmationIsRefusedUnlessLiveDispatchAndSuppressionAreBothEnabled()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync();
        using var scope = h.NewScope();
        h.Genesys.LiveCustomerDispatchEnabled = false;
        var off = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Disabled, off.Outcome);
        Assert.Contains("switched off", off.Detail);
        h.Genesys.LiveCustomerDispatchEnabled = true;
        h.Genesys.SuppressionEnabled = false;
        var noSuppression = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Disabled, noSuppression.Outcome);
        Assert.Contains("suppression", noSuppression.Detail);
        Assert.Empty(await ItemsAsync(h));
        Assert.Empty(h.Client.Calls);
    }

    [Fact]
    public async Task AnApprovedDispatchIsNotSentIfLiveDispatchIsSwitchedOffBeforeTheJobRuns()
    {
        using var h = await SeededAsync();
        var (_, id) = await ApproveAsync(h);
        h.Genesys.LiveCustomerDispatchEnabled = false;
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        Assert.Empty(h.Client.Calls);
        Assert.Equal(DispatchStatus.Failed, (await scope.Store.GetDispatchByIdAsync(id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task CallableIsTrueOnlyForApprovedVoiceEligibleContacts()
    {
        using var h = await SeededAsync(3);
        await UploadAsync(h);
        var sent = Assert.Single(h.Client.Calls).Contacts;
        Assert.Equal(3, sent.Count);
        Assert.All(sent, c => { Assert.True(c.Callable); Assert.Matches(@"^\+[1-9]\d{7,14}$", c.Phone); });
        Assert.All(await ItemsAsync(h), i => Assert.True(i.VoiceEligible));

        var item = new CollectionsDispatchItem { Phone = "+971500000001", VoiceEligible = true, Amount = 1m, DueDate = new DateOnly(2026, 10, 1), ReminderType = CampaignReminderType.Overdue };
        var options = new GenesysOutboundOptions();
        Assert.True(DispatchService.ToPayload(item, CurrentMonthList, options).Callable);
        item.VoiceEligible = false;                                              // not approved as voice eligible
        Assert.False(DispatchService.ToPayload(item, CurrentMonthList, options).Callable);
        item.VoiceEligible = true; item.Phone = "12345";                          // approved, but no valid international number
        Assert.False(DispatchService.ToPayload(item, CurrentMonthList, options).Callable);
        item.Phone = "";
        Assert.False(DispatchService.ToPayload(item, CurrentMonthList, options).Callable);
    }

    [Fact]
    public async Task ARecordThatIsNoLongerVoiceEligibleAtSendTimeIsNeverUploaded()
    {
        using var h = await SeededAsync(3);
        var (_, id) = await ApproveAsync(h);
        using (var context = h.CreateContext())
        {
            var one = await context.CollectionsDispatchItems.OrderBy(i => i.RecordKey).FirstAsync();
            one.VoiceEligible = false;
            await context.SaveChangesAsync();
        }
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        Assert.Equal(2, Assert.Single(h.Client.Calls).Contacts.Count);
        var excluded = Assert.Single(await ItemsAsync(h), i => i.Status == DispatchItemStatus.Excluded);
        Assert.Contains("Not voice eligible", excluded.StatusReason);
        Assert.Null(excluded.GenesysContactId);
    }

    [Fact]
    public async Task AListWithAnUncallablePhoneCannotBeConfirmed()
    {
        using var h = await SeededAsync(2);
        var (_, confirm) = await h.PrepareAsync();
        using (var context = h.CreateContext())
        {
            var record = await context.CollectionsReviewRecords.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).OrderBy(r => r.RecordKey).FirstAsync();
            record.Phone = "";                                                  // tampered after the summary was shown
            await context.SaveChangesAsync();
        }
        using var scope = h.NewScope();
        var result = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Empty(await ItemsAsync(h));
    }

    // ================================================================ 4. paid-after-upload suppression

    [Fact]
    public async Task ACustomerWhoPaysAfterUpload_IsMadeNotCallable_AndOthersAreLeftAlone()
    {
        using var h = await SeededAsync(3);
        await UploadAsync(h);
        var items = await ItemsAsync(h);
        var paid = items.Single(i => i.CustomerName == "Customer 2");
        h.Source.Items.RemoveAll(r => r.TenantId == "T0002");                    // paid in PACT after the upload

        using var scope = h.NewScope();
        var sweep = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.True(sweep.Ran);
        Assert.Equal((3, 1, 0, 0, 2), (sweep.Examined, sweep.Suppressed, sweep.Failed, sweep.Unconfirmed, sweep.Unchanged));

        var call = Assert.Single(h.Client.Suppressions);
        Assert.Equal(paid.GenesysContactId, call.ContactId);
        Assert.Equal(CurrentMonthList, call.ContactListId);
        Assert.Equal(CurrentMonthList, call.Contact.ContactListId);
        Assert.Equal("Customer 2", call.Contact.CustomerName);
        var after = await ItemsAsync(h);
        var suppressed = Assert.Single(after, i => i.SuppressionStatus == ContactSuppressionStatus.Suppressed);
        Assert.Equal(paid.RecordKey, suppressed.RecordKey);
        Assert.NotNull(suppressed.SuppressedAtUtc);
        Assert.All(after.Where(i => i.RecordKey != paid.RecordKey), i => { Assert.Equal(ContactSuppressionStatus.None, i.SuppressionStatus); Assert.NotNull(i.BalanceCheckedAtUtc); });

        // Already suppressed contacts are not touched again.
        var again = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.Equal(2, again.Examined);
        Assert.Single(h.Client.Suppressions);
    }

    [Fact]
    public async Task APartPaymentOrAChangedDueDateAlsoStopsTheCall_ButAnUnchangedBalanceDoesNot()
    {
        using var h = await SeededAsync(3);
        await UploadAsync(h);
        var one = h.Source.Items.FindIndex(r => r.TenantId == "T0001");
        h.Source.Items[one] = h.Source.Items[one] with { Amount = 50m, PlanAmount = 101m, AllocatedAmount = 51m };   // part-paid
        var two = h.Source.Items.FindIndex(r => r.TenantId == "T0002");
        h.Source.Items[two] = h.Source.Items[two] with { DueDate = new DateTime(2026, 10, 25) };                       // moved
        using var scope = h.NewScope();
        var sweep = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.Equal(2, sweep.Suppressed);
        Assert.Equal(1, sweep.Unchanged);
        Assert.Equal(["Customer 1", "Customer 2"], h.Client.Suppressions.Select(s => s.Contact.CustomerName).Order());
    }

    [Fact]
    public async Task SuppressionOutcomes_RejectedIsRetried_UnknownIsUnconfirmed_AnUnconfirmedCallableIsNotBelieved()
    {
        using var h = await SeededAsync(4);
        await UploadAsync(h);
        h.Source.Items.Clear();                                                  // all four paid
        h.Client.SuppressScript.Enqueue(new(GenesysUploadOutcome.Rejected, 400, null, false, "Genesys rejected the request (HTTP 400); nothing was changed."));
        h.Client.SuppressScript.Enqueue(new(GenesysUploadOutcome.Unknown, null, null, false, "The request timed out."));
        h.Client.SuppressScript.Enqueue(new(GenesysUploadOutcome.Accepted, 200, true, false, null));           // 2xx but still callable
        h.Client.SuppressScript.Enqueue(new(GenesysUploadOutcome.Accepted, 404, null, true, null));            // contact no longer exists
        using var scope = h.NewScope();
        var first = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.Equal((1, 1, 2), (first.Suppressed, first.Unconfirmed, first.Failed));
        var statuses = (await ItemsAsync(h)).Select(i => i.SuppressionStatus).Order().ToList();
        Assert.Equal(new[] { ContactSuppressionStatus.Suppressed, ContactSuppressionStatus.Failed, ContactSuppressionStatus.Failed, ContactSuppressionStatus.Unconfirmed }.Order(), statuses);

        // Everything not suppressed is retried by the next sweep (default scripted answer: accepted, callable=false).
        var second = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.Equal(3, second.Examined);
        Assert.Equal(3, second.Suppressed);
        Assert.All(await ItemsAsync(h), i => Assert.Equal(ContactSuppressionStatus.Suppressed, i.SuppressionStatus));
        Assert.Equal(7, h.Client.Suppressions.Count);
    }

    [Fact]
    public async Task AnExceptionFromGenesysDuringSuppressionIsUnconfirmed_NotLost()
    {
        using var h = await SeededAsync(1);
        await UploadAsync(h);
        h.Source.Items.Clear();
        using var scope = h.NewScope(new FaultyClient());
        var sweep = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.Equal(1, sweep.Unconfirmed);
        var item = Assert.Single(await ItemsAsync(h));
        Assert.Equal(ContactSuppressionStatus.Unconfirmed, item.SuppressionStatus);
        Assert.NotNull(item.SuppressionError);
    }

    private sealed class FaultyClient : IGenesysOutboundClient
    {
        // The destination check before an upload is read-only and succeeds here; this double only misbehaves where its name says.
        public Task<GenesysContactListInfo> GetContactListAsync(string contactListId, CancellationToken ct) => Task.FromResult(new GenesysContactListInfo(GenesysUploadOutcome.Accepted, 200, TigerCS.Application.Modules.Collections.Review.GenesysContactTemplate.ColumnsFor(string.Equals(contactListId, ReviewHarness.ManualListId, StringComparison.OrdinalIgnoreCase)).ToList(), null));
        public Task<GenesysCampaignLookupResult> FindCampaignsByContactListAsync(string contactListId, CancellationToken ct) => Task.FromResult(new GenesysCampaignLookupResult(GenesysUploadOutcome.Accepted, 200, [], null));
        public Task<GenesysUploadResult> UploadContactsAsync(string contactListId, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct) => throw new TimeoutException();
        public Task<GenesysSuppressResult> SetNotCallableAsync(string contactListId, string contactId, GenesysContactPayload contact, CancellationToken ct) => throw new TimeoutException();
        public Task<GenesysCampaignLookupResult> FindCampaignsByNameAsync(string name, CancellationToken ct) => throw new TimeoutException();
        public Task<GenesysCampaignCreateResult> CreateCampaignAsync(string name, string contactListId, GenesysCampaignOptions template, CancellationToken ct) => throw new TimeoutException();
    }

    [Fact]
    public async Task IfCurrentBalancesCannotBeRead_NothingIsSuppressedOrAssumedPaid()
    {
        using var h = await SeededAsync(2);
        await UploadAsync(h);
        h.Source.Failure = new PactReceivablesSourceException("The PACT report connection is not configured.");
        using var scope = h.NewScope();
        var sweep = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.False(sweep.Ran);
        Assert.Empty(h.Client.Suppressions);
        Assert.All(await ItemsAsync(h), i => Assert.Equal(ContactSuppressionStatus.None, i.SuppressionStatus));
    }

    [Fact]
    public async Task TheSweepDoesNothingUnlessEnabled_AndHonoursItsWindowAndBatchLimit()
    {
        using var h = await SeededAsync(3, configure: x => x.Genesys.MaxSuppressionsPerSweep = 1);
        await UploadAsync(h);
        h.Source.Items.Clear();
        using var scope = h.NewScope();
        h.Genesys.SuppressionEnabled = false;
        Assert.False((await scope.Suppression.SweepAsync(CancellationToken.None)).Ran);
        h.Genesys.SuppressionEnabled = true;
        Assert.Equal(1, (await scope.Suppression.SweepAsync(CancellationToken.None)).Examined);            // one contact per sweep (rate limit)
        Assert.Equal(1, (await scope.Suppression.SweepAsync(CancellationToken.None)).Examined);
        Assert.Equal(1, (await scope.Suppression.SweepAsync(CancellationToken.None)).Examined);
        Assert.Equal(0, (await scope.Suppression.SweepAsync(CancellationToken.None)).Examined);            // all done
        Assert.Equal(3, h.Client.Suppressions.Select(s => s.ContactId).Distinct().Count());

        using var old = await SeededAsync(2, configure: x => x.Genesys.SuppressionWindowDays = 1);
        await UploadAsync(old);
        old.Time.Advance(TimeSpan.FromDays(2));
        old.Source.Items.Clear();
        using var oldScope = old.NewScope();
        Assert.Equal(0, (await oldScope.Suppression.SweepAsync(CancellationToken.None)).Examined);          // past the window: no longer swept
    }

    [Fact]
    public async Task ASweepAfterTheMonthRolledOver_DoesNotMistakeTheNewMonthForPayment()
    {
        using var h = await SeededAsync(2, configure: x => x.Genesys.SuppressionWindowDays = 30);
        await UploadAsync(h);                                                    // uploaded on 14 Oct
        h.Time.Advance(TimeSpan.FromDays(20));                                   // swept on 3 Nov
        h.Source.Items.RemoveAll(r => r.TenantId == "T0001");                    // one is genuinely paid
        using var scope = h.NewScope();
        var sweep = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.Equal(1, sweep.Suppressed);
        Assert.Equal(1, sweep.Unchanged);                                        // the other stays callable although November's schedule differs
    }

    [Fact]
    public async Task UploadedContactsWithoutAGenesysId_CannotBeSwept_AndAreCountedForManualFollowUp()
    {
        using var h = await SeededAsync(3);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Unknown);
        var (dto, id) = await ApproveAsync(h);
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        var detail = (await scope.Dispatch.GetAsync(h.Manager, dto.DispatchId, CancellationToken.None)).Value!;
        var done = await scope.Dispatch.ReconcileAsync(h.Manager, dto.DispatchId, detail.Batches.Single().BatchId, new("ConfirmedUploaded", "3 contacts present in the list"), CancellationToken.None);
        Assert.Equal(3, done.Value!.UploadedWithoutContactId);
        h.Source.Items.Clear();
        var sweep = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.Equal(0, sweep.Examined);                                         // nothing it can safely update
        Assert.Empty(h.Client.Suppressions);
    }

    // ================================================================ 8. ambiguous Genesys outcomes

    [Fact]
    public async Task AcceptedWithFewerIdsThanContacts_LeavesTheMissingOnesUnconfirmed()
    {
        using var h = await SeededAsync(4);
        h.Client.IdCounts.Enqueue(3);
        var dto = await UploadAsync(h);
        Assert.Equal("CompletedWithErrors", dto.Status);
        Assert.Equal(3, dto.UploadedCount);
        Assert.Equal(1, dto.UnknownCount);
        Assert.Equal("UnknownOutcome", Assert.Single(dto.Batches).Status);
        Assert.Contains("different number", dto.Batches[0].Error);
        Assert.Single(await ItemsAsync(h), i => i.Status == DispatchItemStatus.UnknownOutcome && i.GenesysContactId is null);
        // Nothing is resent.
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Single(h.Client.Calls);
    }

    [Fact]
    public async Task AnAcceptedResponseWithNoReadableIds_IsAnUnconfirmedBatch()
    {
        using var h = await SeededAsync(2);
        h.Client.IdCounts.Enqueue(0);
        var dto = await UploadAsync(h);
        Assert.Equal(2, dto.UnknownCount);
        Assert.Equal(0, dto.UploadedCount);
    }

    [Fact]
    public async Task AnUnconfirmedBatchDoesNotStopTheOthers_AndIsNeverResentWhenTheJobRunsAgain()
    {
        using var h = new ReviewHarness();
        for (var n = 1; n <= 2500; n++) h.Source.Items.Add(ReviewHarness.Row(n));
        await h.RefreshAsync();
        h.Client.Script.Enqueue(GenesysUploadOutcome.Accepted);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Unknown);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Accepted);
        var (dto, id) = await ApproveAsync(h);
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        var final = (await scope.Dispatch.GetAsync(h.Manager, dto.DispatchId, CancellationToken.None)).Value!;
        Assert.Equal(new[] { "Uploaded", "UnknownOutcome", "Uploaded" }, final.Batches.Select(b => b.Status));
        Assert.Equal((1500, 1000), (final.UploadedCount, final.UnknownCount));
        Assert.Equal(3, h.Client.Calls.Count);                                   // exactly one request per batch, however often the job ran
        Assert.Equal(2500, h.Client.Calls.SelectMany(c => c.Contacts).Select(c => c.Phone).Distinct().Count());
    }

    // ================================================================ 8. cancellation racing with job start

    [Fact]
    public async Task CancelRacingWithJobStartup_NeverBothCancelsAndUploads()
    {
        var cancelWins = 0;
        var jobWins = 0;
        for (var round = 0; round < 12; round++)
        {
            using var h = await SeededAsync(3, file: true);
            var (dto, id) = await ApproveAsync(h);
            CollectionsResult<DispatchDto> cancel = null!;
            var go = new TaskCompletionSource();
            var runJob = Task.Run(async () => { await go.Task; await Task.Delay(round * 4); using var s = h.NewScope(); await s.Dispatch.ExecuteAsync(id, CancellationToken.None); });
            var runCancel = Task.Run(async () => { await go.Task; using var s = h.NewScope(); cancel = await s.Dispatch.CancelAsync(h.Manager, dto.DispatchId, CancellationToken.None); });
            go.SetResult();
            await Task.WhenAll(runJob, runCancel);

            using var check = h.NewScope();
            var stored = (await check.Store.GetDispatchByIdAsync(id, CancellationToken.None))!;
            var items = await ItemsAsync(h);
            if (cancel.IsSuccess)
            {
                cancelWins++;
                Assert.Equal(DispatchStatus.Cancelled, stored.Status);
                Assert.Empty(h.Client.Calls);                                    // cancelled means nothing was uploaded
                Assert.All(items, i => Assert.Equal(DispatchItemStatus.Released, i.Status));
            }
            else
            {
                jobWins++;
                Assert.Equal(CollectionsOutcome.InvalidRequest, cancel.Outcome);
                Assert.Equal(DispatchStatus.Completed, stored.Status);          // the job owned it: cancel was refused, upload happened once
                Assert.Single(h.Client.Calls);
                Assert.All(items, i => Assert.Equal(DispatchItemStatus.UploadedToGenesys, i.Status));
            }
        }
        output.WriteLine($"cancel won {cancelWins}, job won {jobWins} of 12 races");
        Assert.Equal(12, cancelWins + jobWins);
    }

    [Fact]
    public async Task CancelWhileTheJobIsAlreadyRevalidating_IsRefused_AndTheUploadProceeds()
    {
        using var h = await SeededAsync(2, file: true);
        var (dto, id) = await ApproveAsync(h);
        CollectionsResult<DispatchDto>? cancel = null;
        h.Source.OnRead = async ct =>
        {
            using var s = h.NewScope();
            cancel = await s.Dispatch.CancelAsync(h.Manager, dto.DispatchId, ct);
        };
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, cancel!.Outcome);
        Assert.Single(h.Client.Calls);
        Assert.Equal(DispatchStatus.Completed, (await scope.Store.GetDispatchByIdAsync(id, CancellationToken.None))!.Status);
    }

    // ================================================================ 6. leases, progress and concurrency during long validation

    [Fact]
    public async Task TheLeaseIsRenewedWhileASlowSourceReadRuns_SoNoSecondWorkerCanStart()
    {
        using var h = await SeededAsync(2, file: true);
        var (_, id) = await ApproveAsync(h);
        bool? secondWorkerAcquired = null;
        h.Source.OnRead = async ct =>
        {
            h.Time.Advance(TimeSpan.FromMinutes(4));  await Task.Delay(300, ct);   // heartbeat (20 ms) renews to now + 5 min
            h.Time.Advance(TimeSpan.FromMinutes(2));  await Task.Delay(300, ct);   // 6 minutes after the lease was first taken
            using var other = h.NewScope();
            secondWorkerAcquired = await other.Store.TryAcquireLeaseAsync(id, Guid.NewGuid(), h.Time.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(5), ct);
        };
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        Assert.False(secondWorkerAcquired);                                       // without the heartbeat the original 5-minute lease would have expired
        Assert.Single(h.Client.Calls);
    }

    [Fact]
    public async Task AWorkerThatLosesItsLeaseDuringTheRead_StopsWithoutWritingOrUploading()
    {
        using var h = await SeededAsync(2, file: true);
        var (_, id) = await ApproveAsync(h);
        var thief = Guid.NewGuid();
        h.Source.OnRead = async ct =>
        {
            using var context = h.CreateContext();
            await context.CollectionsDispatches.ExecuteUpdateAsync(s => s.SetProperty(d => d.LeaseOwner, thief), ct);   // another worker took over
        };
        h.Source.Delay = TimeSpan.FromSeconds(20);                                // a long read the heartbeat has to interrupt
        var clock = Stopwatch.StartNew();
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Empty(h.Client.Calls);
        var stored = (await scope.Store.GetDispatchByIdAsync(id, CancellationToken.None))!;
        Assert.Equal(DispatchStatus.Revalidating, stored.Status);                 // untouched: the new owner decides
        Assert.Equal(thief, stored.LeaseOwner);
        Assert.All(stored.Items, i => Assert.Equal(DispatchItemStatus.Approved, i.Status));
    }

    [Fact]
    public async Task ProgressIsVisibleDuringValidation_AndTheTimingIsRecorded()
    {
        using var h = await SeededAsync(2, file: true);
        var (dto, id) = await ApproveAsync(h);
        string? phaseDuringRead = null;
        h.Source.OnRead = async ct =>
        {
            using var s = h.NewScope();
            phaseDuringRead = (await s.Store.GetDispatchByIdAsync(id, ct))!.Phase;
            await Task.Delay(120, ct);
        };
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        Assert.Contains("Checking current balances", phaseDuringRead);
        var done = (await scope.Dispatch.GetAsync(h.Manager, dto.DispatchId, CancellationToken.None)).Value!;
        Assert.Equal("Finished", done.Phase);
        Assert.True(done.RevalidationMs >= 100, $"{done.RevalidationMs} ms");
    }

    [Fact]
    public async Task BothCompaniesAreReadOnceEach_AndConcurrently()
    {
        using var h = new ReviewHarness(file: true);
        h.Source.Items.Add(ReviewHarness.Row(1));
        h.Source.Items.Add(ReviewHarness.Row(2, company: 32));
        await h.RefreshAsync();
        var (_, id) = await ApproveAsync(h);
        var seedReads = h.Source.Reads;
        var inFlight = 0;
        var bothInFlight = new TaskCompletionSource<bool>();
        h.Source.OnRead = async ct =>
        {
            if (Interlocked.Increment(ref inFlight) == 2) bothInFlight.TrySetResult(true);
            // A sequential implementation would never get the second read started while this one waits.
            Assert.Same(bothInFlight.Task, await Task.WhenAny(bothInFlight.Task, Task.Delay(5000, ct)));
        };
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        Assert.Equal(2, h.Source.Reads - seedReads);
        Assert.Single(h.Client.Calls);
    }

    [Fact]
    public async Task MeasuredAppSideCost_OfValidatingAQuarterOfAMillionSourceRows()
    {
        var rows = new List<PactReceivableInstalment>(250_000);
        for (var n = 1; n <= 62_500; n++)
            for (var k = 0; k < 4; k++)                                           // four instalments per unit
                rows.Add(ReviewHarness.Row(n, 100m + k, day: 1 + k * 6) with { UnitId = 100_000 + n, UnitCode = $"TP140-{n}", TenantId = $"T{n:D6}", Mobile = $"97150{n % 10_000_000:D7}" });
        var context = new ReviewBuildContext(new DateOnly(2026, 10, 14), ReviewHarness.Now, "t", "AED", true, true, false, 0.0001m, new Dictionary<string, string>());
        var watch = Stopwatch.StartNew();
        var built = ReviewRecordBuilder.Build(rows, context);
        watch.Stop();
        output.WriteLine($"ReviewRecordBuilder: {rows.Count:N0} source rows -> {built.Records.Count:N0} records in {watch.ElapsedMilliseconds:N0} ms");
        Assert.True(built.Records.Count > 0);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{watch.Elapsed}");

        using var h = new ReviewHarness();
        h.Source.Items.AddRange(rows.Take(40_000).Select(r => r));
        var refresh = Stopwatch.StartNew();
        await h.RefreshAsync();
        refresh.Stop();
        output.WriteLine($"Refresh job (read + validate + save, SQLite) for 40,000 source rows: {refresh.ElapsedMilliseconds:N0} ms");
        using var scope = h.NewScope();
        var query = Stopwatch.StartNew();
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth", PaymentStatus: "All", MinRemaining: 0m), 3, 25, CancellationToken.None)).Value!;
        query.Stop();
        output.WriteLine($"Filtered + counted + paged review query over {page.TotalCount:N0} records: {query.ElapsedMilliseconds:N0} ms");
        Assert.Equal(25, page.Items.Count);
    }
}
