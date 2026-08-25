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
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Timeline;

/// <summary>
///     The 2D playback timeline's view-model: registered <see cref="ITimelineTrack" />s, their built bands
///     and markers, the layout math, and the playhead. It never moves the clock — a click or drag raises
///     <see cref="SeekRequested" /> and the owning tab forwards it to
///     <c>IModuleContext.RequestSeekToFrame</c>, so LiveSync keeps observing every seek.
///     <para>
///         The x-axis domain is FRAME INDEX, which is the movement contract everything else in the app
///         already speaks; tick-stamped events are converted once at build time by the adapter.
///     </para>
/// </summary>
public sealed partial class Playback2DTimelineViewModel : ObservableObject
{
    // Two markers of one track closer than this are folded into a single visual whose tooltip carries the
    // count. Without it a 90k-frame demo realizes ~400 glyphs onto a ~600 px bar.
    private const double MarkerCoalescePixels = 2.0;

    private const int MaxFoldedTooltipLines = 5;

    private readonly List<TimelineBand> _builtBands = new();
    private readonly List<TimelineMarker> _builtMarkers = new();
    private readonly List<TimelineTrackToggle> _toggles = new();
    private readonly List<ITimelineTrack> _tracks = new();

    [ObservableProperty]
    private int _currentFrameIndex = -1;

    [ObservableProperty]
    private string _currentRoundLabel = "";

    [ObservableProperty]
    private int _currentTick;

    [ObservableProperty]
    private string _followStatus = "";

    /// <summary>Reserved for the CS2 ghost cursor. Always null in A1 — the LiveSync tick projection it needs
    /// is a contract change of its own.</summary>
    [ObservableProperty]
    private int? _ghostFrameIndex;

    [ObservableProperty]
    private string _hoverText = "";

    [ObservableProperty]
    private bool _isVisible;

    private ITimelineData? _data;

    [ObservableProperty]
    private double _pixelWidth;

    [ObservableProperty]
    private double _playheadX;

    [ObservableProperty]
    private string _positionText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private int _totalFrames;

    /// <summary>The registered tracks' toggles, in registration order (which is display order).</summary>
    public IReadOnlyList<TimelineTrackToggle> Tracks => _toggles;

    /// <summary>The laid-out round bands. Rebuilt on <see cref="Rebuild" /> and on a width change.</summary>
    public ObservableCollection<TimelineBandViewModel> Bands { get; } = new();

    /// <summary>The laid-out point markers, after same-track coalescing.</summary>
    public ObservableCollection<TimelineMarkerViewModel> Markers { get; } = new();

    /// <summary>
    ///     Raised on click / drag-scrub with the target frame index. The owner forwards it to
    ///     <c>IModuleContext.RequestSeekToFrame</c> — the timeline never moves the clock itself.
    /// </summary>
    public event Action<int>? SeekRequested;

    /// <summary>Registers a track. Registration order is display order; re-registering an id is ignored.</summary>
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
    }

    /// <summary>
    ///     Re-runs every registered track against <paramref name="data" /> and re-lays out. A null
    ///     <paramref name="data" /> (no demo) clears everything, leaving no stale bands behind.
    /// </summary>
    public void Rebuild(ITimelineData? data)
    {
        _data = data;
        _builtBands.Clear();
        _builtMarkers.Clear();

        TotalFrames = data?.TotalFrames ?? 0;

        if (data is not null && TotalFrames > 0)
        {
            for (int i = 0; i < _tracks.Count; i++)
            {
                ITimelineTrack track = _tracks[i];
                TimelineTrackToggle toggle = _toggles[i];
                toggle.IsAvailable = track.IsAvailable(data);

                if (!toggle.IsAvailable || !toggle.IsEnabled)
                {
                    continue;
                }

                _builtBands.AddRange(track.BuildBands(data));
                _builtMarkers.AddRange(track.BuildMarkers(data));
            }
        }
        else
        {
            foreach (TimelineTrackToggle toggle in _toggles)
            {
                toggle.IsAvailable = false;
            }
        }

        _builtMarkers.Sort(static (a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        Relayout();
        UpdateRoundLabel();
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

    /// <summary>The playhead's left offset as a margin — the item layer positions by margin, not by Canvas.</summary>
    public Thickness PlayheadOffset => new(PlayheadX, 0, 0, 0);

    /// <summary>Moves the playhead. Called once per coalesced playback push — a binary search and two sets.</summary>
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
    ///     Raises <see cref="SeekRequested" /> for an exact frame — the rounds band seeks to a band's FIRST
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
        if (e.PropertyName == nameof(TimelineTrackToggle.IsEnabled))
        {
            Rebuild(_data);
        }
    }

    // Re-derives the bound visual collections from the built model lists + the current PixelWidth. Never
    // re-runs a track — a width change must not re-decode the demo's events.
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

    // Resolves a walled-off Pb2d HUD token through the central theme resolver — the same token namespace
    // XAML's {DynamicResource} reads — falling back to the dark-theme literal when the key is missing.
    //
    // Two thread rules are folded in here, and both are load-bearing rather than defensive:
    //   * the brushes are IMMUTABLE — a SolidColorBrush is an AvaloniaObject whose constructor calls
    //     VerifyAccess(), so building one off the UI thread throws;
    //   * Application.ActualThemeVariant is a styled property with the same affinity, hence the
    //     CheckAccess guard before reaching for it.
    // Together they keep the layout math (and every marker/band it builds) testable without a dispatcher,
    // falling back to the dark-theme literal in that case.
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

    /// <summary>Hover text — carries the fold count when several markers coalesced here.</summary>
    public string Tooltip { get; }

    /// <summary>Resolved from the marker's ARGB, or from the theme token for its kind.</summary>
    public IBrush Brush { get; }

    /// <summary>Left offset in px on the scrub bar.</summary>
    public double X { get; }

    /// <summary>The same offset as a left margin — the item layer is a Panel, positioned by margin.</summary>
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

    /// <summary>The band's left offset as a margin — the item layer is a Panel, positioned by margin.</summary>
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
