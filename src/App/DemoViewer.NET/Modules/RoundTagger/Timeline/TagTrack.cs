#region

using System.Globalization;
using System.Text;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.Modules.RoundTagger.Timeline;

/// <summary>
///     The Tag Store's instances on the 2D timeline (tag-store.md §3.11): one lane of bands plus one
///     marker per instance.
///     <para>
///         <b>One lane, merged.</b> <see cref="ITimelineTrack.BuildBands" /> must return non-overlapping
///         bands and tags overlap freely, so overlapping spans are merged into runs. A run of one instance
///         is labelled with its code and coloured by it; a run of several is labelled with the count and
///         left to the host's neutral colour. The per-instance marker at each start is what keeps a merged
///         instance findable. Per-code lanes would need a lane on <see cref="TimelineBand" />, a Core
///         change this track does not make (§4.6).
///     </para>
///     <para>
///         <b>Ticks convert once.</b> Instances carry frame-clock ticks; each is resolved through
///         <see cref="ITimelineData.FrameIndexAtTick" /> at build time and an instance whose start resolves
///         to -1 (past the end of this parse) is dropped, the <c>AnnotationTrack</c> rule. An end past the
///         parse is clamped to the last frame instead: the start is on screen, so the instance is real.
///     </para>
/// </summary>
public sealed class TagTrack : ITimelineTrack, IDisposable
{
    /// <summary>The track's stable id. A bare word like <c>round</c> and <c>annotation</c>; never renamed.</summary>
    public const string TrackId = "tag";

    /// <summary>The marker glyph: one flag per instance, at its start.</summary>
    public const string Glyph = "⚑";

    private const int MaxRunTooltipLines = 5;

    // Stand-in code colours until the Tag Palette supplies its button colours through CodeColour. Eight
    // opaque hues picked by a stable hash of the code, so a code keeps its colour across runs and demos:
    // string.GetHashCode is randomized per process.
    private static readonly uint[] _defaultColours =
    [
        0xFFE0A030, 0xFF4A90D9, 0xFF5AB05A, 0xFFB66CD8,
        0xFFE06060, 0xFF40B8B0, 0xFFD8C040, 0xFF8080E0
    ];

    private readonly Action<Action> _post;
    private readonly TagSession _session;

    private bool _disposed;
    private int _lastVersion;
    private int _pending;

    /// <summary>Creates a track over a session and subscribes to its changes.</summary>
    /// <param name="session">The session whose document's instances become bands and markers.</param>
    /// <param name="post">
    ///     Marshals the re-query onto the UI thread. <see cref="TagSession.Changed" /> may fire from the
    ///     thread pool after a save, and the timeline is UI-thread affine.
    /// </param>
    public TagTrack(TagSession session, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(post);
        _session = session;
        _post = post;
        _lastVersion = session.Version;
        _session.Changed += OnSessionChanged;
    }

    /// <summary>
    ///     A code's colour as ARGB, or null for the default. The Tag Palette sets this to its button
    ///     colours; until then, and for a code no palette names, <see cref="DefaultColour" /> decides.
    /// </summary>
    public Func<string, uint?>? CodeColour { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Changed -= OnSessionChanged;
    }

    /// <inheritdoc />
    public string Id => TrackId;

    /// <inheritdoc />
    public string DisplayName => "Tags";

    /// <inheritdoc />
    public event Action? MarkersChanged;

