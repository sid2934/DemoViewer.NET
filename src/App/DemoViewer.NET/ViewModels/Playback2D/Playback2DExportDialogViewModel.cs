#region

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.Export;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.ViewModels.Playback2D;

/// <summary>One selectable frame range, resolved by the caller before the dialog opens.</summary>
/// <param name="Label">What the user picks, e.g. "Current round (frames 1204–3980)".</param>
/// <param name="StartFrame">First demo frame, inclusive.</param>
/// <param name="EndFrame">Last demo frame, inclusive.</param>
public sealed record ExportRangeOption(string Label, int StartFrame, int EndFrame);

/// <summary>One selectable output size.</summary>
/// <param name="Label">What the user picks, e.g. "1080p (1920×1080)".</param>
/// <param name="Width">Width in pixels; always even.</param>
/// <param name="Height">Height in pixels; always even.</param>
public sealed record ExportSizeOption(string Label, int Width, int Height);

/// <summary>
///     Fetches the pinned LGPL ffmpeg build: download, verify against the pinned SHA-256, show the
///     <c>LICENSE.txt</c> read out of the verified bytes, and install only if that returns true.
///     <para>
///         A delegate rather than a direct <c>FfmpegAcquisition.AcquireAsync</c> call so the dialog's
///         download flow is testable without a network, a 140 MB transfer, or a Windows-x64 machine — the
///         only platform for which a build is pinned at all.
///     </para>
/// </summary>
/// <param name="consent">Shown the offer and the licence; false leaves the disk exactly as it was.</param>
/// <param name="progress">Transfer fraction in [0,1].</param>
/// <param name="ct">Cancels the transfer; leaves no partial file.</param>
public delegate Task<FfmpegLocation> FfmpegAcquire(
    Func<FfmpegDownloadOffer, string, CancellationToken, Task<bool>> consent,
    IProgress<double>? progress,
    CancellationToken ct);

/// <summary>
///     The export dialog. <b>Thin by constraint</b>: it collects choices and hands them to
///     <see cref="IExportJobService" />. Every rule it applies — supported frame rates, even dimensions,
///     the GIF caps, the range — is <c>SceneExportSession</c>'s, so <c>dv2d export</c> enforces exactly the
///     same ones without a line of shared UI code.
///     <para>
///         Every environment dependency is an injected delegate, mirroring
///         <c>HighlightReelDialogViewModel</c>'s proven seams: a null ffmpeg locator means "assume
///         present", which keeps the pure-VM tests off the filesystem and off whatever happens to be
///         installed on the machine running them.
///     </para>
///     <para>
///         <b><see cref="ViewModelBase" />, not <c>ObservableObject</c>, and that is load-bearing.</b> The
///         pane is mounted as <c>&lt;ContentControl Content="{Binding ExportDialog}"/&gt;</c>, which resolves
///         its view through the app <c>ViewLocator</c> — and the locator's <c>Match</c> is
///         <c>data is ViewModelBase</c>. As a bare <c>ObservableObject</c> nothing claimed this VM, the
///         <c>ContentControl</c> fell through to <c>ToString()</c>, and the whole pane rendered as one line
///         of fully-qualified type name for the life of the feature. The alternative fix — widening
///         <c>Match</c> to <c>ObservableObject</c> — would have made the locator claim every row and item
///         view-model in the app, most of which have no <c>…View</c> type and would have started rendering
///         as "Not Found: …" instead of their template. The base class is the narrow end of that choice.
///     </para>
/// </summary>
public sealed partial class Playback2DExportDialogViewModel : ViewModelBase, IDisposable
{
    private readonly FfmpegAcquire? _acquireFfmpeg;
    private readonly Func<AnnotationSession?>? _captureInk;
    private readonly Func<CameraScript> _captureLiveCamera;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<FfmpegLocation>? _ffmpegLocator;
    private readonly Func<bool>? _isLiveSyncSessionActive;
    private readonly IExportJobService? _job;
    private readonly Func<int, int, int, double, int> _outputFrameCount;
    private readonly Action<Action<AppSettings>>? _persistDefaults;

    // Non-null only between the licence arriving and the user answering it. The acquisition is awaiting
    // this task on a pool thread; Accept / Decline complete it from the UI thread.
    private TaskCompletionSource<bool>? _licenseAnswer;

    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    private string _customHeightText = "1080";

    [ObservableProperty]
    private string _customWidthText = "1920";

