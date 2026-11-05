using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>
/// The application's type system, guarded where it lives — one self-hosted
/// family, one scale, and a floor below which nothing is allowed to shrink.
/// A stylesheet is the only place these can be stated, so they are asserted
/// against it rather than against any one page.
/// </summary>
public sealed partial class AppTypographyTests
{
    private static string SiteCss([CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return File.ReadAllText(Path.Combine(srcDir, "TigerCS.Web", "wwwroot", "css", "site.css"));
    }

    [GeneratedRegex(@"font-size:\s*([0-9.]+)px")]
    private static partial Regex FontSize { get; }

    /// <summary>
    /// One modern system stack, resolved on the machine. Nothing is fetched:
    /// the Content-Security-Policy allows only <c>font-src 'self'</c> and the
    /// repository ships no font file, so a Google Fonts @import or a remote
    /// @font-face would simply be blocked.
    /// </summary>
    [Fact]
    public void SiteCss_UsesOneSelfContainedFontStack_AndFetchesNothing()
    {
        var css = SiteCss();

        Assert.Contains("--font-sans: Inter, \"Segoe UI\", Roboto, Helvetica, Arial, sans-serif;", css, StringComparison.Ordinal);
        Assert.Contains("font-family: var(--font-sans);", css, StringComparison.Ordinal);

        Assert.DoesNotContain("@import", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@font-face", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.googleapis.com", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.gstatic.com", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", css, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Nothing in the application is smaller than 12px: a label is quiet because of its colour and weight, never because it is tiny.</summary>
    [Fact]
    public void SiteCss_HasNoTypeSmallerThanTwelvePixels()
    {
        var tooSmall = FontSize.Matches(SiteCss())
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(size => size < 12)
            .Distinct()
            .ToList();

        Assert.Empty(tooSmall);
    }

    /// <summary>The scale's anchors, in the ranges the hierarchy is built on.</summary>
    [Theory]
    [InlineData("--text-2xl", 22, 24)]  // page title
    [InlineData("--text-xl", 22, 24)]   // record title
    [InlineData("--text-lg", 15, 16)]   // section heading
    [InlineData("--text-md", 14, 15)]   // card heading
    [InlineData("--text-base", 13, 14)] // body
    [InlineData("--text-sm", 13, 13)]   // tables, lists, controls, buttons
    [InlineData("--text-xs", 12, 12)]   // labels, meta
    [InlineData("--text-2xs", 12, 12)]  // uppercase micro-labels — the floor
    public void SiteCss_TypeScale_SitsInTheIntendedRanges(string token, double min, double max)
    {
        var match = Regex.Match(SiteCss(), Regex.Escape(token) + @":\s*([0-9.]+)px");
        Assert.True(match.Success, $"{token} is not defined.");

        var size = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(size, min, max);
    }

    [Fact]
    public void SiteCss_AppliesTheScaleToTheHierarchysAnchors()
    {
        var css = SiteCss();

        // Page title, KPI number, section title, table, button, navigation.
        Assert.Contains(".page-title { font-size: var(--text-2xl); font-weight: var(--weight-title);", css, StringComparison.Ordinal);
        Assert.Contains(".kpi-card strong { font-size: 26px; font-weight: 700;", css, StringComparison.Ordinal);
        Assert.Contains(".panel__title { font-size: var(--text-lg); font-weight: var(--weight-section);", css, StringComparison.Ordinal);
        Assert.Contains(".data-table { width: 100%; border-collapse: collapse; font-size: var(--text-sm); }", css, StringComparison.Ordinal);
        Assert.Contains("font-size: var(--text-sm); font-weight: var(--weight-strong); line-height: 1; letter-spacing: -0.005em;", css, StringComparison.Ordinal);
        Assert.Contains("font-size: var(--text-base); font-weight: var(--weight-medium); color: var(--color-text-secondary);", css, StringComparison.Ordinal);

        // Weights: titles are heavy, section titles a step below, controls semibold.
        foreach (var (token, min, max) in new[] { ("--weight-title", 650, 700), ("--weight-section", 600, 650), ("--weight-strong", 600, 600), ("--weight-medium", 500, 500) })
        {
            var match = Regex.Match(css, Regex.Escape(token) + @":\s*([0-9]+);");
            Assert.True(match.Success, $"{token} is not defined.");
            Assert.InRange(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), min, max);
        }
    }

    /// <summary>The one official brand gold, defined once as --tiger-gold — a subtle accent; primary buttons are charcoal with a white label.</summary>
    [Fact]
    public void SiteCss_DefinesTheOfficialTigerGold_Once()
    {
        var css = SiteCss();

        Assert.Contains("--tiger-gold: #C5A15A;", css, StringComparison.Ordinal);
        Assert.Contains("--color-gold: var(--tiger-gold);", css, StringComparison.Ordinal);
        Assert.Contains(".btn-gold { background: var(--color-primary); border-color: var(--color-primary); color: var(--color-on-primary); }", css, StringComparison.Ordinal);

        // No second gold: the legacy browns / ambers that used to stand in for the accent are gone.
        foreach (var retired in new[] { "#C58D19", "#B8841B", "#A5740F", "#A56F0E", "#B98A21", "#dfae4a" })
        {
            Assert.DoesNotContain(retired, css, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gold is Tiger's brand accent, not the product's colour: the semantic
    /// palette carries operational meaning, and every tone is defined once.
    /// </summary>
    [Fact]
    public void SiteCss_DefinesTheSemanticPalette_AndOneToneClassPerMeaning()
    {
        var css = SiteCss();

        // Each tone: a text/icon colour, a pale fill and a hairline.
        foreach (var (token, fill) in new[]
        {
            ("--color-info", "--color-info-tint"),
            ("--color-progress", "--color-progress-tint"),
            ("--color-success", "--color-success-pale"),
            ("--color-warning", "--color-warning-tint"),
            ("--color-critical", "--color-critical-pale"),
            ("--color-secondary-op", "--color-secondary-op-tint"),
        })
        {
            Assert.Contains($"{token}: #", css, StringComparison.Ordinal);
            Assert.Contains($"{fill}:", css, StringComparison.Ordinal);
            Assert.Contains($"{token}-line:", css, StringComparison.Ordinal);
        }

        foreach (var tone in new[] { "info", "progress", "success", "warning", "critical", "secondary", "brand" })
        {
            Assert.Contains($".tone-{tone} {{ --tone:", css, StringComparison.Ordinal);
        }
    }
}
