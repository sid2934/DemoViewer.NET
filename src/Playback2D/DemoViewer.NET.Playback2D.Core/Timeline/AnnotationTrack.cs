#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Timeline;

/// <summary>
///     Puts one timeline marker on the scrub bar per time-anchored annotation, so a telestration made at
///     a moment is findable from the timeline rather than only by scrubbing until it appears.
///     <para>
///         <b>Markers live on the FRAME-INDEX axis</b> (A1 decision D5, design §5.6: "frame index is the
///         movement contract"). An element's <c>FromTick</c> is converted exactly once through
///         <see cref="ITimelineData.FrameIndexAtTick" />, and an element whose tick resolves to -1, a
///         stroke anchored past the end of this parse, is DROPPED rather than silently drawn at frame 0.
///     </para>
///     <para>
///         B2 ships the markers; B3 adds drag-to-edit on top of them using the
///         <see cref="DocDelta.Replace" /> API this phase exports (design open question 3, resolved).
///     </para>
/// </summary>
public sealed class AnnotationTrack : ITimelineTrack, IDisposable
{
    /// <summary>
    ///     The track's stable id. A bare word like A1's <c>round</c>/<c>kill</c>/<c>bomb</c>: the string
    ///     <c>playback2d.annotations</c> is the LAYER id and the FEATURE id, and reusing it here would
    ///     make three different registries share one key.
    /// </summary>
    public const string TrackId = "annotation";

    private static readonly IReadOnlyList<TimelineBand> _noBands = [];
    private readonly AnnotationDocument _document;

    private readonly List<TimelineMarker> _markers = [];

    private bool _disposed;

    /// <summary>Creates a track over a document and subscribes to its changes.</summary>
    /// <param name="document">The document whose anchored elements become markers.</param>
    public AnnotationTrack(AnnotationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _document.Changed += OnDocumentChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _document.Changed -= OnDocumentChanged;
    }

    /// <inheritdoc />
    public string Id => TrackId;

    /// <inheritdoc />
    public string DisplayName => "Annotations";

    /// <inheritdoc />
    public event Action? MarkersChanged;

    /// <inheritdoc />
    public bool IsAvailable(ITimelineData data)
    {
        IReadOnlyList<AnnotationElement> elements = _document.Elements;
        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i].Time.FromTick.HasValue)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        _markers.Clear();

        IReadOnlyList<AnnotationElement> elements = _document.Elements;
        for (int i = 0; i < elements.Count; i++)
        {
            AnnotationElement element = elements[i];
            if (element.Time.FromTick is not { } tick)
            {
                continue;
            }

            int frameIndex = data.FrameIndexAtTick(tick);
            if (frameIndex < 0)
            {
                continue; // anchored past this parse: a marker at frame 0 would be a lie
            }

            _markers.Add(new TimelineMarker(TrackId, frameIndex, tick, TimelineMarkerKind.Annotation,
                "✎", Tooltip(element), 0u));
        }

        _markers.Sort(static (a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        return _markers;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => _noBands;

    private void OnDocumentChanged() => MarkersChanged?.Invoke();

    private static string Tooltip(AnnotationElement element)
    {
        TimeEnvelope envelope = element.Time;
        string window = envelope.UntilTick is { } until
            ? string.Create(CultureInfo.InvariantCulture, $"{envelope.FromTick} → {until}")
            : string.Create(CultureInfo.InvariantCulture, $"from {envelope.FromTick}");

        string fades = envelope.FadeInTicks > 0 || envelope.FadeOutTicks > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $" · fade {envelope.FadeInTicks}/{envelope.FadeOutTicks}")
            : "";

        return string.Create(CultureInfo.InvariantCulture, $"{element.Kind} · {window}{fades}");
    }
}
