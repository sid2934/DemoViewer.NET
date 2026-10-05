#region

using System.ComponentModel;
using System.Windows.Input;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     A chip the shell shows on the status strip on someone else's behalf: an extension's contributed chip or
///     a first-party export job's. The shell adds <see cref="Chip" /> while <see cref="IsShown" /> is true and
///     <see cref="FeatureId" /> is on, and re-checks on <see cref="Changed" />.
/// </summary>
internal interface IShellChip
{
    /// <summary>Unique among the strip's contributed chips.</summary>
    string Id { get; }

    /// <summary>The feature the chip shows under.</summary>
    string FeatureId { get; }

    /// <summary>The chip to show, or null while there is nothing to show.</summary>
    StatusChipViewModel? Chip { get; }

    /// <summary>True while <see cref="Chip" /> belongs on the strip.</summary>
    bool IsShown { get; }

    /// <summary>Raised on the UI thread after <see cref="Chip" /> or <see cref="IsShown" /> may have moved.</summary>
    event Action? Changed;

    /// <summary>Starts watching the owner. Called once, by the shell, on the UI thread.</summary>
    void Attach();

    /// <summary>Stops watching the owner.</summary>
    void Detach();
}

/// <summary>
///     An extension's contributed chip. The host owns the <see cref="StatusChipViewModel" /> the strip binds and
///     copies the extension's state into it; every read of the extension's source runs under its guard, and a
///     change the extension raises on any thread is applied on the UI thread.
/// </summary>
internal sealed class HostStatusChip : IShellChip
{
    private readonly IStatusChipSource _source;
    private readonly ExtensionGuard _guard;
    private readonly Action<Action> _toUiThread;
    private bool _attached;
    private ICommand? _wrappedFrom;

    /// <param name="contribution">The extension's contribution.</param>
    /// <param name="featureId">The feature the chip shows under, stamped by the collector.</param>
    /// <param name="guard">The owning extension's guard.</param>
    /// <param name="toUiThread">Runs the re-read on the UI thread.</param>
    public HostStatusChip(StatusChipContribution contribution, string featureId, ExtensionGuard guard, Action<Action> toUiThread)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(toUiThread);
        Id = contribution.Id;
        FeatureId = featureId;
        _source = contribution.Source;
        _guard = guard;
        _toUiThread = toUiThread;
    }

    public string Id { get; }

    public string FeatureId { get; }

    /// <summary>The host's own chip; the same instance for the life of the session.</summary>
    public StatusChipViewModel Chip { get; } = new();

    StatusChipViewModel? IShellChip.Chip => Chip;

    public bool IsShown { get; private set; }

    public event Action? Changed;

    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _guard.Run("status chip subscribe", () => _source.PropertyChanged += OnSourceChanged);
        Refresh();
    }

    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _guard.Run("status chip unsubscribe", () => _source.PropertyChanged -= OnSourceChanged);
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => _toUiThread(Refresh);

    // A source that throws on any read hides its chip rather than showing half-copied state.
    private void Refresh()
    {
        if (!_attached)
        {
            return;
        }

        bool shown = _guard.Run("status chip", () =>
        {
            Chip.Label = _source.Label ?? "";
            Chip.DotState = _source.DotState;
            Chip.IsPulsing = _source.IsPulsing;
            Chip.IsHollow = _source.IsHollow;
            Chip.Tooltip = _source.Tooltip;
            Chip.FlyoutContent = _source.FlyoutContent;
            ICommand? action = _source.PrimaryAction;
            if (!ReferenceEquals(action, _wrappedFrom))
            {
                _wrappedFrom = action;
                Chip.PrimaryAction = action is null ? null : new GuardedCommand(action, _guard);
            }

            return _source.IsShown;
        }, false);

        IsShown = shown;
        Changed?.Invoke();
    }

    // The chip's click runs the extension's command; a throw from it is the extension's fault, not the strip's.
    private sealed class GuardedCommand(ICommand inner, ExtensionGuard guard) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => guard.Run("status chip command", () => inner.CanExecuteChanged += value);
            remove => guard.Run("status chip command", () => inner.CanExecuteChanged -= value);
        }

        public bool CanExecute(object? parameter) => guard.Run("status chip command", () => inner.CanExecute(parameter), false);

        public void Execute(object? parameter) => guard.Run("status chip command", () => inner.Execute(parameter));
    }
}

/// <summary>
///     A first-party export job's chip. The job builds lazily, on the first Export, long after the strip
///     exists, so the slot exists first and the job's status is mounted into it later. Shown while the job
///     runs, or while a finished result has not been dismissed.
/// </summary>
internal sealed class ExportChipSlot(string id, string featureId) : IShellChip
{
    private Playback2DExportStatusViewModel? _status;
    private bool _dismissed;

    public string Id { get; } = id;

    public string FeatureId { get; } = featureId;

    public StatusChipViewModel? Chip => _status?.Chip;

    /// <summary>An idle job shows nothing: the mount happens on the first Export, long before the first Start.</summary>
    public bool IsShown => _status is { } status && (status.IsRunning || !status.IsIdle && !_dismissed);

    public event Action? Changed;

    public void Attach()
    {
    }

    public void Detach()
    {
    }

    /// <summary>Mounts the status the export job publishes. Idempotent.</summary>
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
        Changed?.Invoke();
    }

    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Playback2DExportStatusViewModel.IsIdle)
            or nameof(Playback2DExportStatusViewModel.IsRunning)))
        {
            return;
        }

        // A newly running job brings a dismissed chip back.
        if (_status is { IsRunning: true })
        {
            _dismissed = false;
        }

        Changed?.Invoke();
    }

    private void OnDismissRequested(object? sender, EventArgs e)
    {
        _dismissed = true;
        Changed?.Invoke();
    }
}