    /// <inheritdoc />
    public bool IsAvailable(ITimelineData data)
    {
        if (data is null || _session.Document is not { } document)
        {
            return false;
        }

        foreach (TagInstance instance in document.Instances)
        {
            if (data.FrameIndexAtTick(instance.FromTick) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data)
    {
        List<Span> spans = Spans(data);
        if (spans.Count == 0)
        {
            return Array.Empty<TimelineBand>();
        }

        List<TimelineBand> bands = new();
        int first = 0;
        int end = spans[0].End;
        for (int i = 1; i <= spans.Count; i++)
        {
            // Inclusive ranges: a span starting on the run's last frame overlaps it; one starting on the
            // next frame does not, and gets a band of its own.
            if (i < spans.Count && spans[i].Start <= end)
            {
                end = Math.Max(end, spans[i].End);
                continue;
            }

            bands.Add(Run(spans, first, i, end));
            if (i < spans.Count)
            {
                first = i;
                end = spans[i].End;
            }
        }

        return bands;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data)
    {
        List<Span> spans = Spans(data);
        if (spans.Count == 0)
        {
            return Array.Empty<TimelineMarker>();
        }

        List<TimelineMarker> markers = new(spans.Count);
        foreach (Span span in spans)
        {
            markers.Add(new TimelineMarker(TrackId, span.Start, span.Instance.FromTick, TimelineMarkerKind.Custom,
                Glyph, Tooltip(span.Instance), ColourOf(span.Instance.Code)));
        }

        return markers;
    }

    /// <summary>
    ///     The instances merged into the band that starts at <paramref name="startFrame" />, in the track's
    ///     order (by start, then by end), or none when no band starts there. A band click is how the Tag
    ///     Palette's Label Mode picks a tag, and a run of several is walked one click at a time.
    /// </summary>
    /// <param name="data">The timeline data the bands were built from.</param>
    /// <param name="startFrame">The band's first frame.</param>
    public IReadOnlyList<Guid> InstancesInRun(ITimelineData data, int startFrame)
    {
        List<Span> spans = Spans(data);
        if (spans.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        // The merge of BuildBands, so a run here is exactly a band there.
        int first = 0;
        int end = spans[0].End;
        for (int i = 1; i <= spans.Count; i++)
        {
            if (i < spans.Count && spans[i].Start <= end)
            {
                end = Math.Max(end, spans[i].End);
                continue;
            }

            if (spans[first].Start == startFrame)
            {
                return spans.GetRange(first, i - first).ConvertAll(s => s.Instance.Id);
            }

            if (i < spans.Count)
            {
                first = i;
                end = spans[i].End;
            }
        }

        return Array.Empty<Guid>();
    }

    /// <summary>The stand-in colour for a code: stable across processes, one of eight hues.</summary>
    /// <param name="code">The code.</param>
    public static uint DefaultColour(string code)
    {
        // FNV-1a over the UTF-16 code units.
        uint hash = 2166136261;
        foreach (char c in code ?? "")
        {
            hash = (hash ^ c) * 16777619;
        }

        return _defaultColours[hash % (uint)_defaultColours.Length];
    }

    /// <summary>The marker tooltip: <c>"{code} · {labels} · r{round}"</c>, empty parts left out.</summary>
    /// <param name="instance">The instance.</param>
    public static string Tooltip(TagInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        StringBuilder sb = new(instance.Code.Length == 0 ? "(no code)" : instance.Code);
        if (instance.Labels.Count > 0)
        {
            sb.Append(" · ");
            for (int i = 0; i < instance.Labels.Count; i++)
            {
                TagLabel label = instance.Labels[i];
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(label.Group.Length == 0 ? label.Value : $"{label.Group}: {label.Value}");
            }
        }

        if (instance.Round is { } round)
        {
            sb.Append(" · r").Append(round.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private uint ColourOf(string code) => CodeColour?.Invoke(code) ?? DefaultColour(code);

    private TimelineBand Run(List<Span> spans, int first, int endExclusive, int endFrame)
    {
        int count = endExclusive - first;
        TagInstance head = spans[first].Instance;
        if (count == 1)
        {
            return new TimelineBand(TrackId, spans[first].Start, endFrame, head.Code, Tooltip(head),
                ColourOf(head.Code));
        }

        StringBuilder sb = new();
        sb.Append(count.ToString(CultureInfo.InvariantCulture)).Append(" tags");
        int lines = Math.Min(count, MaxRunTooltipLines);
        for (int k = 0; k < lines; k++)
        {
            sb.Append('\n').Append(Tooltip(spans[first + k].Instance));
        }

        if (count > lines)
        {
            sb.Append("\n…");
        }

        return new TimelineBand(TrackId, spans[first].Start, endFrame,
            count.ToString(CultureInfo.InvariantCulture), sb.ToString(), 0u);
    }

    // Every instance resolved onto the frame axis, ascending by start and then by end, so the merge above
    // and the markers share one conversion and one order.
    private List<Span> Spans(ITimelineData data)
    {
        List<Span> spans = [];
        if (data is null || data.TotalFrames <= 0 || _session.Document is not { } document)
        {
            return spans;
        }

        int last = data.TotalFrames - 1;
        foreach (TagInstance instance in document.Instances)
        {
            int start = data.FrameIndexAtTick(instance.FromTick);
            if (start < 0)
            {
                continue; // past the end of this parse: a band at frame 0 would be a lie
            }

            int end = data.FrameIndexAtTick(instance.ToTick);
            end = end < 0 ? last : Math.Min(end, last);
            spans.Add(new Span(instance, start, Math.Max(start, end)));
        }

        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        return spans;
    }

    // Changed also fires for a status-line change after a save, which moves nothing on the timeline; only
    // a version bump does. Several bumps before the UI thread runs collapse into one re-query.
    private void OnSessionChanged()
    {
        int version = _session.Version;
        if (version == Volatile.Read(ref _lastVersion))
        {
            return;
        }

        Volatile.Write(ref _lastVersion, version);
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        _post(() =>
        {
            Volatile.Write(ref _pending, 0);
            if (!_disposed)
            {
                MarkersChanged?.Invoke();
            }
        });
    }

    private readonly record struct Span(TagInstance Instance, int Start, int End);
}
