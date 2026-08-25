#region

using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>Playback2D</c> annotation preferences: they bind from an empty file, they round-trip
///     through the real file, and — the one that actually bites — every one of them survives the
///     FILELESS in-memory path.
///     <para>
///         On WASM there is no settings file, only the provider <c>SettingsService.WriteInMemory</c>
///         populates by hand, key by key. A property modelled on <c>AppSettings</c> but missing from that
///         method binds fine, writes fine, and forgets itself on the next reload with nothing to see
///         anywhere. Annotations ARE WASM-reachable — in-session drawing works in the browser.
///     </para>
/// </summary>
[NotInParallel]
public class Playback2DAnnotationSettingsTests
{
    [Test]
    public async Task Playback2D_Section_BindsFromEmptyFile_WithDefaults()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
            SettingsService svc = new(dir);
            Playback2DSettings prefs = svc.Current.Playback2D;

            await Assert.That(prefs.LastTool).IsEqualTo("PanZoom");
            await Assert.That(prefs.AnnotationColorArgb).IsEqualTo(0xFFFFC107u);
            await Assert.That(prefs.AnnotationWidth).IsEqualTo(8d);
            await Assert.That(prefs.AnnotationOpacity).IsEqualTo(1d);
            await Assert.That(prefs.AnnotationDefaultVisibility).IsEqualTo("Always");
            await Assert.That(prefs.AnnotationFadeInTicks).IsEqualTo(8);
            await Assert.That(prefs.AnnotationFadeOutTicks).IsEqualTo(16);
            await Assert.That(prefs.AnnotationHoldTicks).IsEqualTo(320);
            await Assert.That(prefs.AnnotationAnchorToEntities).IsFalse();
            await Assert.That(prefs.AnnotationAutoSave).IsTrue();
            await Assert.That(prefs.AnnotationRecentColors).IsEmpty();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task Write_ThenRead_RoundTripsEveryKey()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            svc.Write(Mutate);

            SettingsService reader = new(dir);
            await AssertMutated(reader.Current.Playback2D);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The exact failure mode <c>SettingsService.WriteInMemory</c>'s own comment warns about, driven
    ///     through a fileless service so the branch is the WASM one.
    /// </summary>
    [Test]
    public async Task WriteInMemory_FlattensEveryPlayback2DKey()
    {
        SettingsService svc = new(null);
        svc.Write(Mutate);

        await AssertMutated(svc.Current.Playback2D);
    }

    [Test]
    public async Task WriteInMemory_ShrinkingRecentColors_DropsStaleIndices()
    {
        SettingsService svc = new(null);

        svc.Write(s => s.Playback2D.AnnotationRecentColors = ["#FFFF0000", "#FF00FF00", "#FF0000FF"]);
        await Assert.That(svc.Current.Playback2D.AnnotationRecentColors.Length).IsEqualTo(3);

        svc.Write(s => s.Playback2D.AnnotationRecentColors = ["#FFFF0000"]);

        await Assert.That(svc.Current.Playback2D.AnnotationRecentColors.Length).IsEqualTo(1)
            .Because("a shrunk array must not leave higher-index keys a later bind resurrects");
        await Assert.That(svc.Current.Playback2D.AnnotationRecentColors[0]).IsEqualTo("#FFFF0000");
    }

    private static void Mutate(AppSettings s)
    {
        s.Playback2D.LastTool = "Draw";
        s.Playback2D.AnnotationColorArgb = 0xC012345Fu;
        s.Playback2D.AnnotationWidth = 13.5;
        s.Playback2D.AnnotationOpacity = 0.6;
        s.Playback2D.AnnotationDefaultVisibility = "Fade";
        s.Playback2D.AnnotationFadeInTicks = 3;
        s.Playback2D.AnnotationFadeOutTicks = 4;
        s.Playback2D.AnnotationHoldTicks = 5;
        s.Playback2D.AnnotationAnchorToEntities = true;
        s.Playback2D.AnnotationAutoSave = false;
        s.Playback2D.AnnotationRecentColors = ["#FF112233", "#FF445566"];
    }

    private static async Task AssertMutated(Playback2DSettings prefs)
    {
        await Assert.That(prefs.LastTool).IsEqualTo("Draw");
        await Assert.That(prefs.AnnotationColorArgb).IsEqualTo(0xC012345Fu);
        await Assert.That(prefs.AnnotationWidth).IsEqualTo(13.5);
        await Assert.That(prefs.AnnotationOpacity).IsEqualTo(0.6);
        await Assert.That(prefs.AnnotationDefaultVisibility).IsEqualTo("Fade");
        await Assert.That(prefs.AnnotationFadeInTicks).IsEqualTo(3);
        await Assert.That(prefs.AnnotationFadeOutTicks).IsEqualTo(4);
        await Assert.That(prefs.AnnotationHoldTicks).IsEqualTo(5);
        await Assert.That(prefs.AnnotationAnchorToEntities).IsTrue();
        await Assert.That(prefs.AnnotationAutoSave).IsFalse();
        await Assert.That(prefs.AnnotationRecentColors.Length).IsEqualTo(2);
        await Assert.That(prefs.AnnotationRecentColors[1]).IsEqualTo("#FF445566");
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-p2d-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp dir that outlives the test is noise, not a failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
