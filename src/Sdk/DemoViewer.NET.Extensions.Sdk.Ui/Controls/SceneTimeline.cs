#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.Playback2D.Core.Timeline;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui.Controls;

/// <summary>
///     A timeline over a frame clock of your own, drawn by <see cref="TimelineView" /> the way the 2D Playback
///     tab draws the demo's: a scrub bar with the playhead, one band row and one marker row built from the
///     tracks you register. Own it on the view model: register the tracks, <see cref="Rebuild" /> when the
///     clock's length changes, move the playhead on every tick, and seek when <see cref="SeekRequested" /> says
///     the user scrubbed or clicked.
///     <para>
///         A track is an <see cref="ITimelineTrack" /> from <c>DemoViewer.NET.Playback2D.Scene</c>; the ink of an
///         <c>AnnotationDocument</c> draws through its <c>AnnotationTrack</c>. Call members on the UI thread.
///         Outside the app there is no host to draw it, so it holds nothing and raises nothing.
///     </para>
/// </summary>
public sealed class SceneTimeline : IDisposable
{
    private readonly ITimelineBackend? _backend;

    /// <summary>Creates the timeline over the host's timeline renderer.</summary>
    public SceneTimeline()
    {
        _backend = TimelineHost.Create();
        if (_backend is not null)
        {
            _backend.SeekRequested += frameIndex => SeekRequested?.Invoke(frameIndex);
        }
    }

    /// <summary>Frames on the clock, from the last <see cref="Rebuild" />; 0 with no data.</summary>
    public int TotalFrames => _backend?.TotalFrames ?? 0;

    /// <summary>The markers the enabled tracks built, in frame order, as built and before any folding for the screen.</summary>
    public IReadOnlyList<TimelineMarker> Markers => _backend?.Markers ?? [];

    /// <summary>The bands the enabled tracks built, in registration order.</summary>
    public IReadOnlyList<TimelineBand> Bands => _backend?.Bands ?? [];

    /// <summary>Raised with the frame the user asked for, from a scrub, a click on the bar or a band, or <see cref="RequestSeek" />.</summary>
    public event Action<int>? SeekRequested;

    /// <summary>A track whose bands and markers the timeline draws. A track id already registered is ignored.</summary>
    /// <param name="track">The track.</param>
    public void RegisterTrack(ITimelineTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _backend?.RegisterTrack(track);
    }

    /// <summary>Removes a registered track and what it drew.</summary>
    /// <param name="track">The track.</param>
    public void UnregisterTrack(ITimelineTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _backend?.UnregisterTrack(track);
    }

    /// <summary>Builds every track again over <paramref name="data" />. Null clears the timeline.</summary>
    /// <param name="data">The clock: its frame count and tick rate.</param>
    public void Rebuild(ITimelineData? data) => _backend?.Rebuild(data);

    /// <summary>Moves the playhead.</summary>
    /// <param name="frameIndex">The frame shown.</param>
    /// <param name="tick">The tick shown.</param>
    public void UpdatePlayhead(int frameIndex, int tick) => _backend?.UpdatePlayhead(frameIndex, tick);

    /// <summary>Raises <see cref="SeekRequested" /> for a frame, clamped to the clock. Nothing before a <see cref="Rebuild" /> with frames.</summary>
    /// <param name="frameIndex">The frame.</param>
    public void RequestSeek(int frameIndex) => _backend?.RequestSeek(frameIndex);

    /// <inheritdoc />
    public void Dispose() => _backend?.Dispose();

    /// <summary>The renderer a <see cref="TimelineView" /> shows.</summary>
    internal ITimelineBackend? Backend => _backend;
}

/// <summary>Shows a <see cref="SceneTimeline" />. Set <see cref="Timeline" />, or bind it to the view model's.</summary>
public sealed class TimelineView : Control
{
    /// <summary>Defines the <see cref="Timeline" /> property.</summary>
    public static readonly StyledProperty<SceneTimeline?> TimelineProperty =
        AvaloniaProperty.Register<TimelineView, SceneTimeline?>(nameof(Timeline));

    private Control? _view;

    /// <summary>Creates an empty view.</summary>
    public TimelineView()
    {
        ClipToBounds = true;
    }

    /// <summary>The timeline shown, or null for nothing.</summary>
    public SceneTimeline? Timeline
    {
        get => GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TimelineProperty)
        {
            return;
        }

        if (_view is not null)
        {
            LogicalChildren.Remove(_view);
            VisualChildren.Remove(_view);
            _view = null;
        }

        _view = change.GetNewValue<SceneTimeline?>()?.Backend?.CreateView();
        if (_view is not null)
        {
            LogicalChildren.Add(_view);
            VisualChildren.Add(_view);
        }

        InvalidateMeasure();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_view is null)
        {
            return default;
        }

        _view.Measure(availableSize);
        return _view.DesiredSize;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        _view?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

/// <summary>What draws a <see cref="SceneTimeline" />: the app's timeline, installed at startup.</summary>
internal interface ITimelineBackend : IDisposable
{
    /// <summary>Frames on the clock.</summary>
    int TotalFrames { get; }

    /// <summary>The markers as built.</summary>
    IReadOnlyList<TimelineMarker> Markers { get; }

    /// <summary>The bands as built.</summary>
    IReadOnlyList<TimelineBand> Bands { get; }

    /// <summary>The user asked for a frame.</summary>
    event Action<int>? SeekRequested;

    /// <summary>Adds a track.</summary>
    void RegisterTrack(ITimelineTrack track);

    /// <summary>Removes a track.</summary>
    void UnregisterTrack(ITimelineTrack track);

    /// <summary>Builds every track again.</summary>
    void Rebuild(ITimelineData? data);

    /// <summary>Moves the playhead.</summary>
    void UpdatePlayhead(int frameIndex, int tick);

    /// <summary>Asks for a frame.</summary>
    void RequestSeek(int frameIndex);

    /// <summary>A fresh control drawing this timeline.</summary>
    Control CreateView();
}

/// <summary>Where the app installs the renderer every <see cref="SceneTimeline" /> is built over.</summary>
internal static class TimelineHost
{
    /// <summary>Builds a renderer; null before the app installs one.</summary>
    internal static Func<ITimelineBackend>? Factory { get; set; }

    internal static ITimelineBackend? Create() => Factory?.Invoke();
}
