#region

using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     The placeholder tab's VM. Subscribes to <c>IModuleContext.Advanced</c> on activation and
///     unsubscribes on deactivation, counting pushes — the concrete proof that only the active
///     module receives pushes; inactive modules do zero per-tick work). Also exercises the on-activation
///     resync pull (<c>CurrentPlayers</c>) so the host player-join is reachable end-to-end.
/// </summary>
public sealed partial class PlaceholderTabViewModel : ObservableObject, IWorkspaceTabViewModel
{
    private IModuleContext? _context;

    [ObservableProperty]
    private string _status = "Module Sandbox — inactive";

    /// <summary>Number of Advanced pushes received while active (read by tests).</summary>
    public int PushCount { get; private set; }

    public void OnActivated(IModuleContext context)
    {
        _context = context;
        context.Advanced += OnAdvanced;

        // On-activation resync: pull the current join so a tab activated mid-playback is correct.
        int players = context.CurrentPlayers.Count;
        Status = $"Module Sandbox — active · {players} players joined · 0 pushes";
    }

    public void OnDeactivated()
    {
        if (_context is not null)
        {
            _context.Advanced -= OnAdvanced;
            _context = null;
        }

        Status = $"Module Sandbox — inactive · {PushCount} pushes received";
    }

    private void OnAdvanced(IPlaybackSnapshot snapshot)
    {
        PushCount++;
        Status = $"Module Sandbox — active · frame {snapshot.FrameIndex} · " +
                 $"{snapshot.Players.Count} players · {PushCount} pushes";
    }
}
