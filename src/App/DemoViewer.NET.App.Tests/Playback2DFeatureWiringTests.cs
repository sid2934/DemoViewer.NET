#region

using DemoViewer.NET.Features;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A manual audit, encoded as a test. Guards the two failure modes a feature id has once it is in the catalog:
///     <b>declared but never consumed</b> (nothing gates on it), and
///     <b>consumed in the wrong assembly</b>: Core / Pipeline / <c>dv2d</c> must read
///     no gates at all, because a headless renderer takes explicit flags and must produce the same picture
///     whatever a user's Settings screen says.
///     <para>
///         Source scans, not reflection: a gate id is a string literal, and what needs proving is that
///         somebody typed it somewhere that is not the catalog.
///     </para>
/// </summary>
public class Playback2DFeatureWiringTests
{
    [Test]
    public async Task EveryPlayback2dId_IsReferencedInAppSources()
    {
        string appRoot = Path.Combine(RepoRoot(), "src", "App", "DemoViewer.NET");
        string catalogPath = Path.Combine(appRoot, "Features", "FeatureCatalog.cs");
        // The compiled-in extensions' production sources: a pack reads the ids of the tabs and
        // features it contributes. Their test and capture projects are not production and do not count.
        string extensionsRoot = Path.Combine(RepoRoot(), "src", "Extensions");

        string[] sources = Directory
            .EnumerateFiles(appRoot, "*.*", SearchOption.AllDirectories)
            .Concat(Directory.Exists(extensionsRoot)
                ? Directory.EnumerateFiles(extensionsRoot, "*.*", SearchOption.AllDirectories).Where(IsExtensionProductionSource)
                : [])
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Where(p => !string.Equals(p, catalogPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        List<string> unconsumed = new();
        foreach (string id in Playback2DFeatureCatalogTests.Ids)
        {
            bool found = sources.Any(p =>
                File.ReadAllText(p).Contains(id, StringComparison.Ordinal));
            if (!found)
            {
                unconsumed.Add(id);
            }
        }

        await Assert.That(string.Join(", ", unconsumed)).IsEqualTo("")
            .Because("a catalog row nothing reads is a switch wired to nothing — the user flips it and "
                     + "the app does not change");
    }

    // No build output, and no segment that names a test, test-support or capture project.
    private static bool IsExtensionProductionSource(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).All(segment =>
            segment is not ("bin" or "obj")
            && !segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            && !segment.EndsWith(".TestSupport", StringComparison.OrdinalIgnoreCase)
            && !segment.EndsWith(".UiCapture", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     The single <c>!OperatingSystem.IsBrowser()</c> site for module ids. Export is in it;
    ///     nothing else is, because nothing else needs a filesystem or a subprocess.
    /// </summary>
    [Test]
    public async Task ExportId_IsInDesktopOnlySet()
    {
        await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds.Contains("playback2d.export")).IsTrue();

        foreach (string id in Playback2DFeatureCatalogTests.Ids.Where(i => i != "playback2d.export"))
        {
            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds.Contains(id)).IsFalse()
                .Because($"{id} works identically on both hosts and must not acquire a platform AND");
        }
    }

    /// <summary>
    ///     The render core, the pipeline and the CLI take explicit flags, never gates. A
    ///     <c>dv2d render</c> whose output depended on the invoking user's Settings would be unusable as a
    ///     golden source.
    /// </summary>
    [Test]
    public async Task CoreAndPipelineAndCli_NeverReferenceFeatureGating()
    {
        string[] roots =
        [
            Path.Combine(RepoRoot(), "src", "Playback2D", "DemoViewer.NET.Playback2D.Scene"),
            Path.Combine(RepoRoot(), "src", "Playback2D", "DemoViewer.NET.Playback2D.Core"),
            Path.Combine(RepoRoot(), "src", "Playback2D", "DemoViewer.NET.Playback2D.Pipeline"),
            Path.Combine(RepoRoot(), "tools", "DemoViewer.NET.Playback2D.Cli")
        ];

        // "playback2d.annotations" is deliberately absent: the layer registry gives the ink LAYER that exact
        // id, so the literal legitimately appears in Core as SceneLayerIds.Annotations. The two registries
        // collide on one string by coincidence, which AnnotationTrack's own doc comment records: banning
        // the literal would flag the layer, not a gate. The GATING TYPES below are the real test: a
        // renderer that could read IFeatureGate is a renderer whose output depends on the caller's
        // Settings screen.
        string[] banned =
        [
            "IFeatureGate", "FeatureCatalog", "IModuleFeatureGate",
            "playback2d.timeline", "playback2d.levels.auto", "playback2d.follow", "playback2d.export"
        ];

        List<string> hits = new();
        foreach (string root in roots)
        {
            await Assert.That(Directory.Exists(root)).IsTrue().Because($"{root} should exist");

            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // obj/bin artefacts under the project dir are build output, not source.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                string text = File.ReadAllText(file);
                foreach (string token in banned.Where(t => text.Contains(t, StringComparison.Ordinal)))
                {
                    hits.Add($"{Path.GetFileName(file)} → {token}");
                }
            }
        }

        await Assert.That(string.Join("; ", hits)).IsEqualTo("")
            .Because("the CLI takes explicit flags instead of reading a user's feature gates");
    }

    /// <summary>
    ///     The one string that is BOTH a feature id and a layer id. Pinned because
    ///     the collision is easy to read as a mistake and "fix", and because the previous test has to
    ///     exempt it, so the exemption needs a reason that is itself under test. The annotation TIMELINE
    ///     track deliberately does not join the collision: its id is the bare word <c>annotation</c>.
    /// </summary>
    [Test]
    public async Task AnnotationsId_IsBothAFeatureIdAndALayerId_AndTheTrackIdIsNot()
    {
        await Assert.That(Playback2DFeatureCatalogTests.Ids.Contains(SceneLayerIds.Annotations,
                StringComparer.Ordinal)).IsTrue()
            .Because("the layer id and the feature id are deliberately the same string");

        await Assert.That(Playback2DFeatureCatalogTests.Ids.Contains(AnnotationTrack.TrackId,
                StringComparer.Ordinal)).IsFalse()
            .Because("the timeline track id is the bare word 'annotation' — three registries must not "
                     + "share one key");
    }

    private static string RepoRoot() =>
        DemoTestHelper.FindRepoRoot()
        ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");
}
