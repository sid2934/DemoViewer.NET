#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D;
using ThirdPartyFake;
using ThirdPartyFake.Scene;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The configuration the app ships: every scene, map and timeline backend is built with no tracker of
///     its own (<c>SdkSceneHosts.Install</c>) and reads <see cref="ExtensionFaults.Current" /> per call,
///     which the desktop head installs before Avalonia starts.
///     <para>
///         The installed tracker's one scope owns ThirdPartyFake, not this assembly, so <c>Owner()</c>
///         answers null for anything declared here and a part this assembly writes keeps its bare id.
///     </para>
/// </summary>
// Keyless, not keyed: a keyless [NotInParallel] runs under TUnit's exclusive not-in-parallel lock, which
// every other test in the assembly takes shared, and Current is process-wide while installed. A key would
// exclude only the tests carrying that same key.
[NotInParallel]
public class ExtensionFaultsInstallTests
{
    /// <summary>
    ///     A layer and a tool contributed from an extension's own assembly are filed under it,
    ///     <c>ext.&lt;extension id&gt;.&lt;local id&gt;</c>, which is what makes two extensions' ids unable
    ///     to collide. The map view files a layer the same way as the scene.
    /// </summary>
    [Test]
    public async Task AnExtensionsLayerAndTool_AreFiledUnderTheProcessTracker()
    {
        ExtensionFaults? previous = ExtensionFaults.Current;
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                // App.Initialize sets the production factories per isolated application, but under
                // PerAssembly isolation it runs once a session, so a swapped factory would still be in place.
                SdkSceneHosts.Install();
                ExtensionFaults.For([new FakeExtension()]).Install();

                SceneView scene = new() { Source = new FakeSceneSource() };
                ILayerHandle sceneLayer = scene.AddLayer("heat", FakeSceneLayer.Create);
                IToolHandle tool = scene.AddTool(new FakeSceneTool());
                MapView map = new();
                ILayerHandle mapLayer = map.AddLayer("heat", FakeSceneLayer.Create);

                using (Assert.Multiple())
                {
                    await Assert.That(sceneLayer.LayerId).IsEqualTo("ext." + FakeExtension.ExtensionId + ".heat");
                    await Assert.That(tool.ToolId).IsEqualTo("ext." + FakeExtension.ExtensionId + ".pin");
                    await Assert.That(mapLayer.LayerId).IsEqualTo("ext." + FakeExtension.ExtensionId + ".heat");
                }

                sceneLayer.Dispose();
                tool.Dispose();
                mapLayer.Dispose();
            });
        }
        finally
        {
            ExtensionFaults.RestoreCurrent(previous);
        }
    }

    /// <summary>
    ///     The two reads that take no injected tracker: the timeline's track registration and the scene
    ///     source's own guard. A throwing track or source of an extension's draws nothing and is counted
    ///     against that extension.
    /// </summary>
    [Test]
    public async Task AnExtensionsTrackAndSource_AreGuardedByTheProcessTracker()
    {
        ExtensionFaults? previous = ExtensionFaults.Current;
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SdkSceneHosts.Install();
                ExtensionFaults faults = ExtensionFaults.For([new FakeExtension()]).Install();

                using SceneTimeline timeline = new();
                timeline.RegisterTrack(new FakeTimelineTrack { FailMarkers = true });
                timeline.Rebuild(new FakeTimelineData(100));

                HostedSceneView.SourceFrameHost source = new(new FakeSceneSource { FailMapAsset = true });

                using (Assert.Multiple())
                {
                    await Assert.That(timeline.Markers).IsEmpty().Because("the guarded track built nothing");
                    await Assert.That(source.MapAsset).IsNull().Because("the guarded read answered its fallback");
                    await Assert.That(faults.StateOf(FakeExtension.MasterSwitch).Count).IsGreaterThanOrEqualTo(2)
                        .Because("both throws were counted against the extension that wrote them");
                }

                source.Detach();
            });
        }
        finally
        {
            ExtensionFaults.RestoreCurrent(previous);
        }
    }
}
