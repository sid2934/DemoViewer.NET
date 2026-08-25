#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The session controller: load on attach, debounced autosave, flush on deactivate, and the claim
///     that a gated-off feature touches no disk at all.
/// </summary>
[NotInParallel]
public class Playback2DAnnotationPersistenceTests
{
    [Test]
    public async Task Controller_LoadsOnDemoAttach()
    {
        using TempDemo demo = new();
        AnnotationStore store = new(demo.AppData);
        AnnotationElement seeded = Stroke();
        await store.SaveAsync(demo.DemoPath, AnnotationStore.IdentityFor(demo.DemoPath), demo.Clock, [seeded]);

        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null);
        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);

        await Assert.That(controller.Document.Elements.Count).IsEqualTo(1);
        await Assert.That(controller.Document.Elements[0]).IsEqualTo(seeded);
        await Assert.That(controller.Document.UndoDepth).IsEqualTo(0)
            .Because("loading is not an action the user can undo into the previous demo's ink");
    }

    /// <summary>
    ///     Opening a demo must not litter a sidecar beside it. Loading raises the document's Changed —
    ///     it resets the element list — and without suppressing autosave there, every demo the user ever
    ///     opened would grow an empty <c>.dvann.json</c> next to it. Caught by a stray file appearing in
    ///     the repo's own <c>assets/tour/</c> after a test run.
    /// </summary>
    [Test]
    public async Task Controller_AttachingToADemoWithNoAnnotations_WritesNothing()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(20)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Task.Delay(200);
        await controller.FlushAsync();

        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse();
        await Assert.That(controller.SaveCount).IsEqualTo(0);
    }

    /// <summary>
    ///     ...but erasing the LAST stroke must still rewrite an existing sidecar. "Nothing to save" is
    ///     only true when there is also nothing on disk to correct.
    /// </summary>
    [Test]
    public async Task Controller_ErasingTheLastStroke_StillClearsAnExistingSidecar()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(20)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        AnnotationElement stroke = Stroke();
        controller.Document.Apply(new DocDelta.Add(stroke, 0));
        await controller.FlushAsync();
        await Assert.That(File.Exists(demo.SidecarPath)).IsTrue();

        controller.Document.Apply(new DocDelta.Remove(stroke.Id));
        await controller.FlushAsync();

        AnnotationStore reader = new(demo.AppData);
        AnnotationLoadResult loaded = await reader.LoadAsync(demo.DemoPath, demo.Clock);
        await Assert.That(loaded.Elements).IsEmpty();
    }

    [Test]
    public async Task Controller_AutosavesAfterDebounce()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(30)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));

        await WaitFor(() => controller.SaveCount > 0, TimeSpan.FromSeconds(5));

        await Assert.That(File.Exists(demo.SidecarPath)).IsTrue();

        AnnotationStore reader = new(demo.AppData);
        AnnotationLoadResult loaded = await reader.LoadAsync(demo.DemoPath, demo.Clock);
        await Assert.That(loaded.Elements.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     The debounce is the point: a stroke and a drag-erase both commit one document change, but a
    ///     rapid burst must still cost ONE write rather than one per delta.
    /// </summary>
    [Test]
    public async Task Controller_CoalescesABurstIntoOneSave()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(120)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        for (int i = 0; i < 25; i++)
        {
            controller.Document.Apply(new DocDelta.Add(Stroke(), i));
        }

        await WaitFor(() => controller.SaveCount > 0, TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        await Assert.That(controller.SaveCount).IsEqualTo(1);
    }

    [Test]
    public async Task Controller_FlushesOnDeactivate()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            // Long enough that the debounce would NOT have fired on its own.
            AutoSaveDelay = TimeSpan.FromSeconds(30)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));
        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse();

        await controller.FlushAsync();

        await Assert.That(File.Exists(demo.SidecarPath)).IsTrue()
            .Because("a debounced autosave that had not fired yet is the difference between a stroke " +
                     "surviving a tab switch and vanishing");
    }

    [Test]
    public async Task Controller_GateOff_NeverTouchesDisk()
    {
        using TempDemo demo = new();
        FakeModuleFeatureGate gate = new();
        gate.SetEnabled(AnnotationSessionController.FeatureId, false);

        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(20)
        };
        controller.SetFeatures(gate);

        await Assert.That(controller.IsEnabled).IsFalse();

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));
        await Task.Delay(200);
        await controller.FlushAsync();

        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse();
        await Assert.That(controller.SaveCount).IsEqualTo(0);
    }

    [Test]
    public async Task Controller_ReattachToTheSameDemo_KeepsTheInMemoryDocument()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromSeconds(30)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock, force: false);

        await Assert.That(controller.Document.Elements.Count).IsEqualTo(1)
            .Because("a tab RE-activation must not throw away what has not been autosaved yet");
    }

    [Test]
    public async Task Controller_SeedsStyleFromSettings()
    {
        using TempDemo demo = new();
        SettingsService settings = new(demo.SettingsDir);
        settings.Write(s =>
        {
            s.Playback2D.AnnotationColorArgb = 0xFF00FF00;
            s.Playback2D.AnnotationWidth = 21;
            s.Playback2D.AnnotationDefaultVisibility = "Fade";
            s.Playback2D.AnnotationAnchorToEntities = true;
        });

        using AnnotationSessionController controller = new(null, settings);

        await Assert.That(controller.Session.Style.ColorArgb).IsEqualTo(0xFF00FF00u);
        await Assert.That(controller.Session.Style.WidthWorld).IsEqualTo(21f);
        await Assert.That(controller.Session.DefaultVisibility).IsEqualTo(EnvelopeMode.Fade);
        await Assert.That(controller.Session.AnchorToEntities).IsTrue();
    }

    private static AnnotationElement Stroke() =>
        new(Guid.NewGuid(), AnnotationKind.Freehand, AnnotationStyle.Default, new SpaceRef.World(0),
            TimeEnvelope.Static,
            [new InkPoint(0, 0, 0.5f), new InkPoint(40, 10, 0.5f), new InkPoint(80, 0, 0.5f)], null);

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(15);
        }
    }

    private sealed class TempDemo : IDisposable
    {
        private readonly string _root;

        public TempDemo()
        {
            _root = Path.Combine(Path.GetTempPath(), "dv-p2d-ann-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "demos"));
            AppData = Path.Combine(_root, "appdata");
            SettingsDir = Path.Combine(_root, "settings");
            Directory.CreateDirectory(AppData);
            Directory.CreateDirectory(SettingsDir);

            DemoPath = Path.Combine(_root, "demos", "match.dem");
            File.WriteAllBytes(DemoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00]);
            SidecarPath = DemoPath + AnnotationStore.SidecarExtension;
            Clock = new ClockIdentity(ClockIdentity.DvFrameClock, 64, 1000, 0, 0);
        }

        public string AppData { get; }

        public string SettingsDir { get; }

        public string DemoPath { get; }

        public string SidecarPath { get; }

        public ClockIdentity Clock { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp tree that outlives the test is noise, not a failure.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
