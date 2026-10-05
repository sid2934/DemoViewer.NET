#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using SkiaSharp;
using SdkP = DemoViewer.NET.Extensions.Sdk.Playback;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The map's places as an SDK extension reads them through <see cref="SdkPlaybackSurface" />: the place
///     names, and a standing place lookup by floor that answers what a press at the same point answers.
/// </summary>
[NotInParallel]
public class SdkPlaybackSurfaceMapInfoTests
{
    // Two floors: upper [-500, 100000) under key QuantizeZ(-500) = -512, lower [-2000, -500).
    private static readonly MapLevel Upper = new() { Id = MapSpace.IdForZMin(-500), Name = "upper", ZMin = -500, ZMax = 100_000 };
    private static readonly MapLevel Lower = new() { Id = MapSpace.IdForZMin(-2000), Name = "lower", ZMin = -2000, ZMax = -500 };

    // Hut is a 100-unit square at the origin on the upper floor, Ramp sits to its right; "Hut" is listed twice
    // and an unnamed place is listed once, as an overlay can produce.
    private static PlaceResolver Zones() => new(new ZoneSet("de_synthetic", "9f1c02aa", "075a27b3", null, 64,
        [new ZoneFloor(-512, -528, 100_000), new ZoneFloor(-2048, -100_000, -528)],
        [
            new ZonePlace(0, "Ramp", PlaceOrigin.Baked), new ZonePlace(1, "Hut", PlaceOrigin.Baked),
            new ZonePlace(2, "Hut", PlaceOrigin.Custom), new ZonePlace(3, "", PlaceOrigin.Baked)
        ],
        [],
        [
            new ZoneArea(1, 1, -512, true, -400, [0, 0, 100, 0, 100, 100, 0, 100]),
            new ZoneArea(2, 0, -512, true, -400, [100, 0, 200, 0, 200, 100, 100, 100])
        ],
        [(1, 2)], [(0, 1)], null));

    private static (Playback2DTabViewModel Vm, Playback2DSurface Surface) Surface(IReadOnlyList<MapLevel> levels,
        PlaceResolver? zones)
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        return (vm, new Playback2DSurface(vm.Timeline, () => levels, _ => true, () => Scene2DFrame.Empty, () => zones));
    }

    [Test]
    public async Task Places_AreTheMapsNamesDistinctAndOrdered_AndEmptyWithoutZones()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface([Lower, Upper], Zones());
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        (Playback2DTabViewModel bareVm, Playback2DSurface bare) = Surface([Lower, Upper], null);
        using SdkPlaybackSurface none = new(bare, new FaultRig().Guard);

        using (Assert.Multiple())
        {
            await Assert.That(surface.Places).IsEquivalentTo(["Hut", "Ramp"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(none.Places).IsEmpty();
        }

        vm.Dispose();
        bareVm.Dispose();
    }

    [Test]
    public async Task PlaceAt_ResolvesOnTheNamedFloorOnly()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface([Lower, Upper], Zones());
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);

        using (Assert.Multiple())
        {
            await Assert.That(surface.PlaceAt("upper", 50, 50)).IsEqualTo("Hut");
            await Assert.That(surface.PlaceAt("upper", 150, 50)).IsEqualTo("Ramp");
            await Assert.That(surface.PlaceAt("lower", 50, 50)).IsNull().Because("the lower floor has no areas");
            await Assert.That(surface.PlaceAt("upper", 5_000, 5_000)).IsNull().Because("nothing is in snapping distance");
            await Assert.That(surface.PlaceAt("attic", 50, 50)).IsNull().Because("no floor has that name");
            await Assert.That(surface.PlaceAt(null, 50, 50)).IsNull().Because("a point on a two-floor map needs a floor");
        }

        vm.Dispose();
    }

    [Test]
    public async Task PlaceAt_WithNoFloorNamed_UsesTheOnlyFloor_AndAnswersNullWithoutZonesOrFloors()
    {
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface([Upper], Zones());
        using SdkPlaybackSurface single = new(inner, new FaultRig().Guard);
        (Playback2DTabViewModel bareVm, Playback2DSurface bare) = Surface([Upper], null);
        using SdkPlaybackSurface noZones = new(bare, new FaultRig().Guard);
        (Playback2DTabViewModel emptyVm, Playback2DSurface empty) = Surface([], Zones());
        using SdkPlaybackSurface noFloors = new(empty, new FaultRig().Guard);

        using (Assert.Multiple())
        {
            await Assert.That(single.PlaceAt(null, 50, 50)).IsEqualTo("Hut");
            await Assert.That(noZones.PlaceAt("upper", 50, 50)).IsNull();
            await Assert.That(noFloors.PlaceAt(null, 50, 50)).IsNull();
        }

        vm.Dispose();
        bareVm.Dispose();
        emptyVm.Dispose();
    }

    [Test]
    public async Task PlaceAt_AnswersWhatAPressAtTheSamePointAnswers()
    {
        PlaceResolver zones = Zones();
        (Playback2DTabViewModel vm, Playback2DSurface inner) = Surface([Lower, Upper], zones);
        using SdkPlaybackSurface surface = new(inner, new FaultRig().Guard);
        List<string?> pressed = [];
        using IDisposable handler = surface.AddPointerPreHandler(p =>
        {
            pressed.Add(p.PlaceAt());
            return false;
        });

        (MapLevel Level, double X, double Y)[] points = [(Upper, 50, 50), (Upper, 150, 50), (Lower, 50, 50), (Upper, 900, 900)];
        foreach ((MapLevel level, double x, double y) in points)
        {
            inner.TryHandlePointerPress(new ScenePointer(level, x, y, SKPoint.Empty, ToolModifiers.None, Scene2DFrame.Empty,
                () => zones));
        }

        string?[] looked = [.. points.Select(p => surface.PlaceAt(p.Level.Name, p.X, p.Y))];

        await Assert.That(pressed).IsEquivalentTo(looked, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(looked[0]).IsEqualTo("Hut");

        vm.Dispose();
    }
}
