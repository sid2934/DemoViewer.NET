#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Playback2D.Core;
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
/// </summary>
public sealed partial class Playback2DExportDialogViewModel : ObservableObject
{
    private readonly Func<CameraScript> _captureLiveCamera;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<FfmpegLocation>? _ffmpegLocator;
    private readonly Func<bool>? _isLiveSyncSessionActive;
    private readonly IExportJobService? _job;
    private readonly Func<int, int, int, double, int> _outputFrameCount;
    private readonly Action<Action<AppSettings>>? _persistDefaults;

    [ObservableProperty]
    private string _customHeightText = "1080";

    [ObservableProperty]
    private string _customWidthText = "1920";

    [ObservableProperty]
    private string? _errorBanner;

    [ObservableProperty]
    private bool _includeAnnotations = true;

    [ObservableProperty]
    private bool _includeHud = true;

    [ObservableProperty]
    private bool _includeVision;

    [ObservableProperty]
    private bool _isFfmpegMissing;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private string _selectedFormat = ExportFormats.WebM;

    [ObservableProperty]
    private int _selectedFps = 60;

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
    public Playback2DExportDialogViewModel(
        IReadOnlyList<ExportRangeOption> ranges,
        Playback2DSettings? defaults = null,
        IExportJobService? job = null,
        Func<CameraScript>? captureLiveCamera = null,
        Func<int, int, int, double, int>? outputFrameCount = null,
        Func<FfmpegLocation>? ffmpegLocator = null,
        Func<bool>? isLiveSyncSessionActive = null,
        Action<Action<AppSettings>>? persistDefaults = null,
        Func<string, bool>? fileExists = null)
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

        _selectedRange = Ranges.Count > 0 ? Ranges[0] : null;

        Playback2DSettings seed = defaults ?? new Playback2DSettings();
        _selectedFormat = Normalize(seed.ExportFormatId);
        _includeHud = seed.ExportIncludeHud;
        _includeAnnotations = seed.ExportIncludeAnnotations;
        _selectedSize = SizePresets.FirstOrDefault(s => s.Width == seed.ExportWidth && s.Height == seed.ExportHeight)
                        ?? SizePresets[0];
        _customWidthText = seed.ExportWidth.ToString(CultureInfo.InvariantCulture);
        _customHeightText = seed.ExportHeight.ToString(CultureInfo.InvariantCulture);
        _outputPath = BuildDefaultPath(seed);

        RebuildFps(seed.ExportFps);
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

    /// <summary>True when a pinned LGPL build exists for this machine, so the Download button is offered.</summary>
    public static bool CanOfferFfmpegDownload =>
        FfmpegDependency.ManagedDirectory is { } managed && FfmpegAcquisition.Offer(managed) is not null;

    /// <summary>Whether the user consented to the in-app download. Read by the job's request.</summary>
    [ObservableProperty]
    private bool _allowFfmpegDownload = true;

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
            _job.Start(new Scene2DExportRequest(core, OutputPath, string.Empty,
                AllowFfmpegDownload && CanOfferFfmpegDownload, range.StartFrame, range.EndFrame));

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

    /// <summary>Re-probes for ffmpeg after the user installs it, without restarting the app.</summary>
    [RelayCommand]
    private void RecheckFfmpeg()
    {
        RefreshFfmpegStatus();
        UpdateValidation();
    }

    /// <summary>Opens the ffmpeg download page in the default browser.</summary>
    [RelayCommand]
    private static void OpenFfmpegDownloadPage() =>
        Controls.OpenExternal.OpenUri("https://ffmpeg.org/download.html");

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
            ids.Add(SceneLayerIds.HudClock);
            ids.Add(SceneLayerIds.HudKillFeed);
        }

        if (IncludeAnnotations)
        {
            // B2's layer id. Harmless when B2's layer is not registered — the stack simply has nothing
            // answering to it, and CreateSceneStack ignores ids it does not know how to build.
            ids.Add("playback2d.annotations");
        }

        return ids;
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
            settings.Playback2D.ExportIncludeAnnotations = IncludeAnnotations;
        });

    partial void OnSelectedFormatChanged(string value)
    {
        RebuildFps(SelectedFps);
        UpdateValidation();
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

    private void RefreshFfmpegStatus() =>
        IsFfmpegMissing = _ffmpegLocator is not null && !_ffmpegLocator().Found;

    private void UpdateValidation()
    {
        OnPropertyChanged(nameof(ResolvedSize));
        ErrorBanner = Validate();
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

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

        return _fileExists(OutputPath)
            ? $"{Path.GetFileName(OutputPath)} already exists and will be overwritten."
            : null;
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

    private static string BuildFfmpegInstructions()
    {
        string install = OperatingSystem.IsWindows()
            ? "Install it with:   winget install Gyan.FFmpeg   (then restart DemoViewer), or let DemoViewer " +
              "download the pinned LGPL build."
            : OperatingSystem.IsMacOS()
                ? "Install it with:   brew install ffmpeg   (then restart DemoViewer)."
                : "Install it with your package manager (apt install ffmpeg) and make sure it is on PATH.";

        return FfmpegDependency.ManagedDirectory is { } managed
            ? install + $" No PATH edits wanted? Copy ffmpeg and ffprobe into:  {managed}  and press Re-check."
            : install;
    }
}
