#region

using System.ComponentModel;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The Strat Book export chip's mount point (step-authoring.md §3.6): the Strat Book tab builds its
///     export job lazily, on the first Export, long after the shell exists, so the shell cannot hold the
///     chip directly the way it holds the 2D export one. <see cref="Mount" /> is the pack's side of that
///     hand-off; <see cref="IContributedStatusChip" /> is the shell's.
/// </summary>
public sealed class StratBookExportChipSlot : IContributedStatusChip
{
    private Playback2DExportStatusViewModel? _status;
    private bool _dismissed;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public StatusChipViewModel? Chip => _status?.Chip;

    /// <summary>
    ///     Shown while running, or while a finished result has not been dismissed. An idle mapper shows
    ///     nothing: <see cref="Mount" /> runs on the first Export, long before the first Start.
    /// </summary>
    public bool IsShown => _status is { } status && (status.IsRunning || !status.IsIdle && !_dismissed);

    /// <summary>Mounts the mapper the Strat Book tab built over its job service. Idempotent.</summary>
    public void Mount(Playback2DExportStatusViewModel status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (ReferenceEquals(_status, status))
        {
            return;
        }

        if (_status is { } previous)
        {
            previous.DismissRequested -= OnDismissRequested;
            previous.PropertyChanged -= OnStatusPropertyChanged;
        }

        _status = status;
        _dismissed = false;
        status.DismissRequested += OnDismissRequested;
        status.PropertyChanged += OnStatusPropertyChanged;
        Raise();
    }

    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Playback2DExportStatusViewModel.IsIdle)
            or nameof(Playback2DExportStatusViewModel.IsRunning)))
        {
            return;
        }

        // A newly-running job un-dismisses, so the chip comes back for a fresh export.
        if (_status is { IsRunning: true })
        {
            _dismissed = false;
        }

        Raise();
    }

    private void OnDismissRequested(object? sender, EventArgs e)
    {
        _dismissed = true;
        Raise();
    }

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
    }
}
