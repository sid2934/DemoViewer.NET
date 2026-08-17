#region

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.ViewModels.Playback;

/// <summary>
///     The lightweight transient pushed to <c>PlaybackController.Advanced</c> on each (coalesced)
///     render frame while playing. Carries only the position — the read-only entity / player-joined
///     surface is the <c>IPlaybackSnapshot</c> facade that wraps this. Two deliberate layers:
///     the controller raises <c>Advanced&lt;PlaybackFrame&gt;</c>; the module context raises
///     <c>Advanced&lt;IPlaybackSnapshot&gt;</c>.
/// </summary>
public readonly record struct PlaybackFrame(int FrameIndex, int Tick);

/// <summary>
///     The single authoritative owner of "current position" and of the authoritative
///     <see cref="EntityTracker" />. Every position move — manual frame selection, the
///     seek controls, the Replay tick-nav, the command palette, and the play loop — routes
///     through this one object, so there is exactly one code path that advances the clock. This
///     eliminates the "two competing playback notions" risk by construction.
///     <para>
///         The controller owns the observable position
///         state (<see cref="CurrentFrameIndex" /> / <see cref="CurrentTick" /> / <see cref="IsPlaying" />
///         / <see cref="Speed" />) and is the single fan-out point for a position move. The actual
///         side-effect body — set selected frame, drive seek controls, kick the entity seek, refresh
///         debugger CanExecute, seek analysis — is wired in by the shell via <see cref="ApplySeek" />
///         (the established callback-delegate dependency direction; the shell never holds a
///         back-reference here either way). A re-entrancy guard ensures that when the fan-out (or any
///         navigation method) assigns <c>ParserTab.SelectedFrame</c>, the resulting setter callback does
///         not re-enter and double-fire the seek.
///     </para>
/// </summary>
public sealed partial class PlaybackController : ObservableObject, IDisposable
{
    // Speed clamp: 0.25× … 8×.
    private const double MinSpeed = 0.25;

    private const double MaxSpeed = 8.0;

    // Coalescing flag: at most one Advanced push per render frame regardless of Speed. The
    // tracker may step K frames between pushes; we never skip DECODING a frame, only NOTIFYING about
    // intermediate ones.
    private bool _advancePending;

    // Re-entrancy guard: the fan-out body assigns ParserTab.SelectedFrame, whose setter loops back
    // into ApplySeek via HandleFrameSelectedFromParserTab. Without this guard a single SeekToFrame
    // would fire the heavy entity seek twice. Mirrors MainViewModel._cardModeActive.
    private bool _applying;

    // ── Authoritative observable position state ───────────────────────────────
    [ObservableProperty]
    private int _currentFrameIndex = -1;

    [ObservableProperty]
    private int _currentTick;

    // Fractional-frame accumulator so Speed handles both <1× and >1× uniformly: each timer tick adds
    // Speed, the integer part is how many frames to step this tick, the fraction carries forward.
    private double _frameAccumulator;

    private IReadOnlyList<DemoFrame>? _frames;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _speed = 1.0;

    // ── Play loop ─────────────────────────────────────────────────────────────
    private DispatcherTimer? _timer;

    /// <summary>Total frames in the loaded demo, 0 when none loaded.</summary>
    public int TotalFrames => _frames?.Count ?? 0;

    /// <summary>True once a demo is loaded and has at least one frame.</summary>
    public bool HasDemo => TotalFrames > 0;

    /// <summary>
    ///     Server tick rate (ticks/second). Sourced from the demo header
    ///     (<c>ParsedDemo.TickRate</c>) on load; defaults to 64 until a demo with a valid tick
    ///     interval is loaded. Used by the play loop to pace the <c>DispatcherTimer</c>.
    /// </summary>
    public int TickRate { get; private set; } = 64;

    /// <summary>
    ///     The shell-wired DISCRETE fan-out applied on a one-off position move (click / scrub /
    ///     palette / StepBack). This is the lifted body of <c>HandleFrameSelectedFromParserTab</c> —
    ///     it does the light-sync work (selected frame, seek controls, command CanExecute, analysis
    ///     seek) AND kicks the heavy ASYNC checkpoint-replay entity seek. Wired once in the shell ctor.
    /// </summary>
    public Action<int>? ApplySeek { get; set; }

