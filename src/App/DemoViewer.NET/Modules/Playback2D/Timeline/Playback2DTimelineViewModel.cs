#region

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Playback2D.Core.Timeline;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>
///     The 2D playback timeline's view-model: registered <see cref="ITimelineTrack" />s, their built bands
///     and markers, the layout math, and the playhead. It never moves the clock: a click or drag raises
///     <see cref="SeekRequested" /> and the owning tab forwards it to
///     <c>IModuleContext.RequestSeekToFrame</c>, so LiveSync keeps observing every seek.
///     <para>
///         The x-axis domain is FRAME INDEX, the movement contract everything else in the app already
///         speaks; tick-stamped events are converted once at build time by the adapter.
///     </para>
/// </summary>
public sealed partial class Playback2DTimelineViewModel : ObservableObject, IDisposable
{
    // Two markers of one track closer than this are folded into a single visual whose tooltip carries the
    // count. Without it a 90k-frame demo realizes ~400 glyphs onto a ~600 px bar.
    private const double MarkerCoalescePixels = 2.0;

    private const int MaxFoldedTooltipLines = 5;

    private readonly List<TimelineBand> _builtBands = new();
    private readonly List<TimelineMarker> _builtMarkers = new();
    private readonly List<TimelineTrackToggle> _toggles = new();

    // Per-track content, so a track that says "re-query me" costs one track's build instead of every
    // track's. Kept parallel to _tracks/_toggles, and recombined in registration order. That is display
    // order, and the rounds band's binary search depends on it staying ascending.
    private readonly List<List<TimelineBand>> _trackBands = new();
    private readonly List<Action> _trackHandlers = new();
    private readonly List<List<TimelineMarker>> _trackMarkers = new();
    private readonly List<ITimelineTrack> _tracks = new();

    [ObservableProperty]
    private int _currentFrameIndex = -1;

    [ObservableProperty]
    private string _currentRoundLabel = "";

    [ObservableProperty]
    private int _currentTick;

    private ITimelineData? _data;

    private bool _disposed;

    [ObservableProperty]
    private string _followStatus = "";

    /// <summary>
    ///     Reserved for the CS2 ghost cursor. Always null: the LiveSync tick projection it needs is a
    ///     contract change of its own.
    /// </summary>
    [ObservableProperty]
    private int? _ghostFrameIndex;

    [ObservableProperty]
    private string _hoverText = "";

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private double _pixelWidth;

    [ObservableProperty]
    private double _playheadX;

    [ObservableProperty]
    private string _positionText = "";

    /// <summary>Why a speed key was refused, or "". Mirrored from the tab so the footer can say it.</summary>
    [ObservableProperty]
    private string _speedLockNote = "";

    [ObservableProperty]
    private string _statusText = "";

    private bool _suppressTrackVisibilityChanged;

    [ObservableProperty]
    private int _totalFrames;

    /// <summary>The registered tracks' toggles, in registration order (which is display order).</summary>
    public IReadOnlyList<TimelineTrackToggle> Tracks => _toggles;

    /// <summary>The laid-out round bands. Rebuilt on <see cref="Rebuild" /> and on a width change.</summary>
    public ObservableCollection<TimelineBandViewModel> Bands { get; } = new();

    /// <summary>The laid-out point markers, after same-track coalescing.</summary>
    public ObservableCollection<TimelineMarkerViewModel> Markers { get; } = new();

    /// <summary>The playhead's left offset as a margin. The item layer positions by margin, not by Canvas.</summary>
    public Thickness PlayheadOffset => new(PlayheadX, 0, 0, 0);

    /// <summary>
    ///     Drops the <see cref="ITimelineTrack.MarkersChanged" /> subscriptions taken in
    ///     <see cref="RegisterTrack" />. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _trackHandlers.Count; i++)
        {
            _tracks[i].MarkersChanged -= _trackHandlers[i];
        }

