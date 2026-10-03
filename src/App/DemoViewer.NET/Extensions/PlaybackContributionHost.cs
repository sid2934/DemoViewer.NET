#region

using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Attaches every pack's <see cref="IPlaybackContribution" /> to one 2D Playback tab, and keeps the set
///     in step with the gate: a pack's contributions are attached only while its umbrella id resolves on,
///     and a live toggle attaches or detaches them without a tab rebuild. A null gate reads every pack as
///     on, the designer and unit-test path.
/// </summary>
public sealed class PlaybackContributionHost
{
    private readonly IFeatureGate? _gate;
    private readonly IReadOnlyList<(IFeaturePack Pack, IReadOnlyList<IPlaybackContribution> Contributions)> _packs;

    /// <param name="packs">Each pack with its playback contributions, in pack order.</param>
    /// <param name="gate">The live gate, or null for a host that does not gate.</param>
    public PlaybackContributionHost(IEnumerable<(IFeaturePack Pack, IReadOnlyList<IPlaybackContribution> Contributions)> packs,
        IFeatureGate? gate)
    {
        ArgumentNullException.ThrowIfNull(packs);
        _packs = [.. packs];
        _gate = gate;
    }

    /// <summary>The host over every pack's collected contributions.</summary>
    internal static PlaybackContributionHost From(PackContributionSet contributions, IFeatureGate? gate)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        return new PlaybackContributionHost(contributions.Packs.Select(p => (p.Pack, p.PlaybackContributions)), gate);
    }

    /// <summary>
    ///     Attaches the enabled packs' contributions to <paramref name="surface" /> and follows the gate from
    ///     here. Disposing detaches everything and stops following.
    /// </summary>
    public IDisposable Attach(IPlaybackSurface surface, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(context);
        return new Binding(this, surface, context);
    }

    private sealed class Binding : IDisposable
    {
        private readonly HashSet<IPlaybackContribution> _attached = [];
        private readonly IModuleContext _context;
        private readonly PlaybackContributionHost _host;
        private readonly IPlaybackSurface _surface;
        private bool _disposed;

        public Binding(PlaybackContributionHost host, IPlaybackSurface surface, IModuleContext context)
        {
            _host = host;
            _surface = surface;
            _context = context;
            Apply();
            if (_host._gate is not null)
            {
                _host._gate.Changed += OnGateChanged;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_host._gate is not null)
            {
                _host._gate.Changed -= OnGateChanged;
            }

            foreach (IPlaybackContribution contribution in _attached)
            {
                contribution.Detach();
            }

            _attached.Clear();
        }

        // Apply touches the surface (timeline contributors, an open pane) with no dispatch of its own: it
        // relies on IFeatureGate.Changed arriving on the UI thread, which FeatureGate marshals.
        private void OnGateChanged(object? sender, EventArgs e) => Apply();

        private void Apply()
        {
            if (_disposed)
            {
                return;
            }

            foreach ((IFeaturePack pack, IReadOnlyList<IPlaybackContribution> contributions) in _host._packs)
            {
                bool on = _host._gate?.IsEnabled(pack.FeatureId) ?? true;
                foreach (IPlaybackContribution contribution in contributions)
                {
                    if (on && _attached.Add(contribution))
                    {
                        contribution.Attach(_surface, _context);
                    }
                    else if (!on && _attached.Remove(contribution))
                    {
                        contribution.Detach();
                    }
                }
            }
        }
    }
}
