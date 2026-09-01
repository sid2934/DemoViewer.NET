#region

using System.Globalization;
using System.Reflection;
using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The mechanical guarantee that no <c>Playback2DSettings</c> property can be added without a matching
///     <c>SettingsService.WriteInMemory</c> row (B5-3 / B5-6).
///     <para>
///         On the WASM head there is no settings file, only the in-memory configuration provider that
///         <c>WriteInMemory</c> populates by hand, key by key. A property modelled on
///         <see cref="Playback2DSettings" /> but missing from that method binds fine, writes fine, and
///         forgets itself on the next read, with nothing to see anywhere. This class is
///         <b>reflection-driven</b> so it covers properties a later phase adds without anyone remembering
///         to extend it; the fileless path is selected with <c>new SettingsService(null)</c>, the same
///         branch <c>SettingsServiceTests.WriteInMemory_ShrinkAndRemove_DropStaleKeys</c> exercises.
///     </para>
///     <para>
///         Black box on purpose: nothing here reaches into <c>SettingsService</c>'s private surface, so the
///         test still holds if the flattening is rewritten.
///     </para>
/// </summary>
[NotInParallel]
public class SettingsWasmRoundTripTests
{
    /// <summary>
    ///     Every public settable scalar of <see cref="Playback2DSettings" /> survives a fileless write.
    ///     A property with no <c>WriteInMemory</c> row fails here, naming itself.
    /// </summary>
    [Test]
    public async Task EveryPlayback2dProperty_SurvivesAFilelessWrite()
    {
        PropertyInfo[] properties = typeof(Playback2DSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

        await Assert.That(properties.Length).IsGreaterThan(15)
            .Because("the section should carry the registry §3.10 property set, not a stub");

        List<string> unpersisted = new();

        foreach (PropertyInfo property in properties)
        {
            SettingsService svc = new(null); // fileless in-memory path, the WASM branch
            object? original = property.GetValue(svc.Current.Playback2D);
            object? changed = NonDefaultFor(property, original);
            if (changed is null)
            {
                // An array property is covered by its own case below; nothing else should land here.
                continue;
            }

            svc.Write(s => property.SetValue(s.Playback2D, changed));

            object? readBack = property.GetValue(svc.Current.Playback2D);
            if (!Equals(readBack, changed))
            {
                unpersisted.Add(
                    $"{property.Name} (wrote {Describe(changed)}, read back {Describe(readBack)})");
            }
        }

        await Assert.That(string.Join(", ", unpersisted)).IsEqualTo("")
            .Because("every Playback2D property needs a SettingsService.WriteInMemory row, or the "
                     + "setting silently forgets itself on the browser head");
    }

    /// <summary>
    ///     The one array-shaped property: recent ink colours are flattened as indexed keys, and a SHRINK
    ///     must drop the stale indices rather than leave a longer array behind.
    /// </summary>
    [Test]
    public async Task RecentColors_RoundTrip_AndShrinkDropsStaleIndices()
    {
        SettingsService svc = new(null);

        svc.Write(s => s.Playback2D.AnnotationRecentColors = ["#FFFF0000", "#FF00FF00", "#FF0000FF"]);
        await Assert.That(svc.Current.Playback2D.AnnotationRecentColors.Length).IsEqualTo(3);
        await Assert.That(svc.Current.Playback2D.AnnotationRecentColors[1]).IsEqualTo("#FF00FF00");

        svc.Write(s => s.Playback2D.AnnotationRecentColors = ["#FFFF0000"]);
        await Assert.That(svc.Current.Playback2D.AnnotationRecentColors.Length).IsEqualTo(1)
            .Because("the ReplaceAll rebuild must drop AnnotationRecentColors:1 and :2");
    }

    /// <summary>
    ///     An EMPTY string round-trips as empty, not as a missing key that re-binds to the type default.
    ///     <c>ExportOutputDirectory</c> uses <c>""</c> for "no folder chosen" (B4 shipped it non-nullable),
    ///     so "" is a real value the flattening has to carry, not an omission.
    /// </summary>
    [Test]
    public async Task EmptyStringProperty_RoundTripsAsEmpty_NotAsTheDefault()
    {
        SettingsService svc = new(null);

        svc.Write(s => s.Playback2D.ExportOutputDirectory = "/tmp/exports");
        await Assert.That(svc.Current.Playback2D.ExportOutputDirectory).IsEqualTo("/tmp/exports");

        svc.Write(s => s.Playback2D.ExportOutputDirectory = "");
        await Assert.That(svc.Current.Playback2D.ExportOutputDirectory).IsEqualTo("");
    }

    /// <summary>
    ///     Regression net over the pre-existing flattened set, the root keys and the processing-queue
    ///     block, so a future edit to <c>WriteInMemory</c> cannot drop them while adding Playback2D rows.
    /// </summary>
    [Test]
    public async Task RootAndProcessingQueue_StillRoundTrip()
    {
        SettingsService svc = new(null);

        svc.Write(s =>
        {
            s.Theme = "Light";
            s.UserCategory = UserCategory.Developer;
            s.FirstRunCompleted = true;
            s.LastSeenVersion = "9.9.9";
            s.Features.DeveloperMode = true;
            s.ProcessingQueue.BackgroundProcessingEnabled = true;
            s.ProcessingQueue.MaxQueueSize = 42;
            s.ProcessingQueue.MaxConcurrency = 3;
        });

        await Assert.That(svc.Current.Theme).IsEqualTo("Light");
        await Assert.That(svc.Current.UserCategory).IsEqualTo(UserCategory.Developer);
        await Assert.That(svc.Current.FirstRunCompleted).IsTrue();
        await Assert.That(svc.Current.LastSeenVersion).IsEqualTo("9.9.9");
        await Assert.That(svc.Current.Features.DeveloperMode).IsTrue();
        await Assert.That(svc.Current.ProcessingQueue.BackgroundProcessingEnabled).IsTrue();
        await Assert.That(svc.Current.ProcessingQueue.MaxQueueSize).IsEqualTo(42);
        await Assert.That(svc.Current.ProcessingQueue.MaxConcurrency).IsEqualTo(3);
    }

    // A value of the property's type that is NOT its current one, so a dropped key shows up as the
    // default coming back instead of what was written. Arrays return null (covered by their own case).
    private static object? NonDefaultFor(PropertyInfo property, object? current) =>
        property.PropertyType switch
        {
            Type t when t == typeof(bool) => !(bool)(current ?? false),
            Type t when t == typeof(int) => (int)(current ?? 0) + 7,
            Type t when t == typeof(uint) => (uint)(current ?? 0u) ^ 0x00123456u,
            Type t when t == typeof(double) => (double)(current ?? 0d) + 1.5,
            Type t when t == typeof(string) => "dv-b5-" + property.Name,
            _ => null
        };

    private static string Describe(object? value) =>
        value is null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?";
}