        _trackHandlers.Clear();
    }

    /// <summary>
    ///     Raised on click / drag-scrub with the target frame index. The owner forwards it to
    ///     <c>IModuleContext.RequestSeekToFrame</c>. The timeline never moves the clock itself.
    /// </summary>
    public event Action<int>? SeekRequested;

    /// <summary>
    ///     Registers a track. Registration order is display order; re-registering an id is ignored.
    ///     <para>
    ///         Subscribes to <see cref="ITimelineTrack.MarkersChanged" />, which the interface documents as
    ///         "the host must re-query it". <see cref="Rebuild" /> runs only on activation and demo-reset,
    ///         so without this a track whose content grows while the demo sits still
    ///         (<c>AnnotationTrack</c>, on every stroke) is never re-queried, and its toggle never becomes
    ///         available either: availability is only evaluated inside a build.
    ///     </para>
    /// </summary>
    /// <param name="track">The track.</param>
    public void RegisterTrack(ITimelineTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        foreach (TimelineTrackToggle existing in _toggles)
        {
            if (string.Equals(existing.Id, track.Id, StringComparison.Ordinal))
            {
                return;
            }
        }

        TimelineTrackToggle toggle = new(track.Id, track.DisplayName);
        toggle.PropertyChanged += OnToggleChanged;
        _tracks.Add(track);
        _toggles.Add(toggle);
        _trackBands.Add([]);
        _trackMarkers.Add([]);

        // Captured so Dispose can take it back off: an anonymous lambda cannot be unsubscribed, and the
        // track outlives this view-model in the tab that owns both.
        Action handler = () => OnTrackContentChanged(track);
        _trackHandlers.Add(handler);
        track.MarkersChanged += handler;
    }

    /// <summary>
    ///     Re-runs every registered track against <paramref name="data" /> and re-lays out. A null
    ///     <paramref name="data" /> (no demo) clears everything, leaving no stale bands behind.
    /// </summary>
    public void Rebuild(ITimelineData? data)
    {
        _data = data;
        TotalFrames = data?.TotalFrames ?? 0;

        if (data is not null && TotalFrames > 0)
        {
            for (int i = 0; i < _tracks.Count; i++)
            {
                BuildTrack(i, data);
            }
        }
        else
        {
            for (int i = 0; i < _toggles.Count; i++)
            {
                _toggles[i].IsAvailable = false;
                _trackBands[i].Clear();
                _trackMarkers[i].Clear();
            }
        }

        Recombine();
    }

    // One track's availability and content, into that track's own slice. The ONE place a track is
    // queried, so "re-query this track" and "re-query all of them" cannot drift apart.
    private void BuildTrack(int index, ITimelineData data)
    {
        ITimelineTrack track = _tracks[index];
        TimelineTrackToggle toggle = _toggles[index];
        List<TimelineBand> bands = _trackBands[index];
        List<TimelineMarker> markers = _trackMarkers[index];

        bands.Clear();
        markers.Clear();
        toggle.IsAvailable = track.IsAvailable(data);

        if (!toggle.IsAvailable || !toggle.IsEnabled)
        {
            return;
        }

        bands.AddRange(track.BuildBands(data));
        markers.AddRange(track.BuildMarkers(data));
    }

    // Flattens the per-track slices back into the two built lists and re-lays out.
    private void Recombine()
    {
        _builtBands.Clear();
        _builtMarkers.Clear();

        for (int i = 0; i < _tracks.Count; i++)
        {
            _builtBands.AddRange(_trackBands[i]);
            _builtMarkers.AddRange(_trackMarkers[i]);
        }

        _builtMarkers.Sort(static (a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        Relayout();
        UpdateRoundLabel();
    }

    // A track saying its content changed. Availability is re-evaluated with it: for AnnotationTrack the
    // FIRST time-anchored stroke is what makes the track available at all, and it arrives here and
    // nowhere else.
    private void OnTrackContentChanged(ITimelineTrack track)
    {
        if (_data is not { } data || TotalFrames <= 0)
        {
            return; // no demo: Rebuild already parked every toggle unavailable
        }

        int index = _tracks.IndexOf(track);
        if (index < 0)
        {
            return;
        }

        BuildTrack(index, data);
        Recombine();
    }

    /// <summary>Turns a track on/off and re-runs the build. Unknown ids are ignored.</summary>
    public void SetTrackEnabled(string trackId, bool enabled)
    {
        foreach (TimelineTrackToggle toggle in _toggles)
        {
            if (string.Equals(toggle.Id, trackId, StringComparison.Ordinal))
            {
                toggle.IsEnabled = enabled;
                return;
            }
        }
    }

    /// <summary>
    ///     Applies persisted track visibility without echoing it straight back out as a change to save.
    ///     The owner calls this once, at construction, from <c>AppSettings.Playback2D.TimelineShow*</c>.
    /// </summary>
    /// <param name="trackId">The track to set. Unknown ids are ignored.</param>
    /// <param name="enabled">Whether the track draws.</param>
    public void RestoreTrackEnabled(string trackId, bool enabled)
    {
        _suppressTrackVisibilityChanged = true;
        try
        {
            SetTrackEnabled(trackId, enabled);
        }
        finally
        {
            _suppressTrackVisibilityChanged = false;
        }
    }

    /// <summary>
    ///     Raised after a USER change to a track's visibility has been applied and rebuilt. Not raised by
    ///     <see cref="RestoreTrackEnabled" />, and never by an availability change.
    /// </summary>
    public event Action? TrackVisibilityChanged;

    /// <summary>Moves the playhead. Called once per coalesced playback push: a binary search and two sets.</summary>
    public void UpdatePlayhead(int frameIndex, int tick)
    {
        CurrentFrameIndex = frameIndex;
        CurrentTick = tick;
        PlayheadX = XForFrame(frameIndex);
        PositionText = TotalFrames > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"frame {frameIndex} / {TotalFrames - 1} · tick {tick}")
            : "";
        UpdateRoundLabel();
    }

    /// <summary>
    ///     Raises <see cref="SeekRequested" /> for an exact frame. The rounds band seeks to a band's FIRST
    ///     frame, which must not round-trip through the pixel mapping.
    /// </summary>
    public void RequestSeekToFrame(int frameIndex)
    {
        if (TotalFrames <= 0)
        {
            return;
        }

        SeekRequested?.Invoke(Math.Clamp(frameIndex, 0, TotalFrames - 1));
    }

    /// <summary>The x offset (px) of a frame index on the scrub bar. 0 for a single-frame or unsized demo.</summary>
    public double XForFrame(int frameIndex)
    {
        if (TotalFrames <= 1 || PixelWidth <= 0 || frameIndex <= 0)
        {
            return 0;
        }

        int clamped = Math.Min(frameIndex, TotalFrames - 1);
        return clamped / (double)(TotalFrames - 1) * PixelWidth;
    }

    /// <summary>The frame index under an x offset (px), clamped into the demo.</summary>
    public int FrameIndexAt(double x)
    {
        if (TotalFrames <= 1 || PixelWidth <= 0 || double.IsNaN(x))
        {
            return 0;
        }

        double raw = x / PixelWidth * (TotalFrames - 1);
        if (double.IsNaN(raw) || double.IsInfinity(raw))
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(raw), 0, TotalFrames - 1);
    }

    /// <summary>Raises <see cref="SeekRequested" /> for the frame under an x offset. No-op without a demo.</summary>
    public void RequestSeek(double x)
    {
        if (TotalFrames <= 0)
        {
            return;
        }

        SeekRequested?.Invoke(FrameIndexAt(x));
    }

    /// <summary>Updates the hover readout for an x offset over the scrub bar.</summary>
    public void UpdateHover(double x)
    {
        if (TotalFrames <= 0)
        {
            HoverText = "";
            return;
        }

        int frame = FrameIndexAt(x);
        HoverText = $"→ {frame.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Clears the hover readout when the pointer leaves the scrub bar.</summary>
    public void ClearHover() => HoverText = "";

    partial void OnPixelWidthChanged(double value)
    {
        Relayout();
        PlayheadX = XForFrame(CurrentFrameIndex);
    }

    partial void OnPlayheadXChanged(double value) => OnPropertyChanged(nameof(PlayheadOffset));

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TimelineTrackToggle.IsEnabled))
        {
            return;
        }

        Rebuild(_data);

        // Raised AFTER the rebuild so a handler that persists the new state reads a settled view-model.
        // Deliberately not raised for IsAvailable: availability is a property of the demo, not a choice
        // the user made, and persisting it would turn "this demo has no bomb" into a stored preference.
        if (!_suppressTrackVisibilityChanged)
        {
            TrackVisibilityChanged?.Invoke();
        }
    }

    // Re-derives the bound visual collections from the built model lists + the current PixelWidth. Never
    // re-runs a track: a width change must not re-decode the demo's events.
    private void Relayout()
    {
        Bands.Clear();
        foreach (TimelineBand band in _builtBands)
        {
            double x = XForFrame(band.StartFrameIndex);
            double width = Math.Max(1.0, XForFrame(band.EndFrameIndex + 1) - x);
            Bands.Add(new TimelineBandViewModel(band, x, width, BrushForBand(band)));
        }

        Markers.Clear();
        int i = 0;
        while (i < _builtMarkers.Count)
        {
            TimelineMarker first = _builtMarkers[i];
            double x = XForFrame(first.FrameIndex);

            // Fold every following marker of the SAME track that lands within the coalesce radius.
            int j = i + 1;
            while (j < _builtMarkers.Count
                   && string.Equals(_builtMarkers[j].TrackId, first.TrackId, StringComparison.Ordinal)
                   && XForFrame(_builtMarkers[j].FrameIndex) - x <= MarkerCoalescePixels)
            {
                j++;
            }

            Markers.Add(new TimelineMarkerViewModel(first, x, BrushForMarker(first),
                FoldTooltip(i, j, first)));
            i = j;
        }
    }

    private string FoldTooltip(int start, int end, TimelineMarker first)
    {
        int count = end - start;
        if (count <= 1)
        {
            return first.Tooltip;
        }

        string label = DisplayNameFor(first.TrackId);
        StringBuilder sb = new();
        sb.Append(count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(label);
        int lines = Math.Min(count, MaxFoldedTooltipLines);
        for (int k = 0; k < lines; k++)
        {
            sb.Append('\n').Append(_builtMarkers[start + k].Tooltip);
        }

        if (count > lines)
        {
            sb.Append("\n…");
        }

        return sb.ToString();
    }

    private string DisplayNameFor(string trackId)
    {
        foreach (TimelineTrackToggle toggle in _toggles)
        {
            if (string.Equals(toggle.Id, trackId, StringComparison.Ordinal))
            {
                return toggle.DisplayName.ToLowerInvariant();
            }
        }

        return trackId;
    }

    // Binary search over the (ascending, non-overlapping) band list for the band holding the playhead.
    private void UpdateRoundLabel()
    {
        if (Bands.Count == 0 || CurrentFrameIndex < 0)
        {
            CurrentRoundLabel = "";
            return;
        }

        int lo = 0, hi = Bands.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo >> 1);
            TimelineBandViewModel band = Bands[mid];
            if (CurrentFrameIndex < band.StartFrameIndex)
            {
                hi = mid - 1;
            }
            else if (CurrentFrameIndex > band.EndFrameIndex)
            {
                lo = mid + 1;
            }
            else
            {
                CurrentRoundLabel = band.Label;
                return;
            }
        }

        CurrentRoundLabel = "";
    }

    private static ImmutableSolidColorBrush BrushForBand(TimelineBand band) =>
        band.Argb != 0
            ? new ImmutableSolidColorBrush(Color.FromUInt32(band.Argb))
            : Token("Pb2dHudDivider", 0x33404A4A);

    private static ImmutableSolidColorBrush BrushForMarker(TimelineMarker marker)
    {
        if (marker.Argb != 0)
        {
            return new ImmutableSolidColorBrush(Color.FromUInt32(marker.Argb));
        }

        return marker.Kind switch
        {
            TimelineMarkerKind.Kill => Token("Pb2dHeadshot", 0xFFF44336),
            TimelineMarkerKind.BombPlant => Token("Pb2dBomb", 0xFFE08040),
            TimelineMarkerKind.BombDefuse => Token("Pb2dDefuseTime", 0xFF5AB0E0),
            TimelineMarkerKind.BombExplode => Token("Pb2dHeadshot", 0xFFF44336),
            TimelineMarkerKind.Annotation => Token("Pb2dFlashAssist", 0xFFB66CD8),
            _ => Token("Pb2dTextBright", 0xFFC0C8D0)
        };
    }

    // Resolves a walled-off Pb2d HUD token through the central theme resolver (the same token namespace
    // XAML's {DynamicResource} reads), falling back to the dark-theme literal when the key is missing.
    //
    // Two thread rules, both load-bearing:
    //   * the brushes are IMMUTABLE. A SolidColorBrush is an AvaloniaObject whose constructor calls
    //     VerifyAccess(), so building one off the UI thread throws.
    //   * Application.ActualThemeVariant is a styled property with the same affinity, hence the
    //     CheckAccess guard before reaching for it.
    // So the layout math, and every marker/band it builds, stays testable without a dispatcher.
    private static ImmutableSolidColorBrush Token(string key, uint fallbackArgb)
    {
        Color fallback = Color.FromUInt32(fallbackArgb);
        if (Application.Current is not { } app || !Dispatcher.UIThread.CheckAccess())
        {
            return new ImmutableSolidColorBrush(fallback);
        }

        return new ImmutableSolidColorBrush(ThemeColors.Get(key, app.ActualThemeVariant, fallback));
    }
}