    /// <summary>
    ///     The one thing that makes the dialog refuse. <b>Blocking only</b> — see
    ///     <see cref="NoticeBanner" />.
    /// </summary>
    [ObservableProperty]
    private string? _errorBanner;

    /// <summary>
    ///     A true-but-harmless remark about the current choices. Never gates <see cref="CanStart" />.
    ///     <para>
    ///         It exists because "this file already exists and will be overwritten" was returned from
    ///         <c>Validate</c> as an <see cref="ErrorBanner" />, and <c>CanStart</c> is
    ///         <c>ErrorBanner is null</c>. The default output path is a constant, so the <b>second export
    ///         ever attempted</b> opened with a red banner and a dead Export button — the failure mode of
    ///         one channel carrying two meanings.
    ///     </para>
    /// </summary>
    [ObservableProperty]
    private string? _noticeBanner;

    [ObservableProperty]
    private bool _includeAnnotations = true;

    /// <summary>
    ///     The master HUD switch, persisted as the original <c>ExportIncludeHud</c>.
    ///     <para>
    ///         It stays because it is the key already in users' settings files, and because the three
    ///         sub-toggles below would otherwise make "no HUD at all" a three-click operation. Off here
    ///         means off regardless of what the three say — the sub-toggles are a composition, not an
    ///         override.
    ///     </para>
    /// </summary>
    [ObservableProperty]
    private bool _includeHud = true;

    /// <summary>Whether <c>hud.clock</c> — the score, round and countdown strip — is burned in.</summary>
    [ObservableProperty]
    private bool _includeHudClock = true;

    /// <summary>Whether <c>hud.killfeed</c> is burned in.</summary>
    [ObservableProperty]
    private bool _includeHudKillFeed = true;

    /// <summary>Whether <c>hud.roster</c> — D3b's player cards down both edges — is burned in.</summary>
    [ObservableProperty]
    private bool _includeHudRoster = true;

    [ObservableProperty]
    private bool _includeVision;

    [ObservableProperty]
    private bool _isFfmpegMissing;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private string _selectedEncoder = EncoderLadder.Auto;

    [ObservableProperty]
    private string _selectedFormat = ExportFormats.WebM;

    [ObservableProperty]
    private int _selectedFps = 60;

    [ObservableProperty]
    private string _selectedQuality = ExportQualities.Standard;

    [ObservableProperty]
    private ExportRangeOption? _selectedRange;

    [ObservableProperty]
    private ExportSizeOption? _selectedSize;

    [ObservableProperty]
    private bool _useCustomSize;

    /// <summary>Creates the dialog view-model.</summary>
    /// <param name="ranges">The frame ranges the user may pick from. At least one.</param>
    /// <param name="defaults">Saved defaults to seed the fields with, or null for the built-ins.</param>
    /// <param name="job">The service Start hands off to. Null makes the dialog inert (design preview).</param>
    /// <param name="captureLiveCamera">
    ///     Snapshots the live host's panes. Called <b>on Start</b>, never on selection — plan D12: mirroring
    ///     the live view is a capture, so panning after pressing Start must change nothing.
    /// </param>
    /// <param name="outputFrameCount">
    ///     Turns a demo range plus fps/speed into an output frame count. The App passes
    ///     <c>TrackerFrameSource.OutputFrameCount</c> so the dialog's GIF-cap check and the source agree;
    ///     the default is the demo-frame count, which is what a pure-VM test wants.
    /// </param>
    /// <param name="ffmpegLocator">Presence probe. Null = assume present, keeping VM tests machine-independent.</param>
    /// <param name="isLiveSyncSessionActive">True while LiveSync holds the machine.</param>
    /// <param name="persistDefaults">Writes the chosen defaults back to settings.</param>
    /// <param name="fileExists">Overwrite probe; defaults to <see cref="File.Exists" />.</param>
    /// <param name="captureInk">
    ///     Freezes the tab's annotation document, called <b>on Start</b> and on the UI thread, exactly like
    ///     <paramref name="captureLiveCamera" />. The snapshot rides the request rather than a field on the
    ///     tab: the job service awaits the heavy-job gate before the runner reads the setup, so a second
    ///     Start — even one the gate then refuses — had already overwritten the ink the first, parked
    ///     export was going to burn in.
    /// </param>
    /// <param name="acquireFfmpeg">
    ///     Fetches the pinned LGPL build for the Download button. Null hides the button entirely, which is
    ///     the correct state on every platform with nothing pinned for it.
    /// </param>
    public Playback2DExportDialogViewModel(
        IReadOnlyList<ExportRangeOption> ranges,
        Playback2DSettings? defaults = null,
        IExportJobService? job = null,
        Func<CameraScript>? captureLiveCamera = null,
        Func<int, int, int, double, int>? outputFrameCount = null,
        Func<FfmpegLocation>? ffmpegLocator = null,
        Func<bool>? isLiveSyncSessionActive = null,
        Action<Action<AppSettings>>? persistDefaults = null,
        Func<string, bool>? fileExists = null,
        Func<AnnotationSession?>? captureInk = null,
        FfmpegAcquire? acquireFfmpeg = null)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        Ranges = new ObservableCollection<ExportRangeOption>(ranges);
        _job = job;
        _captureLiveCamera = captureLiveCamera ?? DefaultCamera;
        _outputFrameCount = outputFrameCount ?? (static (start, end, _, _) => Math.Max(1, end - start + 1));
        _ffmpegLocator = ffmpegLocator;
        _isLiveSyncSessionActive = isLiveSyncSessionActive;
        _persistDefaults = persistDefaults;
        _fileExists = fileExists ?? File.Exists;
        _captureInk = captureInk;
        _acquireFfmpeg = acquireFfmpeg;

