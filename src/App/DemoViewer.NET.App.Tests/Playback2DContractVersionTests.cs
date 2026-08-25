#region

using System.Reflection;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     B5-9's audit, as a test. Nothing in the codebase ENFORCES <c>ContractVersion</c> — it is a
///     documented claim about which additive <see cref="IModuleContext" /> members the module consumes —
///     so the enforcement is this pin plus the human read that accompanies a deliberate edit.
///     <para>
///         The three ids alongside it are persisted / cross-referenced keys: the TabId is written into the
///         session's active-tab record, the feature id is written into
///         <c>Features:Overrides:{id}</c>, and the module id identifies the module in the registry. A
///         rename of any of them is a silent reset of user state.
///     </para>
/// </summary>
public class Playback2DContractVersionTests
{
    /// <summary>
    ///     1.2.0 is the whole v2 release's ONE bump (B5 D7, registry §3.10) — A1 made it, and B2/B3/B4
    ///     consumed the same six additive members without bumping again. Changing it should require
    ///     re-reading the comment above it and re-verifying the member list by grep.
    /// </summary>
    [Test]
    public async Task ContractVersion_IsPinned() =>
        await Assert.That(new Playback2DModule().ContractVersion).IsEqualTo(new Version(1, 2, 0));

    /// <summary>
    ///     Every additive member the 1.2 comment claims must actually be ON <see cref="IModuleContext" />.
    ///     A member removed from the interface while the comment still names it is a contract version
    ///     documenting something that no longer exists.
    /// </summary>
    [Test]
    public async Task EveryAdditiveMemberTheCommentClaims_ExistsOnTheContract()
    {
        string[] members =
        [
            "MapName", "TotalFrames", "FrameIndexAtTick", "EventFrames", "IsSpeedLocked", "RequestSpeed",
            "Features", "GetEventTimeline", "AvailableEventNames", "NotifySpectateTarget"
        ];

        foreach (string member in members)
        {
            await Assert.That(typeof(IModuleContext).GetMember(member).Length).IsGreaterThan(0)
                .Because($"ContractVersion 1.2 claims IModuleContext.{member}");
        }
    }

    [Test]
    public async Task TabAndFeatureIds_AreStable()
    {
        Playback2DModule module = new();
        await Assert.That(module.Id).IsEqualTo("net.demoviewer.playback2d");

        WorkspaceTabDescriptor[] tabs = module.CreateTabs(new NoCapabilityHost()).ToArray();
        await Assert.That(tabs.Length).IsEqualTo(1);
        await Assert.That(tabs[0].TabId).IsEqualTo("playback2d.viewport");

        // MainViewModel maps TabId → feature id. Reflection because the map is a private implementation
        // detail whose CONTENT is nonetheless a persisted-key contract.
        FieldInfo field = typeof(MainViewModel).GetField("_tabFeatureIds",
                              BindingFlags.NonPublic | BindingFlags.Static)
                          ?? throw new InvalidOperationException("MainViewModel._tabFeatureIds is gone");

        Dictionary<string, string> map = (Dictionary<string, string>)field.GetValue(null)!;
        await Assert.That(map["playback2d.viewport"]).IsEqualTo("tab.playback2d");
    }

    private sealed class NoCapabilityHost : IModuleHost
    {
        public IModuleContext Context { get; } = new Playback2DFakeContext();

        public bool HasCapability(string capability) => true;

        public void Log(ModuleLogLevel level, string message)
        {
        }
    }
}
