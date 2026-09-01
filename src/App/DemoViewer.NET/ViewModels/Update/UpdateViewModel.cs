#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Update;

#endregion

namespace DemoViewer.NET.ViewModels.Update;

/// <summary>
///     Drives the update banner and the Settings update controls. Owns the whole client-side update
///     story, so the shell only has to expose it and call <see cref="CheckOnStartupAsync" /> once.
///     <para>
///         <b>Nothing downloads without consent.</b> A check is cheap (one feed request); the payload
///         is a ~110 MB full package, because the release currently ships no delta packages. So the
///         banner announces and waits: <see cref="UpdateAndRestartAsync" /> is the only path that
///         spends bandwidth, and it only runs on a click.
///     </para>
///     <para>
///         Null <see cref="IUpdateService" /> (Browser, tests, designer, dev runs) is a first-class
///         state, not an error: the banner stays hidden and Settings reports that updates are
///         unavailable rather than offering a button that cannot work.
///     </para>
/// </summary>
public sealed partial class UpdateViewModel : ViewModelBase
{
    private static UpdateViewModel? _shared;
    private readonly IUpdateService? _service;

    /// <summary>The version offered by the last check; drives the banner text.</summary>
    [ObservableProperty]
    private string? _availableVersion;

    /// <summary>0–100 download progress, driven by Velopack's reporter.</summary>
    [ObservableProperty]
    private int _downloadProgress;

    /// <summary>True while a manual Settings check is in flight (disables the button).</summary>
    [ObservableProperty]
    private bool _isChecking;

    /// <summary>True while the package is downloading; the banner swaps to a progress row.</summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>
    ///     Banner visibility. Set only by a check that found something, and cleared by Later or by a
    ///     started download: the banner never coexists with progress.
    /// </summary>
    [ObservableProperty]
    private bool _isUpdateAvailable;

    /// <summary>
    ///     Result line for the Settings pane: "You're up to date", a version, or a failure reason.
    ///     Empty until the user checks, so the pane does not open with a stale verdict.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Constructs against the host-provided service; null disables every path.</summary>
    public UpdateViewModel(IUpdateService? service) => _service = service;

    /// <summary>
    ///     The one instance the shell banner and the Settings pane both bind to. They MUST share:
    ///     a check started from Settings has to raise the banner, and the underlying service holds
    ///     the resolved update between check and apply: two instances would mean Settings finds an
    ///     update the banner cannot install.
    ///     <para>
    ///         Created lazily rather than in a static initializer, because the Desktop entry point
    ///         assigns <c>AppHostHooks.UpdateServiceFactory</c> during <c>Main</c> and a type-load
    ///         race would silently capture a null factory. Settable so tests can inject a fake
    ///         service; assign null to reset between cases.
    ///     </para>
    /// </summary>
    public static UpdateViewModel Shared
    {
        get => _shared ??= new UpdateViewModel(AppHostHooks.UpdateServiceFactory?.Invoke());
        set => _shared = value;
    }

    /// <summary>True when this build can update at all: gates the Settings controls.</summary>
    public bool IsSupported => _service is not null;

    /// <summary>
    ///     Running version for display in Settings. Falls back to a plain statement rather than an
    ///     empty cell: "not a packaged build" is the useful answer during a dev run.
    /// </summary>
    public string CurrentVersionDisplay => _service?.CurrentVersion ?? "not a packaged build";

    /// <summary>
    ///     The launch check. Silent by design: it writes <see cref="StatusMessage" /> only on
    ///     success-with-update, so a user who starts up offline sees nothing at all rather than an
    ///     error they did not ask for. Failures are swallowed here and surface only if they later
    ///     press Check in Settings.
    /// </summary>
    public async Task CheckOnStartupAsync(CancellationToken ct = default)
    {
        if (_service is null)
        {
            return;
        }

        UpdateCheckResult result = await _service.CheckAsync(ct).ConfigureAwait(true);
        if (result.HasUpdate)
        {
            AvailableVersion = result.Version;
            IsUpdateAvailable = true;
        }
    }

    /// <summary>
    ///     The Settings button. Unlike the startup check this always reports: the user asked, so
    ///     silence would read as a broken button.
    /// </summary>
    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (_service is null || IsChecking)
        {
            return;
        }

        IsChecking = true;
        StatusMessage = "Checking…";
        try
        {
            UpdateCheckResult result = await _service.CheckAsync().ConfigureAwait(true);
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    AvailableVersion = result.Version;
                    IsUpdateAvailable = true;
                    StatusMessage = $"Version {result.Version} is available.";
                    break;
                case UpdateCheckStatus.UpToDate:
                    StatusMessage = "You're up to date.";
                    break;
                case UpdateCheckStatus.NotSupported:
                    StatusMessage = "Updates apply to installed builds only.";
                    break;
                default:
                    StatusMessage = $"Check failed: {result.Message}";
                    break;
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    ///     Consent path: download, then restart into the new version. The banner is replaced by a
    ///     progress row for the duration, because a ~110 MB download with no visible feedback reads
    ///     as a hang.
    /// </summary>
    [RelayCommand]
    private async Task UpdateAndRestartAsync()
    {
        if (_service is null || IsDownloading)
        {
            return;
        }

        IsDownloading = true;
        IsUpdateAvailable = false;
        DownloadProgress = 0;
        try
        {
            Progress<int> progress = new(p => DownloadProgress = p);
            bool ok = await _service.DownloadAndApplyAsync(progress).ConfigureAwait(true);
            if (!ok)
            {
                // Still running, so the apply failed. Put the banner back, the user asked for this
                // and should be able to retry, and say so in Settings.
                IsUpdateAvailable = true;
                StatusMessage = "Update failed to download. You can retry, or download the installer manually.";
            }
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Dismisses the banner for this run. The next launch checks again.</summary>
    [RelayCommand]
    private void Dismiss() => IsUpdateAvailable = false;
}