        _selectedRange = Ranges.Count > 0 ? Ranges[0] : null;

        Playback2DSettings seed = defaults ?? new Playback2DSettings();
        _selectedFormat = Normalize(seed.ExportFormatId);
        _selectedQuality = ExportQualities.ToId(ExportQualities.ParseOrDefault(seed.ExportQuality));
        _selectedEncoder = NormalizeEncoder(_selectedFormat, seed.ExportEncoder);
        _includeHud = seed.ExportIncludeHud;
        _includeHudClock = seed.ExportIncludeHudClock;
        _includeHudKillFeed = seed.ExportIncludeHudKillFeed;
        _includeHudRoster = seed.ExportIncludeHudRoster;
        _includeAnnotations = seed.ExportIncludeAnnotations;
        _includeVision = seed.ExportIncludeVision;
        _selectedSize = SizePresets.FirstOrDefault(s => s.Width == seed.ExportWidth && s.Height == seed.ExportHeight)
                        ?? SizePresets[0];
        _customWidthText = seed.ExportWidth.ToString(CultureInfo.InvariantCulture);
        _customHeightText = seed.ExportHeight.ToString(CultureInfo.InvariantCulture);
        _outputPath = BuildDefaultPath(seed);

        RebuildFps(seed.ExportFps);
        RebuildEncoders(_selectedEncoder);
        RefreshFfmpegStatus();
        UpdateValidation();
    }

    /// <summary>
    ///     The output size presets. All even, so no preset can trip the yuv420p rule. 720p leads because
    ///     it is the one that exports faster than the clip plays on a CPU — see
    ///     <see cref="Playback2DSettings.ExportWidth" /> for the measurement.
    /// </summary>
    public static IReadOnlyList<ExportSizeOption> SizePresets { get; } =
    [
        new("720p (1280×720)", 1280, 720),
        new("1080p (1920×1080)", 1920, 1080),
        new("1440p (2560×1440)", 2560, 1440),
        new("Square (1080×1080)", 1080, 1080)
    ];

    /// <summary>The container formats, in dialog order.</summary>
    public static IReadOnlyList<string> Formats => ExportFormats.All;

    /// <summary>
    ///     The quality rungs, fastest first — plan P2 D3. They are an intent, not a codec setting: every
    ///     encoder maps the three onto its own rate and speed controls, so "standard" means the same thing
    ///     whether the file is coming off NVENC or off libvpx.
    /// </summary>
    public static IReadOnlyList<string> Qualities => ExportQualities.All;

    /// <summary>
    ///     What <c>--encoder</c> may be for the selected format: <c>auto</c>, <c>software</c>, then each
    ///     ladder rung by name. Re-listed when the format changes, because the two ladders share no rungs.
    ///     <para>
    ///         <b><c>auto</c> is the only entry that cannot fail for an environment reason.</b> Naming a
    ///         rung is taken literally and refused if this machine cannot run it (plan D4) — which is the
    ///         honest behaviour, and the reason the default is not a name.
    ///     </para>
    /// </summary>
    public ObservableCollection<string> AvailableEncoders { get; } = [];

    /// <summary>The ranges the user may export.</summary>
    public ObservableCollection<ExportRangeOption> Ranges { get; }

    /// <summary>The frame rates the selected format supports. Re-listed whenever the format changes.</summary>
    public ObservableCollection<int> AvailableFps { get; } = [];

    /// <summary>True when the dialog would produce a valid export right now.</summary>
    public bool CanStart => ErrorBanner is null;

    /// <summary>The ffmpeg strip is shown when no ffmpeg was found on a host that could otherwise encode.</summary>
    public bool ShowFfmpegStrip => IsFfmpegMissing;

    /// <summary>The strip's explanation copy.</summary>
    public string FfmpegMissingMessage { get; } =
        "Video export needs ffmpeg, which wasn't found on this machine. GIF still works without it.";

    /// <summary>Platform-specific install instructions, plus the drop-in folder.</summary>
    public string FfmpegInstructions { get; } = BuildFfmpegInstructions();

    /// <summary>
    ///     True when a pinned LGPL build exists for this machine <b>and</b> something was injected that can
    ///     fetch it, so the Download button is offered.
    ///     <para>
    ///         Both halves matter. The old spelling was a static that asked only the first question, and it
    ///         was bound to a check box that was <b>ticked by default</b> and read by nothing that could
    ///         act on it — the runner's consent callback was an optional constructor parameter its one
    ///         production caller omitted, so the download rung short-circuited before it began. A tick box
    ///         is not consent to fetch a 140 MB binary anyway; this is a button the user presses, and the
    ///         licence inside the verified archive is shown before anything is written.
    ///     </para>
    /// </summary>
    public bool CanOfferFfmpegDownload => _acquireFfmpeg is not null;

    /// <summary>Transfer fraction in [0,1] while the pinned build downloads.</summary>
    [ObservableProperty]
    private double _ffmpegDownloadFraction;

    /// <summary>True from the moment Download is pressed until the acquisition settles either way.</summary>
    [ObservableProperty]
    private bool _isDownloadingFfmpeg;

    /// <summary>
    ///     The <c>LICENSE.txt</c> read out of the verified archive, or null when there is nothing to
    ///     answer. Non-null is what reveals the accept/decline pair — nothing is extracted until then.
    /// </summary>
    [ObservableProperty]
    private string? _ffmpegLicenseText;

    /// <summary>Why the last download attempt did not produce an ffmpeg. Null when it did, or never ran.</summary>
    [ObservableProperty]
    private string? _ffmpegDownloadStatus;

    /// <summary>The frames this export will produce with the current choices.</summary>
    public int EstimatedFrameCount => SelectedRange is { } range
        ? _outputFrameCount(range.StartFrame, range.EndFrame, SelectedFps, 1.0)
        : 0;

    /// <summary>The resolved output size, honouring the custom fields and snapping them to even.</summary>
    public SKSizeI ResolvedSize
    {
        get
        {
            if (!UseCustomSize)
            {
                ExportSizeOption preset = SelectedSize ?? SizePresets[1];
                return new SKSizeI(preset.Width, preset.Height);
            }

            // Snap DOWN to even rather than refusing: a user typing 1921 meant 1920, and yuv420p's chroma
            // subsampling is not something they should have to know about (plan D8).
            int width = SnapEven(CustomWidthText, 1920);
            int height = SnapEven(CustomHeightText, 1080);
            return new SKSizeI(width, height);
        }
    }

    /// <summary>Starts the export. Refusals surface as <see cref="ErrorBanner" />, never as a crash.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (SelectedRange is not { } range || _job is null)
        {
            return;
        }

        try
        {
            ExportRequest core = BuildRequest(range);

            // The ink is frozen HERE, on the UI thread, one statement after the camera and before the
            // request leaves for the pool — so the snapshot and the request are the same Start. Riding
            // the request is what makes two Starts incapable of trading documents.
            _job.Start(new Scene2DExportRequest(core, OutputPath, string.Empty,
                range.StartFrame, range.EndFrame, SelectedEncoder, SelectedQuality,
                _captureInk?.Invoke()));

            PersistDefaults();
            StartRequested?.Invoke();
        }
        catch (Exception ex) when (ex is ExportRefusedException or ExportValidationException
                                       or InvalidOperationException)
        {
            ErrorBanner = ex.Message;
            OnPropertyChanged(nameof(CanStart));
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Raised after a successful hand-off, so the view can close.</summary>
    public event Action? StartRequested;

    /// <summary>
    ///     Closing the pane aborts an in-flight ffmpeg download. The export job outlives the dialog by
    ///     design; the download does not — it is the pane's own foreground action, and there would be
    ///     nowhere left to show its licence.
    /// </summary>
    public void Dispose()
    {
        AnswerLicense(false);
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = null;
    }

    /// <summary>Re-probes for ffmpeg after the user installs it, without restarting the app.</summary>
    [RelayCommand]
    private void RecheckFfmpeg()
    {
        // Also forget which encoders the OLD ffmpeg had. Normally the two agree by accident — a newly
        // installed ffmpeg lives in a directory the probe has never asked about, and the cache is keyed
        // by directory — but a user who swapped the binary in place for an NVENC-capable build would
        // otherwise be told, from cache, that their new build cannot do what it plainly can.
        EncoderProbeCache.Shared.Clear();

        RefreshFfmpegStatus();
        UpdateValidation();
    }

    /// <summary>Opens the ffmpeg download page in the default browser.</summary>
    [RelayCommand]
    private static void OpenFfmpegDownloadPage() =>
        Controls.OpenExternal.OpenUri("https://ffmpeg.org/download.html");

    /// <summary>
    ///     Fetches the pinned LGPL build, shows its licence, and installs it only if the user accepts.
    ///     <para>
    ///         <b>Here rather than inside the export.</b> <c>FfmpegAcquisition</c> asks for consent
    ///         <i>after</i> the transfer, so that the licence a user reads is the one inside the bytes
    ///         whose checksum was just verified — which makes it a foreground action or nothing. Wired
    ///         into the runner it would have meant a background job silently pulling 140 MB minutes after
    ///         the pane closed and then raising a modal over whatever the user had moved on to.
    ///     </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadFfmpegNow))]
    private async Task DownloadFfmpeg()
    {
        if (_acquireFfmpeg is not { } acquire)
        {
            return;
        }

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        CancellationToken ct = _downloadCts.Token;

        IsDownloadingFfmpeg = true;
        FfmpegDownloadFraction = 0;
        FfmpegLicenseText = null;
        FfmpegDownloadStatus = "Downloading the pinned LGPL build…";
        NotifyDownloadCommands();

        try
        {
            FfmpegLocation located = await acquire(RequestLicenseConsent,
                new Progress<double>(f => FfmpegDownloadFraction = Math.Clamp(f, 0, 1)), ct)
                .ConfigureAwait(true);

            FfmpegDownloadStatus = located.Found
                ? "ffmpeg installed — video export is available."
                : "Nothing was installed. Install ffmpeg yourself, or export GIF.";
        }
        catch (OperationCanceledException)
        {
            FfmpegDownloadStatus = "Download cancelled — nothing was written.";
        }
        catch (FfmpegAcquisitionException ex)
        {
            // A 404 on the pin, a checksum mismatch, a broken network: the message is user-facing copy.
            FfmpegDownloadStatus = ex.Message;
        }
        finally
        {
            AnswerLicense(false); // a fault mid-consent must never leave the acquisition awaiting forever
            IsDownloadingFfmpeg = false;
            FfmpegLicenseText = null;
            RefreshFfmpegStatus();
            UpdateValidation();
            NotifyDownloadCommands();
        }
    }

    /// <summary>Installs the build whose licence is on screen.</summary>
    [RelayCommand(CanExecute = nameof(HasFfmpegLicense))]
    private void AcceptFfmpegLicense() => AnswerLicense(true);

    /// <summary>Declines the licence. The archive is discarded and nothing is written.</summary>
    [RelayCommand(CanExecute = nameof(HasFfmpegLicense))]
    private void DeclineFfmpegLicense() => AnswerLicense(false);

    /// <summary>Aborts an in-flight transfer. Leaves no partial file.</summary>
    [RelayCommand(CanExecute = nameof(IsDownloadingFfmpeg))]
    private void CancelFfmpegDownload() => _downloadCts?.Cancel();

    /// <summary>True while a licence is waiting to be accepted or declined.</summary>
    public bool HasFfmpegLicense => FfmpegLicenseText is not null;

    /// <summary>True when Download would do something: offered, and not already running.</summary>
    public bool CanDownloadFfmpegNow => _acquireFfmpeg is not null && !IsDownloadingFfmpeg;

    // Called by FfmpegAcquisition from a pool thread, after the hash check and before anything is
    // extracted. Publishing on the UI thread and awaiting a gate is what turns a callback into a prompt.
    private Task<bool> RequestLicenseConsent(FfmpegDownloadOffer offer, string license, CancellationToken ct)
    {
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _licenseAnswer = gate;

        OnUiThread(() =>
        {
            FfmpegDownloadFraction = 1;
            FfmpegDownloadStatus =
                $"Verified {offer.LicenseName} build {offer.ReleaseTag}. Read the licence, then install.";
            FfmpegLicenseText = license;
            NotifyDownloadCommands();
        });

        return gate.Task.WaitAsync(ct);
    }

    private void AnswerLicense(bool accepted)
    {
        TaskCompletionSource<bool>? gate = _licenseAnswer;
        _licenseAnswer = null;
        gate?.TrySetResult(accepted);

        FfmpegLicenseText = null;
        NotifyDownloadCommands();
    }

    private void NotifyDownloadCommands()
    {
        OnPropertyChanged(nameof(HasFfmpegLicense));
        OnPropertyChanged(nameof(CanDownloadFfmpegNow));
        DownloadFfmpegCommand.NotifyCanExecuteChanged();
        AcceptFfmpegLicenseCommand.NotifyCanExecuteChanged();
        DeclineFfmpegLicenseCommand.NotifyCanExecuteChanged();
        CancelFfmpegDownloadCommand.NotifyCanExecuteChanged();
    }

    // CheckAccess is true in a harness with no platform at all, which is what keeps the pure-VM tests
    // dispatcher-free — the same reason ExportJobService.SetStatus is written this way.
    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    /// <summary>The pinned build for this machine wired to <c>FfmpegAcquisition</c>, or null when there is none.</summary>
    /// <param name="managedDirectory">Where the binaries go; null (WASM) means no offer.</param>
    public static FfmpegAcquire? ProductionAcquisition(string? managedDirectory) =>
        managedDirectory is { Length: > 0 } managed && FfmpegAcquisition.Offer(managed) is { } offer
            ? (consent, progress, ct) => FfmpegAcquisition.AcquireAsync(offer, consent, progress, null, ct)
            : null;

    /// <summary>
    ///     The request the current choices describe. Public so a test can assert what the dialog builds
    ///     without a job service, and so the CLI's own argument parsing can be compared against it.
    /// </summary>
    /// <param name="range">The range to export.</param>
    /// <param name="camera">
    ///     The camera, or null to capture the live one now. Validation passes a placeholder: the camera
    ///     cannot make a request invalid, and capturing on every keystroke would defeat D12's "captured
    ///     once, at Start".
    /// </param>
    public ExportRequest BuildRequest(ExportRangeOption range, CameraScript? camera = null)
    {
        ArgumentNullException.ThrowIfNull(range);

        int frames = _outputFrameCount(range.StartFrame, range.EndFrame, SelectedFps, 1.0);
        return new ExportRequest(
            0,
            Math.Max(0, frames - 1),
            SelectedFps,
            ResolvedSize,
            1.0,
            SelectedFormat,
            BuildLayerIds(),
            // D12: the capture happens HERE, at Start, not when the user picked the camera option.
            camera ?? _captureLiveCamera());
    }

    private HashSet<string> BuildLayerIds()
    {
        HashSet<string> ids = new(StringComparer.Ordinal)
        {
            SceneLayerIds.Radar,
            SceneLayerIds.Trails,
            SceneLayerIds.AreaEffects,
            SceneLayerIds.Markers,
            SceneLayerIds.Bomb,
            SceneLayerIds.FloorLabel
        };

        if (IncludeVision)
        {
            // Off by default: the vision solve is §6's biggest per-frame consumer and R3's first lever
            // for holding the ≥ realtime budget at 1080p.
            ids.Add(SceneLayerIds.Vision);
        }

        if (IncludeHud)
        {
            // Three layers, three answers. One "HUD" checkbox meant a user who wanted the clock and not a
            // scoreboard down both edges of a 720p clip could only have both or neither, and D3b made that
            // a real choice by adding a third layer to the same switch.
            Toggle(ids, IncludeHudClock, SceneLayerIds.HudClock);
            Toggle(ids, IncludeHudKillFeed, SceneLayerIds.HudKillFeed);
            Toggle(ids, IncludeHudRoster, SceneLayerIds.HudRoster);
        }

        if (IncludeAnnotations)
        {
            // B2's ink. The constant, not the string it spells: the literal was the same nine characters
            // and STILL not an id CreateSceneStack knew, which is how every export under shipped defaults
            // died on "unknown layer id(s): playback2d.annotations" before it rendered a frame (D3a).
            // Naming it here is only half of it — the tab has to hand the setup a document too, or the
            // layer is asked for with nothing to feed it and is skipped.
            ids.Add(SceneLayerIds.Annotations);
        }

        return ids;
    }

    private static void Toggle(HashSet<string> ids, bool on, string id)
    {
        if (on)
        {
            ids.Add(id);
        }
    }

    private void PersistDefaults() =>
        _persistDefaults?.Invoke(settings =>
        {
            settings.Playback2D.ExportFormatId = SelectedFormat;
            settings.Playback2D.ExportFps = SelectedFps;
            settings.Playback2D.ExportWidth = ResolvedSize.Width;
            settings.Playback2D.ExportHeight = ResolvedSize.Height;
            settings.Playback2D.ExportOutputDirectory = Path.GetDirectoryName(OutputPath) ?? string.Empty;
            settings.Playback2D.ExportIncludeHud = IncludeHud;
            settings.Playback2D.ExportIncludeHudClock = IncludeHudClock;
            settings.Playback2D.ExportIncludeHudKillFeed = IncludeHudKillFeed;
            settings.Playback2D.ExportIncludeHudRoster = IncludeHudRoster;
            settings.Playback2D.ExportIncludeAnnotations = IncludeAnnotations;
            settings.Playback2D.ExportIncludeVision = IncludeVision;
            settings.Playback2D.ExportEncoder = SelectedEncoder;
            settings.Playback2D.ExportQuality = SelectedQuality;
        });

    partial void OnSelectedFormatChanged(string value)
    {
        RebuildFps(SelectedFps);

        // The two video ladders share no rung names, so a saved `av1_nvenc` must not survive a switch to
        // MP4 as a value the selector would then refuse. It degrades to `auto`, which is what the user
        // meant by picking a hardware rung in the first place.
        RebuildEncoders(SelectedEncoder);

        // ffmpeg infers the container from the extension and nothing here overrides it, so a stale one is
        // not a cosmetic mismatch: picking MP4 over a path still ending `.webm` produced a WebM under an
        // MP4's settings. Rewriting the path is the only thing that makes the two agree.
        RetargetOutputExtension();

        UpdateValidation();
    }

    private void RetargetOutputExtension()
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            return;
        }

        string retargeted = Path.ChangeExtension(OutputPath, SelectedFormat);
        if (!string.Equals(retargeted, OutputPath, StringComparison.Ordinal))
        {
            // Assigning re-enters OnOutputPathChanged → UpdateValidation, which is harmless (idempotent)
            // and is why the caller still validates afterwards rather than instead.
            OutputPath = retargeted;
        }
    }

    partial void OnSelectedFpsChanged(int value)
    {
        OnPropertyChanged(nameof(EstimatedFrameCount));
        UpdateValidation();
    }

    partial void OnSelectedRangeChanged(ExportRangeOption? value)
    {
        OnPropertyChanged(nameof(EstimatedFrameCount));
        UpdateValidation();
    }

    partial void OnUseCustomSizeChanged(bool value) => UpdateValidation();

    partial void OnCustomWidthTextChanged(string value) => UpdateValidation();

    partial void OnCustomHeightTextChanged(string value) => UpdateValidation();

    partial void OnOutputPathChanged(string value) => UpdateValidation();

    partial void OnIsFfmpegMissingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFfmpegStrip));
        UpdateValidation();
    }

    private void RebuildFps(int preferred)
    {
        AvailableFps.Clear();
        foreach (int fps in SceneExportSession.SupportedFps(SelectedFormat))
        {
            AvailableFps.Add(fps);
        }

        // Keep the user's rate when the new format supports it; otherwise land on the closest one rather
        // than resetting to the head of the list.
        SelectedFps = AvailableFps.Contains(preferred)
            ? preferred
            : AvailableFps.OrderBy(f => Math.Abs(f - preferred)).First();
    }

    private void RebuildEncoders(string preferred)
    {
        AvailableEncoders.Clear();
        AvailableEncoders.Add(EncoderLadder.Auto);
        AvailableEncoders.Add(EncoderLadder.Software);
        foreach (VideoEncoder rung in EncoderLadder.For(SelectedFormat))
        {
            if (rung.IsHardware)
            {
                // The software rung is already offered as `software`, and listing it twice under two
                // names would make the same choice look like two.
                AvailableEncoders.Add(rung.Name);
            }
        }

        SelectedEncoder = AvailableEncoders.Contains(preferred) ? preferred : EncoderLadder.Auto;
    }

    // A hand-edited settings file, or a rung that belongs to the other format's ladder: either way the
    // safe answer is `auto`, which is the one value that can never fail for an environment reason.
    private static string NormalizeEncoder(string formatId, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return EncoderLadder.Auto;
        }

        string trimmed = requested.Trim().ToLowerInvariant();
        return string.Equals(trimmed, EncoderLadder.Auto, StringComparison.Ordinal) ||
               string.Equals(trimmed, EncoderLadder.Software, StringComparison.Ordinal) ||
               EncoderLadder.Find(formatId, trimmed) is not null
            ? trimmed
            : EncoderLadder.Auto;
    }

    private void RefreshFfmpegStatus() =>
        IsFfmpegMissing = _ffmpegLocator is not null && !_ffmpegLocator().Found;

    private void UpdateValidation()
    {
        OnPropertyChanged(nameof(ResolvedSize));
        ErrorBanner = Validate();

        // Computed even when the dialog is refusing for another reason: the two banners answer different
        // questions and hiding the remark behind the refusal would make it appear only once the refusal
        // was fixed, which is exactly when it stops being new information.
        NoticeBanner = Notice();

        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    // The one non-blocking remark there is. Overwriting is what the user asked for by naming an existing
    // path, and the default path is a constant — so refusing here meant every export after the first.
    private string? Notice() =>
        !string.IsNullOrWhiteSpace(OutputPath) && _fileExists(OutputPath)
            ? $"{Path.GetFileName(OutputPath)} already exists and will be overwritten."
            : null;

    private string? Validate()
    {
        if (_job is not null && _job.Status.IsRunning)
        {
            return "An export is already running.";
        }

        if (_isLiveSyncSessionActive?.Invoke() == true)
        {
            return ExportJobService.LiveSyncRefusal;
        }

        if (SelectedRange is not { } range)
        {
            return "Pick a range to export.";
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            return "Choose where to save the video.";
        }

        bool gif = string.Equals(SelectedFormat, ExportFormats.Gif, StringComparison.Ordinal);
        if (IsFfmpegMissing && !gif)
        {
            return "ffmpeg isn't installed — install it and press Re-check, or switch the format to GIF.";
        }

        try
        {
            // The ONE validator. Everything above is about the dialog's own environment; everything a
            // request can be wrong about is Pipeline's rule set, so the CLI refuses identically.
            SceneExportSession.Validate(BuildRequest(range, DefaultCamera()));
        }
        catch (ExportValidationException ex)
        {
            return ex.Message;
        }

        return null;
    }

    // The camera a dialog with no live host captures: an empty Fixed script, which leaves every pane on
    // the fit its own level was born with. What a design-preview or a pure-VM test gets.
    private static CameraScript.Fixed DefaultCamera() =>
        new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>());

    private static string Normalize(string? formatId) =>
        ExportFormats.All.Contains(formatId, StringComparer.Ordinal) ? formatId! : ExportFormats.WebM;

    private static int SnapEven(string text, int fallback)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            value = fallback;
        }

        value = Math.Clamp(value, 16, 7680);
        return value - (value & 1);
    }

    private static string BuildDefaultPath(Playback2DSettings seed)
    {
        string directory = string.IsNullOrWhiteSpace(seed.ExportOutputDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            : seed.ExportOutputDirectory;

        string extension = Normalize(seed.ExportFormatId);
        return Path.Combine(directory, $"demoviewer-2d.{extension}");
    }

    // No mention of the in-app download here: whether that rung exists on this machine is a question only
    // CanOfferFfmpegDownload can answer, and the button it gates is right below this text. Naming it in
    // prose that also renders on macOS and Linux — where nothing is pinned — is how the pane came to
    // advertise a capability that could not run.
    private static string BuildFfmpegInstructions()
    {
        string install = OperatingSystem.IsWindows()
            ? "Install it with:   winget install Gyan.FFmpeg   (then restart DemoViewer)."
            : OperatingSystem.IsMacOS()
                ? "Install it with:   brew install ffmpeg   (then restart DemoViewer)."
                : "Install it with your package manager (apt install ffmpeg) and make sure it is on PATH.";

        return FfmpegDependency.ManagedDirectory is { } managed
            ? install + $" No PATH edits wanted? Copy ffmpeg and ffprobe into:  {managed}  and press Re-check."
            : install;
    }
}
