#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The annotation timeline track. Two things matter and both are easy to get silently wrong: the
///     markers sit on the FRAME-INDEX axis (A1 D5), and an element whose tick does not resolve is dropped
///     rather than drawn at frame 0.
/// </summary>
public class AnnotationTrackTests
{
    [Test]
    public async Task TrackId_IsABareWord_NotTheLayerOrFeatureId()
    {
        using AnnotationTrack track = new(new AnnotationDocument());

        await Assert.That(track.Id).IsEqualTo("annotation")
            .Because("A1's track ids are bare words; 'playback2d.annotations' is the layer id AND the " +
                     "feature id, and one string across three registries is a collision waiting to happen");
        await Assert.That(track.DisplayName).IsEqualTo("Annotations");
    }

    [Test]
    public async Task BuildMarkers_OneMarkerPerAnchoredElement_OnTheFrameIndexAxis()
    {
        AnnotationDocument doc = new();
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(time: new TimeEnvelope(640, 900, 0, 0)), 0));
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(time: new TimeEnvelope(1280, null, 4, 8)), 1));

        using AnnotationTrack track = new(doc);
        FakeTimelineData data = new();

        IReadOnlyList<TimelineMarker> markers = track.BuildMarkers(data);

        await Assert.That(markers.Count).IsEqualTo(2);
        await Assert.That(markers[0].FrameIndex).IsEqualTo(640 / 4);
        await Assert.That(markers[0].Tick).IsEqualTo(640);
        await Assert.That(markers[1].FrameIndex).IsEqualTo(1280 / 4);
        await Assert.That(markers[0].TrackId).IsEqualTo(AnnotationTrack.TrackId);
        await Assert.That(markers[0].Kind).IsEqualTo(TimelineMarkerKind.Annotation);
        await Assert.That(markers[0].Glyph).IsEqualTo("✎");
        await Assert.That(markers[0].Argb).IsEqualTo(0u).Because("a track never names a colour");
    }

    [Test]
    public async Task StaticElements_ProduceNoMarkers()
    {
        AnnotationDocument doc = new();
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));

        using AnnotationTrack track = new(doc);
        FakeTimelineData data = new();

        await Assert.That(track.BuildMarkers(data)).IsEmpty();
        await Assert.That(track.IsAvailable(data)).IsFalse();
    }

    [Test]
    public async Task UnresolvableTick_IsDropped_NotPlacedAtFrameZero()
    {
        AnnotationDocument doc = new();
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(time: new TimeEnvelope(9_999_999, null, 0, 0)),
            0));
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(time: new TimeEnvelope(400, null, 0, 0)), 1));

        using AnnotationTrack track = new(doc);
        IReadOnlyList<TimelineMarker> markers = track.BuildMarkers(new FakeTimelineData());

        await Assert.That(markers.Count).IsEqualTo(1);
        await Assert.That(markers[0].Tick).IsEqualTo(400);
    }

    [Test]
    public async Task DocumentChanged_RaisesMarkersChanged()
    {
        AnnotationDocument doc = new();
        using AnnotationTrack track = new(doc);

        int raised = 0;
        track.MarkersChanged += () => raised++;

        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(time: new TimeEnvelope(100, null, 0, 0)), 0));
        await Assert.That(raised).IsEqualTo(1);

        doc.Undo();
        await Assert.That(raised).IsEqualTo(2);
    }

    [Test]
    public async Task Dispose_StopsListening()
    {
        AnnotationDocument doc = new();
        AnnotationTrack track = new(doc);

        int raised = 0;
        track.MarkersChanged += () => raised++;
        track.Dispose();

        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(), 0));
        await Assert.That(raised).IsEqualTo(0);
    }

    [Test]
    public async Task IsAvailable_TrueOnceAnyElementIsAnchored()
    {
        AnnotationDocument doc = new();
        using AnnotationTrack track = new(doc);
        FakeTimelineData data = new();

        await Assert.That(track.IsAvailable(data)).IsFalse();
        doc.Apply(new DocDelta.Add(AnnotationFakes.Stroke(time: new TimeEnvelope(100, null, 0, 0)), 0));
        await Assert.That(track.IsAvailable(data)).IsTrue();
        await Assert.That(track.BuildBands(data)).IsEmpty();
    }

    /// <summary>A four-ticks-per-frame demo of 1000 frames. Ticks past the end resolve to -1.</summary>
    private sealed class FakeTimelineData : ITimelineData
    {
        public int TotalFrames => 1000;

        public int TickRate => 64;

        public int FrameIndexAtTick(int tick)
        {
            int frame = tick / 4;
            return frame >= 0 && frame < TotalFrames ? frame : -1;
        }

        public IReadOnlyList<int> FramesForEvent(string eventName) => [];

        public IReadOnlyList<TimelineEventRecord> EventsOfType(string eventName) => [];

        public bool HasEvent(string eventName) => false;
    }
}