    /// <summary>
    ///     The shell-wired LIGHT-SYNC fan-out used by the incremental <see cref="StepForward" /> /
    ///     play loop. Does everything <see cref="ApplySeek" /> does EXCEPT the heavy async entity seek
    ///     (the controller instead steps the authoritative tracker synchronously and triggers a sync
    ///     rebuild via <see cref="StepEntityRebuild" />). Runs under the re-entrancy guard so its
    ///     <c>SelectedFrame=</c> echo is absorbed. Wired once in the shell ctor.
    /// </summary>
    public Action<int>? ApplyLightSeek { get; set; }

    /// <summary>
    ///     The single authoritative <see cref="EntityTracker" />. NEVER exposed publicly — the
    ///     read-only <c>IModuleContext</c> wraps it. The discrete async seek publishes its freshly-built
    ///     tracker here via <see cref="PublishTracker" />; the incremental step mutates this instance in
    ///     place via <see cref="EntityTracker.AdvanceOneFrame" />. Null until the first seek completes
    ///     or the demo is unloaded.
    /// </summary>
    internal EntityTracker? AuthoritativeTracker { get; private set; }

    /// <summary>
    ///     Synchronous entity-tree rebuild from the incrementally-stepped authoritative tracker. Wired
    ///     to EntityTab's sync rebuild. Args: (stepped tracker, optional prev-frame snapshot for delta).
    /// </summary>
    public Action<EntityTracker, Dictionary<int, Dictionary<string, object?>>?>? StepEntityRebuild { get; set; }

    /// <summary>
    ///     Cancels any in-flight async discrete seek before an incremental step, so a late-completing
    ///     seek can't clobber the synchronously-stepped instance. Wired to
    ///     EntityTab's seek-cancellation.
    /// </summary>
    public Action? CancelInFlightSeek { get; set; }

    /// <summary>
    ///     Called with the new frame index after each move so the shared navigation seam re-raises
    ///     <c>SelectedFrameChanged</c> to its subscribers (keeps SeekControls / Parser in
    ///     sync). Wired by the shell to <c>Navigation.RaiseSelectedFrameChanged</c>.
    /// </summary>
    public Action<int>? NotifyFrameChanged { get; set; }


    /// <summary>
    ///     Snaps the discrete tabs (Parser / Entity / Analysis) to the current frame. Called ONCE on
    ///     <see cref="Pause" /> so they reflect where playback stopped (the play loop itself never
    ///     touches them). Wired to the light fan-out + a synchronous EntityTab rebuild from the
    ///     already-stepped authoritative tracker.
    /// </summary>
    public Action<int>? SnapDiscreteTabsToCurrent { get; set; }

    /// <inheritdoc />
    public void Dispose() => StopTimer();

    /// <summary>
    ///     Publishes a freshly-built tracker as the new authoritative instance (called by EntityTab
    ///     when its async discrete seek completes). This is the atomic swap-in:
    ///     after it, incremental stepping resumes on the new instance.
    /// </summary>
    public void PublishTracker(EntityTracker tracker)
    {
        AuthoritativeTracker = tracker;

        // A discrete seek's freshly-built tracker has just landed at CurrentFrameIndex. The
        // SeekToFrame fan-out updates the built-in tabs synchronously, but the module-facing Advanced
        // push only ever fired from the play loop — so modules (the 2D viewport) didn't update on
        // nav-bar / frame-box / prev-next / semantic-nav moves while paused. Push now that the tracker is
        // ready at the new position. Coalesced + UI-thread-marshaled, so it's safe off the seek worker.
        RequestCoalescedAdvance();
    }

    /// <summary>
    ///     Render-frame-coalesced push to subscribers while playing. Fires on the UI
    ///     thread, at most once per render frame regardless of <see cref="Speed" />. The
    ///     module context subscribes to this and re-raises its own <c>IPlaybackSnapshot</c> push.
    /// </summary>
    public event Action<PlaybackFrame>? Advanced;

    /// <summary>Clamps <see cref="Speed" /> into [0.25, 8] and re-paces the running timer.</summary>
    partial void OnSpeedChanged(double value)
    {
        double clamped = Math.Clamp(value, MinSpeed, MaxSpeed);
        if (Math.Abs(clamped - value) > double.Epsilon)
        {
            Speed = clamped; // re-enters; the clamped value is within range so this terminates.
            return;
        }

        if (_timer is not null)
        {
            _timer.Interval = TimerInterval();
        }
    }

    /// <summary>
    ///     Registers the loaded demo with the controller: frame list (for index/tick math and
    ///     incremental stepping) and tick rate (for the play loop). Resets position.
    /// </summary>
    public void LoadDemo(IReadOnlyList<DemoFrame> frames, int tickRate)
    {
        _frames = frames;
        TickRate = tickRate > 0 ? tickRate : 64;
        Reset();
    }

