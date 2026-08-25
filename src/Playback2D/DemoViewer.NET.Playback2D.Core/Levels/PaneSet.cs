#region

using DemoViewer.NET.Playback2D.Core.Cameras;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     The single owner of pane lifetime and camera identity (plan correction 4 — B3's
///     "LevelPaneStore" is added behaviour on this type, never a second one).
///     <para>
///         <b>Reconciliation is keyed on <see cref="MapLevelId" />, not array position.</b> The pre-v2
///         <c>EnsureCameras</c> kept cameras by index, so a rebuild that inserted a <i>lower</i> floor
///         slid every camera down one band and silently handed the upper floor the lower floor's pan.
///         That is design risk 5, and this is where it is fixed.
///     </para>
///     <para>
///         <b>Steady state allocates nothing.</b> <see cref="Reconcile" /> early-outs on the level-set
///         version, the display mode and the host size, so the common frame — nothing changed — never
///         reaches the layout policy.
///     </para>
/// </summary>
public sealed class PaneSet
{
    private readonly List<LevelPane> _panes = [];
    private readonly List<LevelPane> _scratch = [];
    private SKSize _host;
    private int _lastVersion = -1;
    private LevelDisplayMode _mode = LevelDisplayMode.Stacked;
    private ILevelLayoutPolicy _policy;

    /// <summary>Creates a pane set over a layout policy.</summary>
    /// <param name="policy">The policy deciding pane geometry.</param>
    public PaneSet(ILevelLayoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <summary>The live panes, lowest level first.</summary>
    public IReadOnlyList<LevelPane> Panes => _panes;

    /// <summary>The host surface size the panes were last arranged over.</summary>
    public SKSize Host => _host;

    /// <summary>The layout policy. Swapping it forces the next <see cref="Reconcile" /> to re-arrange.</summary>
    public ILevelLayoutPolicy Policy
    {
        get => _policy;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_policy, value))
            {
                return;
            }

            _policy = value;
            _lastVersion = -1;
        }
    }

    /// <summary>
    ///     Re-arranges the panes for the current level set and host size, carrying each surviving
    ///     level's camera, manual override and rig across by id.
    /// </summary>
    /// <param name="space">The level set.</param>
    /// <param name="mode">The display mode.</param>
    /// <param name="host">Host surface size in device-independent pixels.</param>
    /// <param name="extent">World extent a newly appeared level is fitted to.</param>
    /// <returns>True when the pane list or their rectangles changed — the caller invalidates caches.</returns>
    public bool Reconcile(MapSpace space, LevelDisplayMode mode, SKSize host, WorldBounds extent)
    {
        ArgumentNullException.ThrowIfNull(space);

        bool sameShape = _lastVersion == space.Version
                         && _mode == mode
                         && Math.Abs(_host.Width - host.Width) <= 0.5f
                         && Math.Abs(_host.Height - host.Height) <= 0.5f
                         && _panes.Count == Math.Max(0, space.Levels.Count);
        if (sameShape)
        {
            return false;
        }

        IReadOnlyList<LevelPane> arranged = _policy.Arrange(space, mode, host);

        _scratch.Clear();
        for (int i = 0; i < arranged.Count; i++)
        {
            LevelPane fresh = arranged[i];
            LevelPane? existing = FindById(fresh.Level.Id);

            if (existing is null)
            {
                // A newly appeared level. Fit it to the current world extent, exactly as the pre-v2
                // EnsureCameras did for a newly appeared slice (line 516).
                LevelPane pane = new(fresh.Level,
                    new SliceCamera(ViewportTransform.Fit(fresh.ViewportRect.Width, fresh.ViewportRect.Height,
                        extent.MinX, extent.MinY, extent.MaxX, extent.MaxY)),
                    ManualRig.Instance)
                {
                    ViewportRect = fresh.ViewportRect,
                    LevelIndex = fresh.LevelIndex,
                    Space = space,
                    PaneCount = arranged.Count
                };
                _scratch.Add(pane);
                continue;
            }

            // A surviving level keeps its camera identity — pan, zoom and manual override — and is
            // merely re-viewported onto the (possibly new) band rectangle.
            existing.Level = fresh.Level;
            existing.LevelIndex = fresh.LevelIndex;
            existing.ViewportRect = fresh.ViewportRect;
            existing.Space = space;
            existing.PaneCount = arranged.Count;
            existing.Camera.Current = existing.Camera.Current
                .WithViewport(fresh.ViewportRect.Width, fresh.ViewportRect.Height);
            existing.SyncCameraEpoch();
            _scratch.Add(existing);
        }

        _panes.Clear();
        _panes.AddRange(_scratch);
        _scratch.Clear();

        _lastVersion = space.Version;
        _mode = mode;
        _host = host;
        return true;
    }

    /// <summary>
    ///     Fits every pane to a world extent and clears its manual override — the pre-v2
    ///     <c>ApplyFitToAllSlices</c> (lines 526-533).
    /// </summary>
    /// <param name="extent">The world rectangle to frame.</param>
    public void FitAll(WorldBounds extent)
    {
        for (int i = 0; i < _panes.Count; i++)
        {
            LevelPane pane = _panes[i];
            pane.Camera.Current = ViewportTransform.Fit(pane.ViewportRect.Width, pane.ViewportRect.Height,
                extent.MinX, extent.MinY, extent.MaxX, extent.MaxY);
            pane.Camera.ManualOverride = false;
            pane.SyncCameraEpoch();
        }
    }

    /// <summary>
    ///     Re-arms every pane's auto camera — the pre-v2 <c>Mode</c> setter (lines 172-175). A mode the
    ///     user just picked must take effect now, not be held off by a prior manual pan.
    /// </summary>
    public void ClearManualOverrides()
    {
        for (int i = 0; i < _panes.Count; i++)
        {
            _panes[i].Camera.ManualOverride = false;
        }
    }

    /// <summary>Assigns a rig to every pane.</summary>
    /// <param name="factory">Produces the rig for a pane; called once per pane.</param>
    public void SetRig(Func<LevelPane, ICameraRig> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        for (int i = 0; i < _panes.Count; i++)
        {
            _panes[i].Rig = factory(_panes[i]);
        }
    }

    /// <summary>
    ///     The pane under a host-space point — the pre-v2 <c>SliceIndexAtScreenY</c> (lines 464-475),
    ///     including its "single band swallows every Y" and clamp-to-edge behaviour.
    /// </summary>
    /// <param name="x">Host X. Unused by the stacked policy; part of the contract for B3's layouts.</param>
    /// <param name="y">Host Y.</param>
    public LevelPane? PaneAt(float x, float y)
    {
        if (_panes.Count == 0)
        {
            return null;
        }

        if (_panes.Count == 1 || _host.Height < 1)
        {
            return _panes[0];
        }

        for (int i = 0; i < _panes.Count; i++)
        {
            SKRect rect = _panes[i].ViewportRect;
            if (y >= rect.Top && y < rect.Bottom && x >= rect.Left && x <= rect.Right)
            {
                return _panes[i];
            }
        }

        // Outside every band (a fractional-pixel gap at the bottom edge, or a pointer captured past the
        // control): clamp to the band the pre-v2 floor/clamp arithmetic would have chosen.
        int count = _panes.Count;
        float bandHeight = _host.Height / count;
        int section = (int)Math.Clamp(Math.Floor(y / bandHeight), 0, count - 1);
        int levelIndex = count - 1 - section;
        return FindByIndex(levelIndex) ?? _panes[0];
    }

    /// <summary>Copies an immutable snapshot of every pane into a caller-owned list. Allocation-free.</summary>
    /// <param name="into">The destination, cleared first.</param>
    public void CopySnapshots(List<LevelPaneSnapshot> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        for (int i = 0; i < _panes.Count; i++)
        {
            into.Add(_panes[i].Snapshot());
        }
    }

    /// <summary>Re-evaluates every pane's camera epoch. Returns true when any bumped.</summary>
    public bool SyncCameraEpochs()
    {
        bool any = false;
        for (int i = 0; i < _panes.Count; i++)
        {
            any |= _panes[i].SyncCameraEpoch();
        }

        return any;
    }

    /// <summary>The pane showing this level, or null.</summary>
    /// <param name="id">A level id.</param>
    public LevelPane? FindById(MapLevelId id)
    {
        for (int i = 0; i < _panes.Count; i++)
        {
            if (_panes[i].Level.Id == id)
            {
                return _panes[i];
            }
        }

        return null;
    }

    /// <summary>Drops every pane. For a demo unload.</summary>
    public void Clear()
    {
        _panes.Clear();
        _lastVersion = -1;
    }

    private LevelPane? FindByIndex(int levelIndex)
    {
        for (int i = 0; i < _panes.Count; i++)
        {
            if (_panes[i].LevelIndex == levelIndex)
            {
                return _panes[i];
            }
        }

        return null;
    }
}
