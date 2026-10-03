#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Drives each pack's <see cref="IPackLifecycle" /> from the gate, in both directions, on real
///     transitions only: the resolved <see cref="IFeatureGate.IsEnabled" /> of the pack's id is compared with
///     the state the lifecycle was last put in, so a settings write that leaves the pack where it was does
///     nothing. <see cref="Start" /> is the startup pass (<see cref="PackStartReason.Startup" />); every later
///     <see cref="IFeatureGate.Changed" /> is a live toggle. Off to on runs the same loads startup runs and
///     then re-polls the library so the pack's evaluators pick up every demo they now want; on to off hands
///     the lifecycle its release and cancels an enable still in flight through its token.
///     <para>
///         On a fresh install the first-run wizard asks whether the pack is on, so while it is still to ask
///         (<c>waitForFirstRun</c>) nothing starts: the wizard's write is a gate change like any other and
///         starts the pack if its answer resolves on. The browser never shows the wizard and never waits.
///     </para>
/// </summary>
public sealed class PackSwitch : IDisposable
{
    private readonly IFeatureGate _gate;
    private readonly Func<IFeaturePack, IPackLifecycle?> _lifecycleFor;
    private readonly IReadOnlyList<IFeaturePack> _packs;
    private readonly IDemoProcessingQueue? _queue;
    private readonly Action _reconsider;
    private readonly Dictionary<string, PackState> _states = new(StringComparer.Ordinal);
    private readonly Func<bool> _waitForFirstRun;
    private bool _disposed;
    private bool _started;

    /// <param name="packs">Every pack, in composition order.</param>
    /// <param name="gate">The live gate; <see cref="IFeatureGate.Changed" /> arrives on the UI thread in the app.</param>
    /// <param name="lifecycleFor">The pack's lifecycle, or null for a pack without one.</param>
    /// <param name="queue">The processing queue the re-poll runs in; null runs it on the pool.</param>
    /// <param name="reconsider">Re-polls every evaluator over the library (the coordinator's capacity re-feed).</param>
    /// <param name="waitForFirstRun">True while the first-run wizard has still to ask about the packs.</param>
    public PackSwitch(IReadOnlyList<IFeaturePack> packs, IFeatureGate gate, Func<IFeaturePack, IPackLifecycle?> lifecycleFor,
        IDemoProcessingQueue? queue, Action reconsider, Func<bool>? waitForFirstRun = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(lifecycleFor);
        ArgumentNullException.ThrowIfNull(reconsider);
        _packs = packs;
        _gate = gate;
        _lifecycleFor = lifecycleFor;
        _queue = queue;
        _reconsider = reconsider;
        _waitForFirstRun = waitForFirstRun ?? (static () => false);
    }

    /// <summary>The last enable or disable requested of every pack, for a caller that needs the loads or the release done.</summary>
    public Task Pending => Task.WhenAll(_states.Values.Select(s => s.Pending));

    /// <summary>Enables every pack that resolves on, unless the wizard is still to ask, then watches the gate.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Apply(PackStartReason.Startup);
        _gate.Changed += OnGateChanged;
    }

    /// <summary>True while <paramref name="pack" />'s lifecycle has been enabled and not disabled since.</summary>
    public bool IsOn(IFeaturePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return _states.TryGetValue(pack.Id, out PackState? state) && state.Enabled;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _gate.Changed -= OnGateChanged;
        }

        foreach (PackState state in _states.Values)
        {
            state.Enabling?.Cancel();
        }
    }

    private void OnGateChanged(object? sender, EventArgs e) => Apply(PackStartReason.EnabledInSession);

    private void Apply(PackStartReason reason)
    {
        if (_disposed || _waitForFirstRun())
        {
            return;
        }

        foreach (IFeaturePack pack in _packs)
        {
            if (!_states.TryGetValue(pack.Id, out PackState? state))
            {
                state = new PackState();
                _states[pack.Id] = state;
            }

            bool on = _gate.IsEnabled(pack.FeatureId);
            if (on == state.Enabled)
            {
                continue;
            }

            state.Enabled = on;
            if (on)
            {
                Enable(pack, state, reason);
            }
            else
            {
                Disable(pack, state);
            }
        }
    }

    private void Enable(IFeaturePack pack, PackState state, PackStartReason reason)
    {
        if (_lifecycleFor(pack) is not { } lifecycle)
        {
            return;
        }

        CancellationTokenSource enabling = new();
        state.Enabling = enabling;
        Task loads;
        try
        {
            loads = lifecycle.OnEnabledAsync(reason, enabling.Token);
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(Log, "enable pack " + pack.Id, ex);
            return;
        }

        if (reason != PackStartReason.EnabledInSession)
        {
            state.Pending = loads;
            return;
        }

        // The backfill (design section 8): the pack's evaluators now want every demo whose pack fields are
        // missing or stale. The re-poll is a queue item behind the loads, owned by the pack so a switch-off
        // cancels it, and skipped when that happened before it ran.
        Task reconsider = QueueWork.Run(_queue, QueueJobKind.SectionCompute, LabelOf(pack) + ": find demos to re-index",
            pack.FeatureId, token =>
            {
                if (!token.IsCancellationRequested && !enabling.IsCancellationRequested)
                {
                    _reconsider();
                }
            }, DemoJobPriority.UserRequested);
        state.Pending = Task.WhenAll(loads, reconsider);
    }

    private void Disable(IFeaturePack pack, PackState state)
    {
        state.Enabling?.Cancel();
        state.Enabling = null;
        if (_lifecycleFor(pack) is not { } lifecycle)
        {
            return;
        }

        try
        {
            state.Pending = lifecycle.OnDisabledAsync();
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(Log, "disable pack " + pack.Id, ex);
        }
    }

    private static string LabelOf(IFeaturePack pack) =>
        pack.Features.FirstOrDefault(d => d.Id == pack.FeatureId)?.Label ?? pack.Id;

    private static ILogger Log => DiagnosticsLog.CreateLogger(AppLog.ShellCategory);

    private sealed class PackState
    {
        public bool Enabled { get; set; }
        public CancellationTokenSource? Enabling { get; set; }
        public Task Pending { get; set; } = Task.CompletedTask;
    }
}