    /// <summary>
    ///     Full unload: drops the frame list on top of everything <see cref="Reset" /> clears. The frame
    ///     list is the controller's only demo-scale reference, and every <see cref="DemoFrame" /> slices
    ///     zero-copy into the demo byte buffer — so leaving it set would pin the whole file after a close.
    ///     <see cref="LoadDemo" /> replaces it on the reload path; this is the standalone-close path.
    /// </summary>
    public void Unload()
    {
        _frames = null;
        TickRate = 64;
        Reset();
    }

    /// <summary>Clears playback state on demo unload / reparse.</summary>
    public void Reset()
    {
        StopTimer();
        CurrentFrameIndex = -1;
        CurrentTick = 0;
        // Must null the authoritative tracker: otherwise a stale instance
        // survives unload and the StepForward readiness precondition would falsely pass against it.
        AuthoritativeTracker = null;
    }

    /// <summary>
    ///     The single position-move entry point. Performs the wired fan-out under the re-entrancy
    ///     guard, then updates the observable position state and notifies the navigation seam.
    ///     Out-of-range indices are clamped out (no-op) to match the legacy guards.
    /// </summary>
    public void SeekToFrame(int frameIndex)
    {
        if (_applying)
        {
            // Re-entrant call from the SelectedFrame setter inside the fan-out — already in flight.
            return;
        }

        // A discrete seek during play must stop the loop first: otherwise the
        // freshly-published tracker swaps under the running loop and the next tick steps the wrong
        // instance. Pause() also snaps the discrete tabs, but the seek below re-runs the full fan-out
        // at the target frame, so the user lands exactly where they clicked.
        if (IsPlaying)
        {
            StopTimer();
        }

        // frameIndex == -1 is the "clear selection" signal (e.g. SelectedFrame set to null on
        // unload). It must still run the fan-out (reset _selectedFrameIndex, refresh command
        // CanExecute) — matching the legacy HandleFrameSelectedFromParserTab(-1) behavior. Other
        // negative / out-of-range indices are no-ops, matching the legacy seek guards.
        if (frameIndex < -1 || frameIndex >= TotalFrames)
        {
            return;
        }

        _applying = true;
        try
        {
            ApplySeek?.Invoke(frameIndex);
        }
        finally
        {
            _applying = false;
        }

        CurrentFrameIndex = frameIndex;
        if (_frames is { } f && frameIndex >= 0 && frameIndex < f.Count)
        {
            CurrentTick = f[frameIndex].ServerTick;
        }

        NotifyFrameChanged?.Invoke(frameIndex);
    }

    /// <summary>Selects the first frame whose server tick is at or after <paramref name="tick" />.</summary>
    public void SeekToTick(int tick)
    {
        if (_frames is not { } f)
        {
            return;
        }

        for (int i = 0; i < f.Count; i++)
        {
            if (f[i].ServerTick >= tick)
            {
                SeekToFrame(i);
                return;
            }
        }
    }

    /// <summary>
    ///     Incremental forward step: when the authoritative tracker is ready and sits exactly at
    ///     the current frame, advance it by ONE frame in place via
    ///     <see cref="EntityTracker.AdvanceOneFrame" /> (O(1)) and rebuild EntityTab synchronously —
    ///     this is the play-loop primitive. Otherwise (cold start / a discrete seek still in flight)
    ///     fall back to a discrete <see cref="SeekToFrame" />.
    ///     <para>
    ///         The light-sync fan-out (selected frame, seek controls, command CanExecute, analysis
    ///         seek) runs under the re-entrancy guard; the heavy entity work is the in-place step, not
    ///         an O(N) async replay. The prev-frame field snapshot is captured BEFORE the step so delta
    ///         highlighting keeps working during stepping/play.
    ///     </para>
    /// </summary>
    public void StepForward()
    {
        int next = CurrentFrameIndex + 1;
        if (next < 0 || next >= TotalFrames || _frames is not { } f)
        {
            return;
        }

        // Readiness precondition: the codeable form of "the loop is paused
        // during a user seek and resumes on the new instance." A null tracker (cold start) or a
        // tracker not sitting exactly at the current frame (async discrete seek still in flight) both
        // fail this and route through the discrete path, which re-establishes the instance.
        if (AuthoritativeTracker is not { } tracker || tracker.CurrentFrameIndex != CurrentFrameIndex)
        {
            SeekToFrame(next);
            return;
        }

        if (_applying)
        {
            return;
        }

        // Cancel any in-flight async discrete seek so a late completion can't clobber the stepped
        // instance.
        CancelInFlightSeek?.Invoke();

        // Snapshot the current fields BEFORE stepping so EntityTab's delta highlighting survives.
        Dictionary<int, Dictionary<string, object?>>? prevSnapshot = tracker.SnapshotCurrentFields();

        _applying = true;
        try
        {
            ApplyLightSeek?.Invoke(next);
        }
        finally
        {
            _applying = false;
        }

        tracker.AdvanceOneFrame(f[next]);

        CurrentFrameIndex = next;
        CurrentTick = f[next].ServerTick;

        StepEntityRebuild?.Invoke(tracker, prevSnapshot);
        NotifyFrameChanged?.Invoke(next);

        // The in-place forward step mutated the authoritative tracker synchronously — push to
        // modules so the 2D viewport advances on a paused nav-bar frame-step (StepBack/SeekToFrame route
        // through PublishTracker; this is the one move that steps in place and would otherwise be silent).
        RequestCoalescedAdvance();
    }

