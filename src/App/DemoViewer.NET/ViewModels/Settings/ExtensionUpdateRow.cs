#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     The update line under an extension's master row in Settings (strat-book-plugin.md §7.10): the last
///     check's verdict in user terms, a Check button, an Update button while a version is offered, and the
///     download's progress while it runs. One per declared pack. A row with no service (the browser head)
///     says updates come with the app and offers nothing.
/// </summary>
public sealed partial class ExtensionUpdateRow : ObservableObject, IDisposable
{
    private readonly ExtensionUpdateService? _service;
    private readonly Action? _onChecked;
    private CancellationTokenSource? _download;
    private bool _disposed;

    /// <summary>The last check's verdict, or null before the first check this run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    [NotifyPropertyChangedFor(nameof(OfferedVersion))]
    private ExtensionUpdateState? _state;

    /// <summary>True while a check is in flight.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    private bool _isChecking;

    /// <summary>True while a download is in flight; the row shows progress and a Cancel button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyPropertyChangedFor(nameof(ShowCheck))]
    private bool _isDownloading;

    /// <summary>Bytes received so far, while downloading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private long _bytesReceived;

    /// <summary>The download's size, from the feed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private long _totalBytes;

    /// <summary>Why the last download was refused, in user terms; null when it was not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    private string? _failure;

    /// <param name="packId">The extension id.</param>
    /// <param name="service">The updater, or null where updates do not apply (the browser head).</param>
    /// <param name="onChecked">Runs after every finished check, so the owner can record the time.</param>
    internal ExtensionUpdateRow(string packId, ExtensionUpdateService? service, Action? onChecked = null)
    {
        ArgumentNullException.ThrowIfNull(packId);
        PackId = packId;
        _service = service;
        _onChecked = onChecked;
        _state = service?.LastState(packId);
    }

    /// <summary>The extension id.</summary>
    public string PackId { get; }

    /// <summary>True when this host can update extensions at all.</summary>
    public bool IsSupported => _service is not null;

    /// <summary>The Check button shows whenever updates apply here and no download is running.</summary>
    public bool ShowCheck => IsSupported && !IsDownloading;

    /// <summary>The Check button is enabled while nothing else is in flight.</summary>
    public bool CanCheck => IsSupported && !IsChecking && !IsDownloading;

    /// <summary>The Update button shows while a version is offered and nothing is in flight.</summary>
    public bool CanUpdate => IsSupported && !IsChecking && !IsDownloading && State?.Status == ExtensionUpdateStatus.UpdateAvailable;

    /// <summary>The offered version's text, for the Update button's tooltip; null when none.</summary>
    public string? OfferedVersion => State?.Offered?.Version.ToString();

    /// <summary>Whether <see cref="Failure" /> is set.</summary>
    public bool HasFailure => Failure is not null;

    /// <summary>Whether <see cref="Message" /> has anything to show.</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>0 to 100 while downloading.</summary>
    public int ProgressPercent => TotalBytes <= 0 ? 0 : (int)Math.Min(100, BytesReceived * 100 / TotalBytes);

    /// <summary>
    ///     The line itself. Empty before the first check of the run (the button is the invitation); otherwise
    ///     one sentence per state, naming versions and never the word "pack".
    /// </summary>
    public string Message
    {
        get
        {
            if (!IsSupported)
            {
                return "Updates come with the app.";
            }

            if (IsChecking)
            {
                return "Checking for updates…";
            }

            if (IsDownloading)
            {
                string version = State?.Offered?.Version.ToString() ?? string.Empty;
                return $"Downloading {version}: {Megabytes(BytesReceived)} of {Megabytes(TotalBytes)} MB";
            }

            return State is null ? string.Empty : Describe(State);
        }
    }

    /// <summary>The sentence for <paramref name="state" />, the same one the row shows.</summary>
    public static string Describe(ExtensionUpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Status switch
        {
            ExtensionUpdateStatus.Unknown => $"Updates cannot be checked: {state.Error}.",
            ExtensionUpdateStatus.UpToDate => "Up to date.",
            ExtensionUpdateStatus.UpdateAvailable => $"{state.Offered!.Version} available.",
            ExtensionUpdateStatus.PendingRestart => $"{state.Pending} installed, restart to use it.",
            ExtensionUpdateStatus.NeedsNewerApp => NeedsNewerApp(state),
            ExtensionUpdateStatus.FeedUnreachable => $"Could not check for updates: {state.Error}.",
            ExtensionUpdateStatus.FeedInvalid => $"Could not check for updates: {state.Error}.",
            _ => string.Empty
        };
    }

    /// <summary>Replaces the verdict, as a finished check does.</summary>
    internal void Apply(ExtensionUpdateState state)
    {
        Failure = null;
        State = state;
    }

    /// <summary>Asks the feed about this extension alone.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (_service is null || IsChecking || IsDownloading)
        {
            return;
        }

        IsChecking = true;
        try
        {
            ExtensionUpdateState state = await _service.CheckAsync(PackId);
            Apply(state);
            _onChecked?.Invoke();
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Downloads and stages the offered version; the next start loads it.</summary>
    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (_service is null || IsDownloading || IsChecking || State?.Offered is not { } offered)
        {
            return;
        }

        IsDownloading = true;
        Failure = null;
        BytesReceived = 0;
        TotalBytes = offered.Size;
        _download = new CancellationTokenSource();
        try
        {
            Progress<ExtensionDownloadProgress> progress = new(p =>
            {
                BytesReceived = p.BytesReceived;
                TotalBytes = p.TotalBytes;
            });
            StageResult result = await _service.DownloadAndStageAsync(offered, progress, _download.Token);
            switch (result.Outcome)
            {
                case StageOutcome.Installed:
                case StageOutcome.AlreadyInstalled:
                    State = State! with
                    {
                        Status = ExtensionUpdateStatus.PendingRestart,
                        Pending = offered.Version,
                        Offered = null
                    };
                    break;
                case StageOutcome.Refused:
                    Failure = $"Update {offered.Version} could not be installed: {result.Detail}.";
                    break;
                case StageOutcome.Cancelled:
                    break;
            }
        }
        finally
        {
            CancellationTokenSource finished = _download;
            _download = null;
            finished.Dispose();
            IsDownloading = false;
        }
    }

    /// <summary>Stops the download; nothing is left on disk.</summary>
    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    /// <summary>Stops a download the window's closing leaves behind.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _download?.Cancel();
    }

    private static string NeedsNewerApp(ExtensionUpdateState state)
    {
        string version = state.Latest?.Version.ToString() ?? "A newer version";
        return state.LatestCompatibility switch
        {
            PackCompatibility.HostContractMismatch h => $"{version} available but needs app contract {h.Required} (this app provides {h.Actual}).",
            PackCompatibility.Cs2DemoKitMismatch k => $"{version} available but needs CS2DemoKit {k.Required} (this app ships {k.Actual}).",
            PackCompatibility.AppTooOld a => $"{version} available but needs app {a.Required} or newer (this is {a.Actual}).",
            _ => $"{version} available but cannot run on this app."
        };
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
}
