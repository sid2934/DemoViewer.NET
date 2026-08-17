#region

using Cs2DemoKit.Parser;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Match Overview damaged-demo banner — the App-tier consumer of the S11 parse-diagnostics
///     channel (v0.6.0): <see cref="ParsedDemo.Warnings" /> pushed onto <see cref="MatchOverviewTabViewModel" />
///     via <c>SetParseWarnings</c>. The channel's Parser-tier behavior (accumulate-then-drain,
///     per-thread reset) is covered separately in <c>Cs2DemoKit.Parser.Tests.ParseDiagnosticsTests</c>.
/// </summary>
public class MatchOverviewDamagedBannerTests
{
    /// <summary>
    ///     The Match Overview banner: warnings for the CURRENT subject raise it with a summarized
    ///     line; a stale subject's push is ignored; an empty list clears it (mirrors the
    ///     sample-clip banner's stability contract).
    /// </summary>
    [Test]
    public async Task DamagedBanner_IsSubjectKeyed()
    {
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("broken.dem", null, null, "C:\\demos\\broken.dem");

        ParseWarning[] warnings =
        [
            new(ParseWarningCodes.StringTableCreateFailed, "table 'userinfo' failed"),
            new(ParseWarningCodes.PlayerInfoUnreadable, "slot 3 dropped")
        ];

        // A stale key must not stamp the page.
        vm.SetParseWarnings("C:\\demos\\other.dem", warnings);
        await Assert.That(vm.IsDamaged).IsFalse();

        vm.SetParseWarnings("C:\\demos\\broken.dem", warnings);
        await Assert.That(vm.IsDamaged).IsTrue();
        await Assert.That(vm.DamageSummary).Contains("string-table");
        await Assert.That(vm.DamageSummary).Contains("player record");

        // A healthy re-push (e.g. after a clean re-open of the same subject) clears the banner.
        vm.SetParseWarnings("C:\\demos\\broken.dem", []);
        await Assert.That(vm.IsDamaged).IsFalse();
    }
}