/// <summary>One laid-out point marker on the scrub bar.</summary>
public sealed class TimelineMarkerViewModel
{
    internal TimelineMarkerViewModel(TimelineMarker marker, double x, IBrush brush, string tooltip)
    {
        TrackId = marker.TrackId;
        FrameIndex = marker.FrameIndex;
        Tick = marker.Tick;
        Kind = marker.Kind;
        Glyph = marker.Glyph;
        Tooltip = tooltip;
        Brush = brush;
        X = x;
    }

    /// <summary>The producing track's stable id.</summary>
    public string TrackId { get; }

    /// <summary>The frame this marker sits on (the layout axis).</summary>
    public int FrameIndex { get; }

    /// <summary>The server tick the event fired at.</summary>
    public int Tick { get; }

    /// <summary>What the marker represents.</summary>
    public TimelineMarkerKind Kind { get; }

    /// <summary>The glyph drawn on the bar.</summary>
    public string Glyph { get; }

    /// <summary>Hover text. Carries the fold count when several markers coalesced here.</summary>
    public string Tooltip { get; }

    /// <summary>Resolved from the marker's ARGB, or from the theme token for its kind.</summary>
    public IBrush Brush { get; }

    /// <summary>Left offset in px on the scrub bar.</summary>
    public double X { get; }

