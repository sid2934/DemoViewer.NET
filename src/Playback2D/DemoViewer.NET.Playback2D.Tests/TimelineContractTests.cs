#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The successor to <c>TimelineCoreCleanTests</c>: the timeline contract shipped Core-clean
///     under an architecture test precisely so a later move into Core would be a namespace
///     rewrite; that test's job is done, so it is deleted and this one takes over, asserting that the
///     seven declared members now live in Core with their signatures unchanged.
///     <para>
///         The "land it in Pipeline instead" fallback did <b>not</b> fire: <c>ITimelineData</c>
///         reaches only BCL types, so <c>ArchitectureTests.Core_ReferencesOnlySkiaSharpAndBcl</c> stays
///         green with it in Core.
///     </para>
/// </summary>
public class TimelineContractTests
{
    [Test]
    public async Task Contract_LivesInCore()
    {
        string core = typeof(Scene2DFrame).Assembly.GetName().Name!;

        foreach (Type t in (Type[])
                 [
                     typeof(ITimelineTrack), typeof(ITimelineData), typeof(TimelineMarker),
                     typeof(TimelineBand), typeof(TimelineEventRecord), typeof(TimelineEventKeys),
                     typeof(TimelineMarkerKind)
                 ])
        {
            await Assert.That(t.Assembly.GetName().Name).IsEqualTo(core);
            await Assert.That(t.Namespace).IsEqualTo("DemoViewer.NET.Playback2D.Core.Timeline");
        }
    }

    /// <summary>
    ///     The six members this interface freezes. Every implementer ships all six, so a silent addition
    ///     here breaks <c>AnnotationTrack</c> and every other track at once.
    /// </summary>
    [Test]
    public async Task ITimelineTrack_HasExactlySixMembers()
    {
        // Accessor methods (get_/add_/remove_) are the same six members seen through reflection's
        // lower-level view, so they are filtered out rather than enumerated.
        string[] declared = typeof(ITimelineTrack).GetMembers()
            .Where(m => !m.Name.StartsWith("get_", StringComparison.Ordinal)
                        && !m.Name.StartsWith("add_", StringComparison.Ordinal)
                        && !m.Name.StartsWith("remove_", StringComparison.Ordinal))
            .Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        await Assert.That(declared).IsEquivalentTo(
        [
            "BuildBands", "BuildMarkers", "DisplayName", "Id", "IsAvailable", "MarkersChanged"
        ]);
    }

    /// <summary>
    ///     The x-axis domain is FRAME INDEX. <c>TimelineMarker</c> carries the tick too, but
    ///     a consumer that lays out on <c>Tick</c> is drawing on the wrong axis.
    /// </summary>
    [Test]
    public async Task TimelineMarker_CarriesBothAxes()
    {
        TimelineMarker marker = new("round", 120, 7680, TimelineMarkerKind.Round, "", "", 0);
        await Assert.That(marker.FrameIndex).IsEqualTo(120);
        await Assert.That(marker.Tick).IsEqualTo(7680);
    }
}
