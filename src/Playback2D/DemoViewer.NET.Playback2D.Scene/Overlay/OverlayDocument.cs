namespace DemoViewer.NET.Playback2D.Core.Overlay;

/// <summary>
///     The Overlay View's input: every alive position of every matched state of a result set, on one
///     map. The heatmap layer draws it; the App fills it from the positions files of the hits, and a
///     fixture carries one so <c>dv2d</c> can draw it with no window.
///     <para>
///         Replaced whole rather than appended to: an overlay describes one result set, and a set is
///         swapped, never grown. <see cref="Version" /> moves on every replacement, which is what the
///         compositor's picture cache keys on.
///     </para>
///     <para>
///         Host-independent on purpose: no Skia type, no App type and no positions-file type live here.
///     </para>
/// </summary>
public sealed class OverlayDocument
{
    private OverlayPoint[] _points = [];

    /// <summary>The map the points lie on, as the demo header spells it. Empty with no overlay.</summary>
    public string MapName { get; private set; } = "";

    /// <summary>How many matched states the points came from: a state is one sampled step of one hit.</summary>
    public int StateCount { get; private set; }

    /// <summary>The points, in the order they were given.</summary>
    public IReadOnlyList<OverlayPoint> Points => _points;

    /// <summary>How many points there are.</summary>
    public int Count => _points.Length;

    /// <summary>There is nothing to draw.</summary>
    public bool IsEmpty => _points.Length == 0;

    /// <summary>Bumped on every change; a cheap way for a layer or a view to notice one.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after every change, on the caller's thread.</summary>
    public event Action? Changed;

    /// <summary>Replaces the overlay with a new set. The points are copied, so the caller's list is free afterwards.</summary>
    /// <param name="mapName">The map the points lie on.</param>
    /// <param name="points">The points.</param>
    /// <param name="stateCount">How many matched states they came from.</param>
    public void Replace(string mapName, IReadOnlyList<OverlayPoint> points, int stateCount)
    {
        ArgumentNullException.ThrowIfNull(mapName);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfNegative(stateCount);

        MapName = mapName;
        StateCount = stateCount;
        _points = [.. points];
        Bump();
    }

    /// <summary>Drops every point. A no-op when there was nothing.</summary>
    public void Clear()
    {
        if (_points.Length == 0 && MapName.Length == 0)
        {
            return;
        }

        MapName = "";
        StateCount = 0;
        _points = [];
        Bump();
    }

    private void Bump()
    {
        Version++;
        Changed?.Invoke();
    }
}