    /// <summary>The same offset as a left margin.</summary>
    public Thickness Offset => new(X, 0, 0, 0);
}

/// <summary>One laid-out range band on the rounds row.</summary>
public sealed class TimelineBandViewModel
{
    internal TimelineBandViewModel(TimelineBand band, double x, double width, IBrush brush)
    {
        TrackId = band.TrackId;
        StartFrameIndex = band.StartFrameIndex;
        EndFrameIndex = band.EndFrameIndex;
        Label = band.Label;
        Tooltip = band.Tooltip;
        Brush = brush;
        X = x;
        Width = width;
    }

    /// <summary>The producing track's stable id.</summary>
    public string TrackId { get; }

    /// <summary>First frame of the band (inclusive).</summary>
    public int StartFrameIndex { get; }

    /// <summary>Last frame of the band (inclusive).</summary>
    public int EndFrameIndex { get; }

    /// <summary>Short label drawn in the band (the round number, or "wu").</summary>
    public string Label { get; }

    /// <summary>Hover text.</summary>
    public string Tooltip { get; }

    /// <summary>Resolved from the band's ARGB, or the neutral theme token.</summary>
    public IBrush Brush { get; }

    /// <summary>Left offset in px.</summary>
    public double X { get; }

    /// <summary>Width in px (at least 1, so a one-frame band is still hittable).</summary>
    public double Width { get; }

    /// <summary>The band's left offset as a margin.</summary>
    public Thickness Offset => new(X, 0, 0, 0);
}

/// <summary>A track's chrome row: its name, whether this demo can feed it, and the user's on/off choice.</summary>
public sealed partial class TimelineTrackToggle : ObservableObject
{
    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private bool _isEnabled = true;

    internal TimelineTrackToggle(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    /// <summary>The track's stable id.</summary>
    public string Id { get; }

    /// <summary>The track's human-readable name.</summary>
    public string DisplayName { get; }
}
