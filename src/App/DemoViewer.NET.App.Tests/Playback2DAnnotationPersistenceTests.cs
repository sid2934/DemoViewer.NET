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
    ///     Opening a demo must not litter a sidecar beside it. Loading raises the document's Changed,
    ///     which resets the element list, and without suppressing autosave there, every demo the user ever
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

        // The BLOCKING overload, which is what OnDeactivated calls: the shell deactivates the tab on
        // its way out of MainViewModel.Dispose, where a fire-and-forget write races the process exit.
        controller.Flush();

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

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock, false);

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

    /// <summary>
    ///     On the browser head the status line must NOT name a sidecar path. System.IO writes there land
    ///     in the WASM runtime's in-memory virtual FS, so the store finds a "writable" location, reports
    ///     it, and the user reads a filename as a promise the next reload breaks. Design §8: annotations
    ///     work in session, a reload loses them, and the UI says so.
    ///     <para>
    ///         Found by a WASM verification pass on the published head: with a demo attached, the
    ///         panel read "saving to /sample-de_nuke.dem.dvann.json" in a browser tab.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Controller_OnBrowser_SaysTheTabForgets_RatherThanNamingAPath()
    {
        using TempDemo demo = new();

        using AnnotationSessionController desktop =
            new(new AnnotationStore(demo.AppData), null, static () => false);
        await desktop.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Assert.That(desktop.StatusText).StartsWith("saving to ")
            .Because("a desktop user gets a real path they can find the file at");

        using AnnotationSessionController browser =
            new(new AnnotationStore(demo.AppData), null, static () => true);
        await browser.AttachDemoAsync(demo.DemoPath, demo.Clock);

        await Assert.That(browser.StatusText).DoesNotContain(".dvann.json");
        await Assert.That(browser.StatusText).Contains("session only");
        await Assert.That(browser.StatusText).Contains("reload");
    }

    /// <summary>
    ///     <b><c>AnnotationAutoSave</c> had a reader and no writer.</b> The key was honoured at runtime,
    ///     carried a <c>WriteInMemory</c> row, and nothing in the app could set it: a user who wanted
    ///     session-only ink had to hand-edit <c>settings.json</c>, and every reader only ever saw the
    ///     default. It now has the toolbar toggle and the writer.
    ///     <para>
    ///         The check also MOVED, from the schedule to <see cref="AnnotationSessionController" />'s save
    ///         itself: <c>FlushAsync</c> is called on a demo swap, on tab deactivation and at shutdown, and
    ///         it went straight past the schedule-time guard. "Session only" that still writes the sidecar
    ///         when you close the tab is not session only. It is the same file arriving at a moment the
    ///         user is even less likely to notice.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Controller_AutoSaveOff_WritesNothing_NotEvenOnFlush()
    {
        using TempDemo demo = new();
        SettingsService settings = new(demo.SettingsDir);
        settings.Write(s => s.Playback2D.AnnotationAutoSave = false);

        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), settings)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(20)
        };

        await Assert.That(controller.AutoSave).IsFalse()
            .Because("ApplySettings seeds it, so the persisted key reaches the live controller");

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));
        await Task.Delay(200);

        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse()
            .Because("the debounce must not arm at all");

        controller.Flush();

        Console.WriteLine($"[autosave-off] saves={controller.SaveCount} "
                          + $"sidecar={File.Exists(demo.SidecarPath)} status='{controller.StatusText}'");

        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse()
            .Because("the flush path — demo swap, deactivate, shutdown — has to honour it too, or the "
                     + "setting only delays the write it was asked to prevent");
        await Assert.That(controller.SaveCount).IsEqualTo(0);

        // And the status line stops promising a destination nothing is going to.
        await Assert.That(controller.StatusText).Contains("auto-save off");
    }

    /// <summary>
    ///     The other direction: the toggle WRITES the key. Previously nothing did, so the branch above
    ///     could only ever be reached by hand-editing the file.
    /// </summary>
    [Test]
    public async Task Controller_TogglingAutoSave_PersistsTheKey()
    {
        using TempDemo demo = new();
        SettingsService settings = new(demo.SettingsDir);

        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), settings)
        {
            StylePersistDelay = TimeSpan.Zero // write inline; the debounce is PersistSettings' own test
        };

        await Assert.That(settings.Current.Playback2D.AnnotationAutoSave).IsTrue();

        controller.AutoSave = false;
        controller.PersistSettings();

        Console.WriteLine("[autosave-write] persisted="
                          + settings.Current.Playback2D.AnnotationAutoSave);

        await Assert.That(settings.Current.Playback2D.AnnotationAutoSave).IsFalse()
            .Because("the key shipped with a reader, a WriteInMemory row, and no writer anywhere");

        // And it comes back on the next controller, which is what "persisted" has to mean.
        using AnnotationSessionController reopened = new(new AnnotationStore(demo.AppData), settings);
        await Assert.That(reopened.AutoSave).IsFalse();
    }

    /// <summary>
    ///     <c>CanAutoSave</c> drives the toggle's enabled state, and it has to be false wherever no
    ///     sidecar is possible: no demo, no store, or the browser head, whose "writable" path is a
    ///     virtual FS that dies with the tab. A checkbox offering to control saving where nothing can be
    ///     saved is this audit's own defect class one layer down.
    /// </summary>
    [Test]
    public async Task Controller_CanAutoSave_IsFalseWhereNoSidecarIsPossible()
    {
        using TempDemo demo = new();

        using AnnotationSessionController detached = new(new AnnotationStore(demo.AppData), null);
        await Assert.That(detached.CanAutoSave).IsFalse().Because("no demo is attached");

        using AnnotationSessionController storeless = new(null, null);
        await storeless.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Assert.That(storeless.CanAutoSave).IsFalse().Because("there is no store to write through");

        using AnnotationSessionController browser =
            new(new AnnotationStore(demo.AppData), null, static () => true);
        await browser.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Assert.That(browser.CanAutoSave).IsFalse()
            .Because("the browser head's writable path is an in-memory FS that dies with the tab");

        using AnnotationSessionController desktop =
            new(new AnnotationStore(demo.AppData), null, static () => false);
        await desktop.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Assert.That(desktop.CanAutoSave).IsTrue();
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
                Directory.Delete(_root, true);
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
