#region

using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Match Overview damaged-demo banner, the App-tier consumer of the S11 parse-diagnostics
///     channel (v0.6.0): <see cref="ParsedDemo.Health" /> pushed onto <see cref="MatchOverviewTabViewModel" />
///     via <c>SetParseHealth</c>. The channel's Parser-tier behavior (accumulate-then-drain,
///     per-thread reset, and how a health grade is derived) is covered separately in
///     <c>CS2DemoKit.Parser.Tests.ParseDiagnosticsTests</c>.
/// </summary>
public class MatchOverviewDamagedBannerTests
{
    private static ParseWarning[] Warnings() =>
    [
        new(ParseWarningCodes.StringTableCreateFailed, "table 'userinfo' failed"),
        new(ParseWarningCodes.PlayerInfoUnreadable, "slot 3 dropped")
    ];

    /// <summary>
    ///     The Match Overview banner: a Damaged verdict for the CURRENT subject raises it with a
    ///     summarized line; a stale subject's push is ignored; a healthy re-push clears it (mirrors
    ///     the sample-clip banner's stability contract).
    /// </summary>
    [Test]
    public async Task DamagedBanner_IsSubjectKeyed()
    {
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("broken.dem", null, null, "C:\\demos\\broken.dem");

        // A stale key must not stamp the page.
        vm.SetParseHealth("C:\\demos\\other.dem", ParseHealth.Damaged, Warnings());
        await Assert.That(vm.IsDamaged).IsFalse();

        vm.SetParseHealth("C:\\demos\\broken.dem", ParseHealth.Damaged, Warnings());
        await Assert.That(vm.IsDamaged).IsTrue();
        await Assert.That(vm.DamageSummary).Contains("string-table");
        await Assert.That(vm.DamageSummary).Contains("player record");

        // A healthy re-push (e.g. after a clean re-open of the same subject) clears the banner.
        vm.SetParseHealth("C:\\demos\\broken.dem", ParseHealth.Clean, []);
        await Assert.That(vm.IsDamaged).IsFalse();
    }

    /// <summary>
    ///     The regression this banner's rewrite exists to prevent: <b>warnings are not damage</b>.
    ///     <para>
    ///         A demo recorded by a CS2 build newer than the parser drops net messages the parser has
    ///         no case for. That is a real warning and a real information loss, but the recording is
    ///         fine and the match renders correctly: the parser grades it
    ///         <see cref="ParseHealth.Degraded" />. The banner previously keyed off
    ///         <c>Warnings.Count > 0</c>, which would accuse every one of those demos of being
    ///         damaged; since CS2 ships builds ahead of the parser routinely, that is a banner on
    ///         essentially every demo, which trains users to ignore the one time it matters.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments(ParseHealth.Clean)]
    [Arguments(ParseHealth.Degraded)]
    public async Task WarningsWithoutDamage_DoNotRaiseTheBanner(ParseHealth health)
    {
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("newer-build.dem", null, null, "C:\\demos\\newer-build.dem");

        vm.SetParseHealth("C:\\demos\\newer-build.dem", health, Warnings());

        await Assert.That(vm.IsDamaged).IsFalse()
            .Because("only Damaged means the demo's own data failed to decode; warnings alone do not");
        await Assert.That(vm.DamageSummary).IsEmpty();
    }
}
