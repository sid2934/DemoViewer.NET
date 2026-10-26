#region

using Avalonia.Threading;
using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.Playback2D.Zones;
using DemoViewer.NET.Playback2D.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Zones;
using TUnit.Core.Exceptions;
using DemoViewer.NET.Extensions.StratBook.Services.Zones;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The overlay as the app loads it from <c>&lt;config&gt;/zones/</c>: a missing directory is the
///     baked set, an unreadable file is the baked set plus a report on the Workbench, and "Reload
///     zones" picks up an edited file without a map reload. Over the committed de_nuke bake, which is
///     an asset, not a demo; skipped on a checkout without it.
/// </summary>
[NotInParallel]
public class ZoneOverlayLoadTests
{
    private const string Map = "de_nuke";

    // The hub is process-global by design, so a malformed overlay published here would otherwise sit
    // on the Rule Workbench's rows for every test that constructs one afterwards.
    [After(Test)]
    public void ForgetTheHub() => ZoneOverlayDiagnostics.Reset();

    [Test]
    public async Task MissingZonesDirectory_LoadsTheBakedSet()
    {
        string bundleDir = RequireBundle();
        using ConfigDir config = new();

        string? zonesDir = AppPaths.ZonesDirectory;
        await Assert.That(zonesDir).IsNotNull();
        await Assert.That(Path.GetFileName(zonesDir!)).IsEqualTo("zones");
        await Assert.That(Directory.Exists(zonesDir!)).IsFalse();

        ZoneLoadResult load = ZoneAssetPipeline.Load(bundleDir, zonesDir);

        await Assert.That(load.Resolver).IsNotNull();
        await Assert.That(load.OverlayApplied).IsFalse();
        await Assert.That(load.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(load.OverlayPath).IsEqualTo(Path.Combine(zonesDir!, Map + ZoneAssetPipeline.OverlaySuffix));
        await Assert.That(load.Resolver!.Zones.EffectiveVersion).IsEqualTo(load.Resolver.Zones.ZonesVersion);

        // The startup bootstrap creates the drop-in folder, exactly as it does for themes.
        await Assert.That(AppPaths.EnsureZonesDirectory()).IsEqualTo(zonesDir);
        await Assert.That(Directory.Exists(zonesDir!)).IsTrue();
    }

    [Test]
    public async Task UnreadableOverlay_LoadsTheBakedSet_AndReportsOnTheWorkbench()
    {
        RequireBundle();
        using ConfigDir config = new();
        string overlay = ConfigDir.WriteOverlay("{ this is not json");

        await HeadlessSession.RunOnUi(async () =>
        {
            ZoneOverlayDiagnostics.Reset();
            using RuleWorkbenchTabViewModel workbench = new();

            (Playback2DTabViewModel vm, _) = ActivatedTab();
            PlaceResolver? resolver = vm.Zones;

            await Assert.That(resolver).IsNotNull();
            await Assert.That(resolver!.Zones.HasOverlay).IsFalse();
            await Assert.That(vm.MapAsset!.ZoneLoad.Diagnostics.Select(d => d.Code).ToArray())
                .Contains("zones.overlay.malformed");

            // Published to the hub on the first read, and from there onto the Workbench's rows.
            await Assert.That(ZoneOverlayDiagnostics.OverlayPath).IsEqualTo(overlay);
            await Assert.That(ZoneOverlayDiagnostics.Current.Select(d => d.Code).ToArray())
                .Contains("zones.overlay.malformed");

            Dispatcher.UIThread.RunJobs();
            WorkbenchDiagnostic? row = workbench.Diagnostics.FirstOrDefault(d => d.Code == "zones.overlay.malformed");
            await Assert.That(row).IsNotNull();
            await Assert.That(row!.File).IsEqualTo(overlay);
            await Assert.That(row.Location).IsEqualTo(Path.GetFileName(overlay));

            vm.OnDeactivated();
            vm.Dispose();
        });
    }

    [Test]
    public async Task ReloadZones_PicksUpAnEditedFile_WithoutAMapReload()
    {
        RequireBundle();
        using ConfigDir config = new();

        await HeadlessSession.RunOnUi(async () =>
        {
            ZoneOverlayDiagnostics.Reset();
            (Playback2DTabViewModel vm, _) = ActivatedTab();
            LoadedMapAsset? asset = vm.MapAsset;
            await Assert.That(asset).IsNotNull();

            PlaceResolver? before = vm.Zones;
            await Assert.That(before).IsNotNull();
            await Assert.That(before!.PlaceId("Sandwich")).IsNull();

            ConfigDir.WriteOverlay("""
                                {
                                  "schemaVersion": 1,
                                  "mapName": "de_nuke",
                                  "zones": [
                                    { "name": "Sandwich", "floor": -512,
                                      "polygon": [ -1600, -1900, -1200, -1900, -1200, -1500, -1600, -1500 ] }
                                  ]
                                }
                                """);

            vm.ReloadZonesCommand.Execute(null);

            PlaceResolver? after = vm.Zones;
            await Assert.That(after).IsNotNull();
            await Assert.That(ReferenceEquals(after, before)).IsFalse();
            await Assert.That(after!.PlaceId("Sandwich")).IsNotNull();
            await Assert.That(after.Zones.HasOverlay).IsTrue();
            await Assert.That(after.Zones.EffectiveVersion).IsNotEqualTo(before.Zones.EffectiveVersion);
            await Assert.That(after.Zones.Places.Single(p => p.Name == "Sandwich").Origin).IsEqualTo(PlaceOrigin.Custom);
            await Assert.That(ZoneOverlayDiagnostics.Current.Count).IsEqualTo(0);
            await Assert.That(vm.Status).Contains("Zones reloaded");

            // The map itself was not reloaded: same asset instance, same map name.
            await Assert.That(ReferenceEquals(vm.MapAsset, asset)).IsTrue();
            await Assert.That(vm.LoadedMapNameForTest).IsEqualTo(Map);

            vm.OnDeactivated();
            vm.Dispose();
        });
    }

    /// <summary>
    ///     The lease's own refresh, over a bundle the test owns rather than the process-wide registry: a
    ///     saved overlay is picked up, and an unchanged one hands back the same resolver instance.
    /// </summary>
    [Test]
    public async Task RefreshZones_RereadsASavedOverlay_AndIsTheSameInstanceWhenNothingMoved()
    {
        string bundleDir = RequireBundle();
        using ConfigDir config = new();
        string zonesDir = AppPaths.EnsureZonesDirectory()!;
        using TestMapAssetLease lease = new(MapAssetPipeline.TryLoadFromDirectory(bundleDir, zonesDir)!, Map);

        PlaceResolver? first = lease.Zones;
        await Assert.That(first).IsNotNull();
        using (Assert.Multiple())
        {
            await Assert.That(ReferenceEquals(lease.RefreshZones(), first)).IsTrue()
                .Because("the lazy read stamped the overlay, so the first refresh re-reads nothing");
            await Assert.That(ReferenceEquals(lease.RefreshZones(), first)).IsTrue();
            await Assert.That(first!.PlaceId("Sandwich")).IsNull();
        }

        ConfigDir.WriteOverlay("""
                            {
                              "schemaVersion": 1,
                              "mapName": "de_nuke",
                              "zones": [
                                { "name": "Sandwich", "floor": -512,
                                  "polygon": [ -1600, -1900, -1200, -1900, -1200, -1500, -1600, -1500 ] }
                              ]
                            }
                            """);

        PlaceResolver? second = lease.RefreshZones();
        await Assert.That(second).IsNotNull();
        using (Assert.Multiple())
        {
            await Assert.That(ReferenceEquals(second, first)).IsFalse().Because("the overlay was saved after the first read");
            await Assert.That(second!.PlaceId("Sandwich")).IsNotNull();
            await Assert.That(second.Zones.HasOverlay).IsTrue();
            await Assert.That(second.Zones.EffectiveVersion).IsNotEqualTo(first!.Zones.EffectiveVersion);
            await Assert.That(ReferenceEquals(lease.RefreshZones(), second)).IsTrue()
                .Because("an unchanged overlay is not re-read");
            await Assert.That(ReferenceEquals(lease.Zones, second)).IsTrue()
                .Because("the refresh published the new load, so a plain read sees it");
        }
    }

    // Each test disposes the tab it activates. The bundle registry is process-wide and keyed on the map
    // alone, so a hold left out would pin this test's config root for the next one's acquire.
    private static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx) ActivatedTab()
    {
        Playback2DFakeContext ctx = new()
        {
            MapName = Map,
            Gate = new FakeModuleFeatureGate()
        };
        ctx.AddPlayer(0, "Alpha", 2);
        Playback2DTabViewModel vm = new();
        vm.OnActivated(ctx);
        return (vm, ctx);
    }

    private static string RequireBundle()
    {
        string? dir = MapAssetBundleReader.FindBundleDirectory(Map);
        if (dir is null || ZoneAssetPipeline.TryReadBaked(dir) is null)
        {
            throw new SkipTestException($"no {Map} bundle with {ZoneAssetPipeline.FileName} in this checkout");
        }

        return dir;
    }

    /// <summary>A private config root for the test, through the same env seam AppCompositionRootTests uses.</summary>
    private sealed class ConfigDir : IDisposable
    {
        private readonly string? _previous;

        public ConfigDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dv-zones-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            _previous = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, Path);
        }

        public string Path { get; }

        public static string WriteOverlay(string json)
        {
            string dir = AppPaths.EnsureZonesDirectory()!;
            string file = System.IO.Path.Combine(dir, Map + ZoneAssetPipeline.OverlaySuffix);
            File.WriteAllText(file, json);
            return file;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, _previous);
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // a leftover temp directory is not a test failure
            }
        }
    }
}
