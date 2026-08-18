#region

using System.ComponentModel;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.ViewModels.Shell;
using ModuleContextImpl = DemoViewer.NET.Modules.ModuleContext;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     The DV-intent observer: translates shell playback events
///     into <see cref="SyncEngine" /> desired-state calls. UI-thread resident — construct and
///     dispose on the UI thread while a session is up.
///     <para>
///         Sources: <c>FrameNavigationViewModel.SelectedFrameChanged</c> (discrete seeks and
///         steps — the play loop never raises it), <c>PlaybackController.PropertyChanged</c>
///         (IsPlaying and Speed; the end-of-demo auto-pause is legitimate DV intent and flows through
///         unchanged, and Speed's clamp re-entry duplicates dedup in the
///         reconciler), and <c>IModuleContext.DemoReset</c> (load-complete → demo/path/mapper
///         rebuild). Intent is NEVER derived from the per-frame <c>Advanced</c> push. The
///         <c>applyingRemote</c> probe is the loop breaker: engine-driven
///         controller mutations (inbound sync) are observed but produce no new intent.
///     </para>
/// </summary>
internal sealed class SyncStateObserver : IDisposable
{
    private readonly Func<bool> _applyingRemote;
    private readonly IModuleContext? _context;
    private readonly SyncEngine _engine;
    private readonly MainViewModel _shell;
    private readonly int _tickOffset;
    private bool _disposed;

    public SyncStateObserver(MainViewModel shell, SyncEngine engine, int tickOffset, Func<bool> applyingRemote)
    {
        _shell = shell;
        _engine = engine;
        _tickOffset = tickOffset;
        _applyingRemote = applyingRemote;
        _context = shell.ModuleContext;

        _shell.Navigation.SelectedFrameChanged += OnSelectedFrameChanged;
        _shell.Playback.PropertyChanged += OnPlaybackPropertyChanged;
        if (_context is not null)
        {
            _context.DemoReset += OnDemoReset;
        }

        // Follow-pick relay (the module tab VMs are lazily built; the host context is the
        // always-reachable seam).
        if (_context is ModuleContextImpl hostContext)
        {
            hostContext.SpectateTargetChanged += OnSpectateTargetChanged;
        }

        // Seed intent from the shell's current state (a demo may already be loaded when the
        // user enables sync).
        PushDemoIntent();
    }

    /// <summary>The current demo's mapper (UI thread; rebuilt on DemoReset). Shared with the inbound pump.</summary>
    internal TickMapper? Mapper { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.Navigation.SelectedFrameChanged -= OnSelectedFrameChanged;
        _shell.Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        if (_context is not null)
        {
            _context.DemoReset -= OnDemoReset;
        }

        if (_context is ModuleContextImpl hostContext)
        {
            hostContext.SpectateTargetChanged -= OnSpectateTargetChanged;
        }
    }

    /// <summary>Resolve the follow pick's slot to the exact in-demo name and push spectate intent.</summary>
    private void OnSpectateTargetChanged(int slot)
    {
        if (_applyingRemote() || _context is null || slot < 0)
        {
            return;
        }

        foreach (PlayerRosterEntry entry in _context.Players)
        {
            if (entry.Slot == slot && !string.IsNullOrWhiteSpace(entry.Name))
            {
                _engine.SetDesiredSpectator(entry.Name);
                return;
            }
        }
    }

    private void OnDemoReset() => PushDemoIntent();

    /// <summary>Re-derives and re-pushes the full intent (the service's Re-sync). UI thread.</summary>
    public void Republish() => PushDemoIntent();

    /// <summary>
    ///     (Re)derives the demo-level intent: path validity gate (CSVG needs a
    ///     rooted, existing host path), fresh <see cref="TickMapper" /> from the freshly-built
    ///     boundaries, and the full (path, tick, playing) push.
    /// </summary>
    private void PushDemoIntent()
    {
        if (_applyingRemote())
        {
            return;
        }

        string? path = _context?.DemoPath;
        ParsedDemo? demo = (_context as ICurrentDemoSource)?.CurrentDemo;
        if (demo is null || path is null)
        {
            Mapper = null;
            _engine.SetDesiredDemo(null, null, false);
            return;
        }

        if (!Path.IsPathRooted(path) || !File.Exists(path))
        {
            Mapper = null;
            _engine.NoteDemoPathUnavailable();
            return;
        }

        Mapper = new TickMapper(demo.Frames, _shell.Navigator.TickBoundaryFrames, _tickOffset);
        _engine.SetDesiredDemo(path, CurrentDesiredTick(), _shell.Playback.IsPlaying);
    }

    private void OnSelectedFrameChanged(int frameIndex)
    {
        if (_applyingRemote() || Mapper is null || frameIndex < 0)
        {
            return;
        }

        _engine.SetDesiredTick(Mapper.Cs2DemoTick(frameIndex));
        // A seek-while-playing lands paused (SeekToFrame stops the play loop first) — mirror
        // DV's actual post-seek state, not the pre-seek one.
        _engine.SetDesiredPlaying(_shell.Playback.IsPlaying);
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingRemote())
        {
            return;
        }

        if (e.PropertyName == nameof(PlaybackController.Speed))
        {
            // User speed changes mirror to CS2's demo timescale — the engine no-ops
            // without the "timescale-set" capability, and the clamp's duplicate re-entry
            // notification dedups in the reconciler (same value → no resend).
            _engine.SetDesiredTimescale(_shell.Playback.Speed);
            return;
        }

        if (e.PropertyName != nameof(PlaybackController.IsPlaying))
        {
            return;
        }

        bool playing = _shell.Playback.IsPlaying;
        if (!playing && Mapper is not null)
        {
            // Pausing fixes DV's playhead wherever the play loop stopped — that position never
            // came through SelectedFrameChanged, so push it as the new discrete intent.
            int frame = _shell.Playback.CurrentFrameIndex;
            if (frame >= 0)
            {
                _engine.SetDesiredTick(Mapper.Cs2DemoTick(frame));
            }
        }

        _engine.SetDesiredPlaying(playing);
    }

    private long? CurrentDesiredTick()
    {
        if (Mapper is null)
        {
            return null;
        }

        int frame = _shell.Playback.CurrentFrameIndex;
        // No frame selected = the demo start — via the mapper, so the D2 TickOffset applies
        // exactly once here like at every other emission site (a literal 0 would skip it).
        return frame >= 0 ? Mapper.Cs2DemoTick(frame) : Mapper.Cs2TickFromDvTick(0);
    }
}
