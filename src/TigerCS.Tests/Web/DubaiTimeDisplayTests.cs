// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>
/// <b>One conversion, one place.</b> Everything is stored and compared in UTC; the interface shows Dubai time (UTC+4, no daylight saving) through
/// <see cref="DubaiTime"/> and nothing else - never the web server's or the viewer's zone, and never twice. The source scan keeps it that way: it fails if a host-dependent
/// conversion or a hand-written "+4 hours" returns to the Web project.
/// </summary>
public class DubaiTimeDisplayTests
{
    private static readonly DateTime Utc = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UtcBecomesDubai_ByExactlyFourHours_OnceOnly()
    {
        Assert.Equal(new DateTime(2026, 11, 20, 14, 0, 0), DubaiTime.FromUtc(Utc));
        Assert.Equal("20 Nov 2026 14:00", DubaiTime.Format(Utc, "d MMM yyyy HH:mm"));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]   // EF Core returns a UTC column as Unspecified: it is read as UTC, never as the host's local time
    public void StoredUtcValues_AreReadAsUtc_WhateverTheirKind(DateTimeKind kind) =>
        Assert.Equal("14:00", DubaiTime.Format(DateTime.SpecifyKind(Utc, kind), "HH:mm"));

    [Fact]
    public void ALocalKindValue_IsConvertedFromItsRealInstant_NotReadAsUtc()
    {
        var local = Utc.ToLocalTime();                                         // the same instant, expressed in whatever zone this machine uses
        Assert.Equal("14:00", DubaiTime.Format(local, "HH:mm"));
    }

    [Fact]
    public void TheResultDoesNotDependOnTheHostTimeZone_ItIsTheFixedUtcPlusFourOffset()
    {
        Assert.Equal(TimeSpan.FromHours(4), DubaiTime.Offset);
        Assert.Equal(Utc + DubaiTime.Offset, DubaiTime.FromUtc(Utc));          // whatever TimeZoneInfo.Local says on this machine
    }

    [Fact]
    public void TheDubaiDay_RollsOverAtTwentyHundredUtc_NotAtTheServersMidnight()
    {
        var lateEvening = new DateTime(2026, 11, 20, 21, 30, 0, DateTimeKind.Utc);   // 01:30 on the 21st in Dubai
        Assert.Equal(new DateTime(2026, 11, 21), DubaiTime.FromUtc(lateEvening).Date);
        Assert.True(DubaiTime.IsSameDubaiDay(lateEvening, new DateTime(2026, 11, 21, 1, 0, 0, DateTimeKind.Utc)));
        Assert.False(DubaiTime.IsSameDubaiDay(lateEvening, new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 21), DubaiTime.TodayFrom(lateEvening));
    }

    [Fact]
    public void ANullInstant_FormatsAsNull() => Assert.Null(DubaiTime.Format((DateTime?)null, "HH:mm"));

    [Fact]
    public void EveryExistingDisplayHelper_UsesTheSameConversion()
    {
        Assert.Equal("20 Nov 2026 14:00 (UAE)", TicketDisplay.UaeTime(Utc));
        Assert.Equal("20 Nov 14:00", TicketDisplay.UaeShort(Utc));
        Assert.Equal("14:00", TicketDisplay.UaeClock(Utc));
        Assert.Equal("20 Nov 2026 14:00:00", TicketDisplay.DubaiTimestamp(Utc));
        Assert.Equal(DubaiTime.Format(Utc, "d MMM yyyy HH:mm"), CustomerPaymentPanel.DubaiTime(Utc));
    }

    [Fact]
    public void TheWebProject_HasNoHostDependentOrHandWrittenTimeConversion()
    {
        var web = Path.Combine(FindSourceRoot(), "TigerCS.Web");
        var forbidden = new[] { ".ToLocalTime(", "DateTime.Now", "DateTime.Today", "DateTimeOffset.Now", "TimeZoneInfo.Local", ".AddHours(4)", ".AddHours( 4)" };
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".cshtml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.EndsWith("DubaiTime.cs")))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///") || line.TrimStart().StartsWith("@*"))
                {
                    continue;
                }

                if (forbidden.FirstOrDefault(t => line.Contains(t, StringComparison.Ordinal)) is { } token)
                {
                    offenders.Add($"{Path.GetRelativePath(web, file)}:{lineNumber} uses {token}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Display code must convert through DubaiTime only:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static string FindSourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var src = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(src, "TigerCS.Web")))
            {
                return src;
            }
        }

        throw new DirectoryNotFoundException("src/TigerCS.Web was not found above " + AppContext.BaseDirectory);
    }
}