    /// <summary>Discrete backward step (always a discrete re-seek).</summary>
    public void StepBack() => SeekToFrame(CurrentFrameIndex - 1);

    /// <summary>Seeks to the last frame.</summary>
    public void SeekToEnd()
    {
        if (TotalFrames > 0)
        {
            SeekToFrame(TotalFrames - 1);
        }
    }

    // ── Play loop ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Starts forward-only, snap-to-tick auto-play paced at the demo tick rate (× <see cref="Speed" />).
    ///     No-op at end of demo. Establishes the authoritative tracker first if needed (a discrete
    ///     seek to the current frame), so the loop's readiness skip cleanly covers the gap until it
    ///     lands. The per-tick advance runs SYNCHRONOUSLY on the UI thread:
    ///     a single <c>AdvanceOneFrame</c> is sub-millisecond, race-free (same thread mutates then the
    ///     module reads), and the only viable path on WASM.
    /// </summary>
    [RelayCommand]
    public void Play()
    {
        if (IsPlaying || !HasDemo || CurrentFrameIndex + 1 >= TotalFrames)
        {
            return;
        }

        // Establish the instance if a demo is loaded but nothing has been seeked yet (cold start).
        if (CurrentFrameIndex < 0)
        {
            SeekToFrame(0);
        }
        else if (AuthoritativeTracker is null || AuthoritativeTracker.CurrentFrameIndex != CurrentFrameIndex)
        {
            // Not ready (e.g. position synced but no tracker) — kick a discrete seek; the readiness
            // skip in the tick handler covers the gap until it lands.
            SeekToFrame(CurrentFrameIndex);
        }

        IsPlaying = true;
        _frameAccumulator = 0;
        _timer = new DispatcherTimer
        {
            Interval = TimerInterval()
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    /// <summary>
    ///     Stops auto-play and snaps the discrete tabs (Parser / Entity / Analysis) to the frame
    ///     playback stopped on — the loop itself never touched them, so this is where they
    ///     catch up. No-op when not playing.
    /// </summary>
    [RelayCommand]
    public void Pause()
    {
        if (!IsPlaying)
        {
            return;
        }

        StopTimer();

        // Snap the discrete tabs to where we stopped. The authoritative tracker already sits at the
        // current frame (the loop stepped it), so this is a light fan-out + a SYNCHRONOUS EntityTab
        // rebuild — NOT an O(N) async re-seek.
        //
        // The guard is load-bearing: during the lean play loop SelectedFrame was never updated,
        // so the snap's ApplyLightSeekFanOut sets SelectedFrame=Frames[current], whose setter echoes
        // back through OnFrameSelected -> SeekToFrame. Without _applying set, that echo would run the
        // FULL discrete fan-out (EntityTab.SeekEntitiesAsync — the debounced from-zero replay) and swap
        // a fresh tracker over the stepped instance. With the guard, the echo's SeekToFrame early-
        // returns; the direct synchronous rebuild inside SnapDiscreteTabsToCurrent still runs.
        if (CurrentFrameIndex >= 0)
        {
            _applying = true;
            try
            {
                SnapDiscreteTabsToCurrent?.Invoke(CurrentFrameIndex);
            }
            finally
            {
                _applying = false;
            }
        }
    }

    /// <summary>Toggles between <see cref="Play" /> and <see cref="Pause" />.</summary>
    [RelayCommand]
    public void TogglePlay()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    // Stops + disposes the timer and clears IsPlaying without the discrete-tab snap (used by the
    // pause-before-seek path and Dispose, where the caller handles the tabs).
    private void StopTimer()
    {
        IsPlaying = false;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        _advancePending = false;
        _frameAccumulator = 0;
    }

    // DispatcherTimer interval. The timer fires at a FIXED rate (the demo tick rate); the play loop scales
    // playback by stepping Speed frames per fire (OnTimerTick: _frameAccumulator += Speed). Speed must scale
    // exactly ONE of those two factors. Pacing the timer at TickRate×Speed AS WELL as stepping ~Speed frames
    // per fire double-applied Speed — playback ran at TickRate×Speed² (0.5× was quarter-speed, 2× quadruple;
    // only 1× was correct). Fixed timer + Speed-scaled step ⇒ frames/sec linear in Speed. WASM cap:
    // browser threads are constrained, so cap the timer rate (on a capped browser thread the frame rate is
    // then thread-limited — a separate, pre-existing constraint, not this Speed bug).
    private TimeSpan TimerInterval()
    {
        double cap = OperatingSystem.IsBrowser() ? 32.0 : 1000.0;
        double timerRate = Math.Clamp(TickRate, 1.0, cap);
        return TimeSpan.FromMilliseconds(1000.0 / timerRate);
    }

    /// <summary>
    ///     Test seam (cadence-invariant gate): the play loop's effective playback rate in frames/second =
    ///     (timer fires/sec) × (frames stepped per fire) = (1000 / intervalMs) × <see cref="Speed" />. Must be
    ///     LINEAR in Speed — the historical bug paced the timer at TickRate×Speed too, making it quadratic.
    /// </summary>
    internal double EffectiveFramesPerSecond() => 1000.0 / TimerInterval().TotalMilliseconds * Speed;

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!IsPlaying || _frames is not { } f)
        {
            StopTimer();
            return;
        }

        // Readiness skip: a discrete async seek is landing — don't step a
        // mismatched / null instance. Cheap int compare; the loop resumes once the seek publishes.
        if (AuthoritativeTracker is not { } tracker || tracker.CurrentFrameIndex != CurrentFrameIndex)
        {
            return;
        }

        // Speed accumulator: integer part = frames to step this tick; fraction
        // carries forward. Handles <1× and >1× uniformly. We never skip DECODING a frame — only
        // NOTIFYING about intermediate ones (the coalesced push below).
        _frameAccumulator += Speed;
        int stepCount = (int)_frameAccumulator;
        _frameAccumulator -= stepCount;
        if (stepCount <= 0)
        {
            return;
        }

        int stepped = 0;
        for (int k = 0; k < stepCount; k++)
        {
            int next = CurrentFrameIndex + 1;
            if (next >= f.Count)
            {
                break;
            }

            tracker.AdvanceOneFrame(f[next]);
            CurrentFrameIndex = next;
            CurrentTick = f[next].ServerTick;
            stepped++;
        }

        if (stepped > 0)
        {
            // Lean per-tick fan-out: only the coalesced Advanced push. The frame readout no
            // longer needs an explicit callback — CurrentFrameIndex is set above (raising
            // PropertyChanged), which the NavStrip's NavFrameText observes directly. No SelectedFrame=,
            // no Analysis seek, no EntityTab rebuild — those are deferred to Pause().
            RequestCoalescedAdvance();
        }

        // Auto-pause at the end of the demo.
        if (CurrentFrameIndex + 1 >= f.Count)
        {
            Pause();
        }
    }

    // Coalesced Advanced push: at most one per render frame regardless of Speed. If a push is
    // already queued for this render frame, don't queue another — the tracker keeps stepping, but the
    // UI is notified once.
    private void RequestCoalescedAdvance()
    {
        if (_advancePending)
        {
            return;
        }

        _advancePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _advancePending = false;
            Advanced?.Invoke(new PlaybackFrame(CurrentFrameIndex, CurrentTick));
        }, DispatcherPriority.Render);
    }

    /// <summary>
    ///     Reflects an externally-driven position change (e.g. the legacy navigation methods that set
    ///     <c>ParserTab.SelectedFrame</c> directly) into the controller's observable state without
    ///     re-running the fan-out. Used by the shell to keep the controller authoritative while the
    ///     pre-existing nav code paths still exist.
    /// </summary>
    public void SyncPositionFromShell(int frameIndex)
    {
        CurrentFrameIndex = frameIndex;
        if (_frames is { } f && frameIndex >= 0 && frameIndex < f.Count)
        {
            CurrentTick = f[frameIndex].ServerTick;
        }
    }
}
