#region

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Clips;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.LiveSync;

#endregion

namespace DemoViewer.NET.ViewModels.Highlights;

/// <summary>
///     The reel <b>configuration pane</b> (promoted out of the modal by
///     the Reels-dashboard redesign). A configuration + pre-flight surface: it turns the tab's ordered
///     <see cref="HighlightSelection" /> tray into a coalesced clip plan (via <see cref="ClipWindows" />),
///     lets the user set paddings, the No-HUD preset, output + encoding, validates inline,
///     and on <c>Generate</c> persists the edited reel defaults and hands the plan to
///     <see cref="IReelJobService.Start" />: the multi-minute job runs in the background behind the Reel
///     status chip, never a locked modal.
///     <para>
///         <b>Why it is still called …DialogViewModel.</b> It no longer hosts a dialog: the Reels tab embeds it
///         as the right-hand pane and owns the tray + footer. The type name is retained deliberately: the
///         ViewLocator name mapping (<c>…ViewModel</c> → <c>…View</c>) and a test that guards it both key off
///         it, and renaming is churn the promotion does not need. The rename to <c>ReelConfigViewModel</c> is
///         recorded as owed debt in <c>docs/ui/design-system.md</c>.
///     </para>
///     <para>
///         <b>This VM lives for the whole app run now</b>, where the modal was built fresh per invocation. Every
///         one-shot latch inside it therefore has to reset explicitly (see <see cref="StartAndClose" /> and
///         <c>_interlockConfirmed</c>). A latch that was harmless in a per-invocation object becomes a
///         permanently-disarmed guard rail in a long-lived one.
///     </para>
///     <para>
///         <b>Contract-faithful reductions.</b> The App-facing <see cref="ReelRequest" /> carries a single
///         <see cref="ReelRequest.NoHudPreset" /> boolean, so the granular <c>Cs2ClipOptions</c> checkboxes
///         collapse to a Default / No-HUD preset radio (the only display flag the contract plumbs). The
///         padding UI is one global lead-in/out pair (per-type overrides are a Settings concern).
///     </para>
///     <para>
///         <b>Injected seams.</b> Platform mode is the injected
///         <see cref="IsDryRunOnly" /> flag (macOS = dry-run), never an inline <c>OperatingSystem</c>
///         call, so the primary-action tests and captures can drive both branches; demo existence is the
///         injected <c>fileExists</c> predicate so the pure-VM tests stay filesystem-free.
///     </para>
/// </summary>
public sealed partial class HighlightReelDialogViewModel : ViewModelBase
{
    private static readonly string[] _defaultContainerOptions = ["mp4", "mkv", "mov", "webm"];
    private static readonly int[] _defaultFpsOptions = [30, 60, 120];

    // Standard capture resolutions offered on the Reels tab, the common 16:9 and 4:3 sizes, plus the
    // Custom sentinel (last) that unlocks the width/height fields. The list is the ComboBox's ItemsSource;
    // its instances are the ones seed-matching and SelectedItem compare against (record value-equality).
    private static readonly ReelResolutionOption[] _resolutionOptions =
    [
        new("2160p · 3840×2160 (16:9)", 3840, 2160),
        new("1440p · 2560×1440 (16:9)", 2560, 1440),
        new("1080p · 1920×1080 (16:9)", 1920, 1080),
        new("720p · 1280×720 (16:9)", 1280, 720),
        new("1080p · 1440×1080 (4:3)", 1440, 1080),
        new("960p · 1280×960 (4:3)", 1280, 960),
        new("768p · 1024×768 (4:3)", 1024, 768),
        new("600p · 800×600 (4:3)", 800, 600),
        ReelResolutionOption.Custom
    ];

    // ── ffmpeg pre-flight (v0.6.0) ────────────────────────────────────────────
    // CSVG's capture/concat path needs ffmpeg (resolved from PATH or the app-managed folder);
    // without this check a missing install surfaced as a raw exception AFTER CS2 had launched.
    // Null locator (pure-VM tests) = assume present, keeping those tests filesystem-free; the
    // composition root passes FfmpegDependency.Locate.
    private readonly Func<FfmpegStatus>? _ffmpegLocator;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<bool>? _isLiveSyncSessionActive;
    private readonly Action<Action<AppSettings>>? _persistDefaults;
    private readonly IReelJobService? _reelJob;

    [ObservableProperty]
    private string _baseFileName;

    [ObservableProperty]
    private int _bitrateKbps;

    [ObservableProperty]
    private bool _captureAudio;

    [ObservableProperty]
    private bool _concatenate;

    [ObservableProperty]
    private string _containerFormat;

    [ObservableProperty]
    private int _crf;

    /// <summary>Custom height (px); see <see cref="CustomWidth" />.</summary>
    [ObservableProperty]
    private int _customHeight;

    /// <summary>Custom width (px), editable only when <see cref="SelectedResolution" /> is the Custom sentinel.</summary>
    [ObservableProperty]
    private int _customWidth;

    /// <summary>Clamp the lead-in to the round-start tick. OFF by default (allow pre-round context).</summary>
    [ObservableProperty]
    private bool _dontCrossRoundStart;

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>The inline pre-flight banner (null when the plan validates).</summary>
    [ObservableProperty]
    private string? _errorBanner;

    [ObservableProperty]
    private int _fps;

    private bool _interlockConfirmed;

    /// <summary>
    ///     Whether the ENCODING section is offered. The <c>highlights.encoding</c> SubFeature
    ///     (<c>Defaults(false, true, true)</c>) binds here: CRF/bitrate are OBS-encoder knobs a
    ///     consumer cannot reason about. A settable property, not an inline category check: the gate is the
    ///     feature system's job and this is only where it lands. The tab VM applies the gate on construction
    ///     AND re-applies it on <c>IFeatureGate.Changed</c>; it defaults visible so a host with no gate
    ///     (tests, UiCapture) never silently loses a section.
    /// </summary>
    [ObservableProperty]
    private bool _isEncodingVisible = true;

    // ── ffmpeg pre-flight (v0.6.0) ────────────────────────────────────────────

    /// <summary>
    ///     True when the pre-flight found NO ffmpeg (neither on PATH nor the app-managed folder).
    ///     Only ever true on a real-capture host with a real reel service. Dry-run walks the plan
    ///     without rendering, and the browser head has no reel path at all.
    /// </summary>
    [ObservableProperty]
    private bool _isFfmpegMissing;

    // ── Padding ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    private double _leadInSeconds;

    [ObservableProperty]
    private double _leadOutSeconds;

    private int _mergedCount;
    private int _movedCount;

    // ── Display preset ────────────────────────────────────────────────────────

    /// <summary>The No-HUD preset. false = Default; true = No-HUD. Maps to <see cref="ReelRequest.NoHudPreset" />.</summary>
    [ObservableProperty]
    private bool _noHudPreset;

    // ── Output ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _outputFolder;

    // The plan built by the last Recompute: the exact ReelClips handed to IReelJobService.Start. Excludes
    // any clip whose demo has moved (those also BLOCK Generate via the pre-flight banner, so this never ships a
    // silently-partial reel).
    private List<ReelClip> _plan = [];

    // The last auto-suggested base name. BaseFileName re-seeds from the tray only while it still equals this.
    // Once the user types their own name, a later staging must not silently overwrite it.
    private string _seededBaseName;
    private int _selectedCount;

    // ── Resolution: capture geometry ─────────────────────────────────────────

    /// <summary>The chosen capture resolution (a preset or the Custom sentinel). Drives the request Width/Height.</summary>
    [ObservableProperty]
    private ReelResolutionOption? _selectedResolution;

    // The tray contents, IN TRAY ORDER. Not readonly any more: the pane outlives any one tray state, and
    // SetSelections swaps it whenever the user stages, un-stages, or reorders.
    private IReadOnlyList<HighlightSelection> _selections;

    // ── Single-CS2 interlock ──────────────────────────────────────────────────

    /// <summary>The interlock confirm strip is showing (Generate pressed while a live-sync session owns CS2).</summary>
    [ObservableProperty]
    private bool _showInterlockConfirm;

    private IStorageProvider? _storageProvider;

    // Clips whose player could not be resolved to a spectate name (the demo's cached roster carries no slot
    // attribution, see HighlightScanService's roster repair). CSVG spectates BY NAME, so an empty name makes
    // a clip unrenderable; these block Generate with an actionable banner instead of a raw CSVG validation dump.
    private int _unresolvedCount;

    // ── Encoding: CRF ⊕ Bitrate, UI-enforced ─────────────────────────────────

    /// <summary>CRF mode selected (radio). When true the bitrate field is disabled; when false CRF is disabled.</summary>
    [ObservableProperty]
    private bool _useCrf;

    /// <summary>
    ///     Builds the dialog VM over the tab's current reel selection and the seeded reel defaults.
    /// </summary>
    /// <param name="selections">The highlights checked on the Highlights tab (each bundles its cache row).</param>
    /// <param name="defaults">The seeded reel defaults: output/encoding/padding preferences.</param>
    /// <param name="reelJob">The background job service the plan hands off to on Generate (null in unit tests without a fake).</param>
    /// <param name="persistDefaults">
    ///     Persists the edited defaults on Generate ("set once"); the App passes
    ///     <c>SettingsService.Write</c>.
    /// </param>
    /// <param name="isLiveSyncSessionActive">The interlock probe: true when a live-sync session owns CS2.</param>
    /// <param name="dryRunOnly">Platform mode: macOS = dry-run-only; Windows/Linux = real generation.</param>
    /// <param name="fileExists">Demo-existence predicate for the pre-flight (defaults to <c>File.Exists</c>).</param>
    /// <param name="ffmpegLocator">
    ///     ffmpeg presence probe for the pre-flight (the App passes <c>FfmpegDependency.Locate</c>).
    ///     Null (pure-VM tests) = assume present, so the reel-plan tests stay machine-independent.
    /// </param>
    public HighlightReelDialogViewModel(
        IReadOnlyList<HighlightSelection> selections,
        HighlightsSettings defaults,
        IReelJobService? reelJob = null,
        Action<Action<AppSettings>>? persistDefaults = null,
        Func<bool>? isLiveSyncSessionActive = null,
        bool dryRunOnly = false,
        Func<string, bool>? fileExists = null,
        Func<FfmpegStatus>? ffmpegLocator = null)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(defaults);

        _selections = selections;
        _reelJob = reelJob;
        _persistDefaults = persistDefaults;
        _isLiveSyncSessionActive = isLiveSyncSessionActive;
        _fileExists = fileExists ?? File.Exists;
        _ffmpegLocator = ffmpegLocator;
        IsDryRunOnly = dryRunOnly;

        // Never unsubscribed: the job service and this pane are both app-lifetime singletons now (the pane is
        // the retained tab's child), so there is no teardown to leak across.
        if (_reelJob is not null)
        {
            _reelJob.StatusChanged += (_, _) => GenerateCommand.NotifyCanExecuteChanged();
        }

        // ── Seed from the reel defaults ──
        _leadInSeconds = defaults.ClipLeadInSeconds;
        _leadOutSeconds = defaults.ClipLeadOutSeconds;
        _outputFolder = defaults.ReelOutputDirectory ?? "";
        _containerFormat = defaults.ReelContainerFormat;
        _fps = defaults.ReelFps;
        _concatenate = defaults.ReelConcatenate;
        _captureAudio = defaults.ReelCaptureAudio;
        _useCrf = defaults.ReelBitrateKbps is null;
        _crf = defaults.ReelCrf;
        _bitrateKbps = defaults.ReelBitrateKbps ?? 8000;

        // Resolution seed: match the persisted size to a preset; if none matches it's a custom size, so
        // select the Custom sentinel and prime the width/height fields with the persisted values.
        _customWidth = defaults.ReelWidth > 0 ? defaults.ReelWidth : 1920;
        _customHeight = defaults.ReelHeight > 0 ? defaults.ReelHeight : 1080;
        _selectedResolution = Array.Find(_resolutionOptions,
                                  o => !o.IsCustom && o.Width == defaults.ReelWidth && o.Height == defaults.ReelHeight)
                              ?? ReelResolutionOption.Custom;
        _baseFileName = SuggestBaseName(selections);
        _seededBaseName = _baseFileName;

        // Ensure the container/fps seed is always a member of the option lists (so the ComboBox binds).
        if (!_defaultContainerOptions.Contains(_containerFormat))
        {
            _containerFormat = _defaultContainerOptions[0];
        }

        if (!_defaultFpsOptions.Contains(_fps))
        {
            _fps = _defaultFpsOptions[1];
        }

        Recompute();
    }

    // ── Clip list + coalescing display ────────────────────────────────────────

    /// <summary>
    ///     The clip plan grouped by (player, demo) with the visible coalescing. <b>This IS the tray</b>
    ///     the Reels dashboard renders, deliberately not a parallel model. The redesign's headline argument
    ///     for promoting the modal is that coalescing feedback becomes visible while you build; rendering the
    ///     tray from anything other than the plan builder would let the two disagree, which is the exact
    ///     failure the promotion exists to remove.
    /// </summary>
    public ObservableCollection<ReelClipGroupViewModel> ClipGroups { get; } = [];

    /// <summary>The tray-mutation seam (▲▼✕). Null in the pure-VM tests, which leaves the buttons inert.</summary>
    public IClipTrayHost? Tray { get; set; }

    /// <summary>
    ///     Header, e.g. "CLIPS (7 staged · 5 after merge)": the live coalescing feedback. Collapses to a bare
    ///     "CLIPS" when nothing is staged: "0 staged · 0 after merge" is noise reporting on an empty page, and
    ///     the tray's own empty state already says it in words.
    /// </summary>
    public string ClipsHeader => _selectedCount == 0
        ? "CLIPS"
        : $"CLIPS ({_selectedCount} staged · {_mergedCount} after merge)";

    /// <summary>True once anything is staged: gates the tray body against its empty state.</summary>
    public bool HasClips => _selectedCount > 0;

    /// <summary>Footer line, e.g. "Total ~24s across 2 clips".</summary>
    public string TotalDurationText
    {
        get
        {
            double total = ClipGroups.SelectMany(g => g.Rows).Sum(r => r.DurationSeconds);
            return $"Total ~{Fmt(total)}s across {_mergedCount} clip{(_mergedCount == 1 ? "" : "s")}";
        }
    }

    public IReadOnlyList<string> ContainerOptions { get; } = _defaultContainerOptions;
    public IReadOnlyList<int> FpsOptions { get; } = _defaultFpsOptions;

    /// <summary>The resolution presets (16:9 + 4:3) plus the Custom sentinel: the resolution ComboBox source.</summary>
    public IReadOnlyList<ReelResolutionOption> ResolutionOptions { get; } = _resolutionOptions;

    /// <summary>True when the Custom resolution is selected: enables the width/height fields.</summary>
    public bool IsCustomResolution => SelectedResolution?.IsCustom ?? false;

    /// <summary>The concrete (width, height) the request will carry: the custom fields, or the preset's size.</summary>
    private (int Width, int Height) EffectiveResolution => IsCustomResolution
        ? (CustomWidth, CustomHeight)
        : (SelectedResolution?.Width ?? 1920, SelectedResolution?.Height ?? 1080);

    // Encoders that emit yuv420p (the H.264/HEVC default) require EVEN width and height, so a custom size is
    // only valid when both are even and inside a sane pixel range. Presets are even by construction; this
    // only ever fails on a hand-typed custom value, which is exactly what the banner needs to catch.
    private bool IsResolutionValid
    {
        get
        {
            (int w, int h) = EffectiveResolution;
            return w is >= 16 and <= 7680 && h is >= 16 and <= 4320 && w % 2 == 0 && h % 2 == 0;
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorBanner);

    /// <summary>
    ///     Whether the pre-flight banner is SHOWN. Distinct from <see cref="HasError" />, which still gates
    ///     Generate: with an empty tray the calm empty state and a red "nothing to render" banner appeared on
    ///     the same screen, so the page shouted an error at the user about its own default state. The banner
    ///     is for problems with what you built, not for not having built anything yet.
    /// </summary>
    public bool ShowErrorBanner => HasError && HasClips;

    /// <summary>The interlock confirm-strip copy.</summary>
    public string InterlockMessage { get; } =
        "Generating a reel restarts CS2 with recording and pauses Live Sync (up to ~2 min). Live Sync " +
        "stays paused until the reel finishes, then you can reconnect.";

    // ── Platform primary action ───────────────────────────────────────────────

    /// <summary>macOS: only a mock dry run is possible (real capture needs Windows: the CS2 present hook is DXGI).</summary>
    public bool IsDryRunOnly { get; }

    /// <summary>Primary button label: real "Generate reel", or the developer-labelled "Dry run (mock)".</summary>
    public string PrimaryActionLabel => IsDryRunOnly ? "Dry run (mock)" : "Generate reel";

    /// <summary>The developer/testing caption under the primary button (dry-run only).</summary>
    public string DryRunCaption { get; } =
        "Developer/testing — walks the clip plan without recording. Real reels need Windows and ffmpeg.";

    /// <summary>The ffmpeg strip is shown: missing, on a host where a real reel could otherwise run.</summary>
    public bool ShowFfmpegStrip => IsFfmpegMissing;

    /// <summary>The strip's explanation copy.</summary>
    public string FfmpegMissingMessage { get; } =
        "Reel rendering needs ffmpeg, which wasn't found on this machine.";

    /// <summary>
    ///     The self-install instructions. The app-managed folder option exists so a user who cannot
    ///     (or would rather not) edit PATH can just drop the two binaries in a folder DemoViewer
    ///     already knows to look in. <c>CsvgWebHost</c> points CSVG there automatically.
    /// </summary>
    public string FfmpegInstructions { get; } = BuildFfmpegInstructions();

    // ── Commands ──────────────────────────────────────────────────────────────

    // A reel is already rendering. The modal could ignore this: the shell simply refused to OPEN a second
    // one, but an embedded pane has no open/close gate, so the primary button would happily fire into an
    // IReelJobService that throws. Machine-exclusive resource, one job at a time.
    private bool JobRunning => _reelJob?.Status.IsRunning ?? false;

    private bool CanGenerate => _movedCount == 0 && _unresolvedCount == 0 && _plan.Count > 0
                                && !string.IsNullOrWhiteSpace(OutputFolder)
                                && IsResolutionValid && !JobRunning && !IsFfmpegMissing;

    /// <summary>The CRF numeric field is editable only in CRF mode (CRF ⊕ bitrate).</summary>
    public bool CrfEnabled => UseCrf;

    /// <summary>The bitrate numeric field is editable only in bitrate mode (CRF ⊕ bitrate).</summary>
    public bool BitrateEnabled => !UseCrf;

    /// <summary>
    ///     Replaces the tray contents and rebuilds the plan. Called by the tab on every stage / un-stage /
    ///     reorder: the ORDER of <paramref name="selections" /> is load-bearing (see <see cref="Recompute" />).
    /// </summary>
    /// <param name="selections">The staged highlights, in tray order.</param>
    public void SetSelections(IReadOnlyList<HighlightSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        _selections = selections;

        // Re-seed the output name from the new head of the tray, but only while the field is still the name
        // the pane suggested. The modal was constructed per-invocation so its one-shot seed was always right; a
        // long-lived pane seeded from an EMPTY tray would otherwise read "reel" forever.
        if (string.Equals(BaseFileName, _seededBaseName, StringComparison.Ordinal))
        {
            _seededBaseName = SuggestBaseName(selections);
            BaseFileName = _seededBaseName;
        }

        Recompute();
    }

    private static string BuildFfmpegInstructions()
    {
        string winget = OperatingSystem.IsWindows()
            ? "Install it with:   winget install Gyan.FFmpeg   (then restart DemoViewer), or download " +
              "a build and add its bin folder to PATH."
            : "Install it with your package manager and make sure it is on PATH.";
        return FfmpegDependency.ManagedDirectory is { } managed
            ? winget + $" No PATH edits wanted? Copy ffmpeg.exe and ffprobe.exe into:  {managed}  and press Re-check."
            : winget;
    }

    partial void OnIsFfmpegMissingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFfmpegStrip));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    // Re-probes the machine. Cheap (a PATH stat scan), so it runs on every Recompute: a user who
    // installs ffmpeg mid-session sees the strip clear on the next tray change or Re-check.
    private void RefreshFfmpegStatus()
    {
        IsFfmpegMissing = _ffmpegLocator is not null
                          && !IsDryRunOnly
                          && _reelJob is not null
                          && !_ffmpegLocator().Found;
    }

    /// <summary>Opens the ffmpeg download page in the default browser.</summary>
    [RelayCommand]
    private static void OpenFfmpegDownloadPage() =>
        OpenExternal.OpenUri("https://ffmpeg.org/download.html");

    /// <summary>
    ///     Re-runs the ffmpeg probe after the user installs it, so Generate unblocks in place
    ///     without restarting the app (a fresh PATH edit still needs a restart to reach this
    ///     process, the instructions say so, but the drop-in folder is picked up immediately).
    /// </summary>
    [RelayCommand]
    private void RecheckFfmpeg()
    {
        RefreshFfmpegStatus();
        UpdateValidation();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised when the dialog should close (Cancel, or a successful Generate hand-off).</summary>
    public event EventHandler? Closed;

    partial void OnErrorBannerChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowErrorBanner));
    }

    /// <summary>
    ///     Primary action. If a live-sync session owns CS2 and it has not yet been confirmed,
    ///     surfaces the interlock confirm strip instead of starting. Otherwise persists the edited defaults,
    ///     hands the plan to the background job, and closes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Generate()
    {
        if (!_interlockConfirmed && (_isLiveSyncSessionActive?.Invoke() ?? false))
        {
            ShowInterlockConfirm = true;
            return;
        }

        StartAndClose();
    }

    /// <summary>Interlock confirm-strip "Continue": the informed consent given, start the job.</summary>
    [RelayCommand]
    private void ConfirmInterlock()
    {
        _interlockConfirmed = true;
        ShowInterlockConfirm = false;
        StartAndClose();
    }

    /// <summary>Interlock confirm-strip "Back": dismiss the strip without starting.</summary>
    [RelayCommand]
    private void CancelInterlock() => ShowInterlockConfirm = false;

    /// <summary>Cancels the dialog (no job started).</summary>
    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>Picks the output folder via the OS folder picker (desktop). No-op when no picker is wired.</summary>
    [RelayCommand]
    private async Task BrowseOutput()
    {
        if (_storageProvider is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> picked = await _storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select the reel output folder",
                AllowMultiple = false
            });
        string? path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
        {
            OutputFolder = path;
        }
    }

    /// <summary>Wired from the dialog view's code-behind (the storage provider needs the visual tree).</summary>
    public void SetStorageProvider(IStorageProvider? provider) => _storageProvider = provider;

    // ── Recompute hooks ───────────────────────────────────────────────────────

    partial void OnLeadInSecondsChanged(double value) => Recompute();
    partial void OnLeadOutSecondsChanged(double value) => Recompute();
    partial void OnDontCrossRoundStartChanged(bool value) => Recompute();
    partial void OnOutputFolderChanged(string value) => GenerateCommand.NotifyCanExecuteChanged();

    partial void OnUseCrfChanged(bool value)
    {
        // CRF ⊕ Bitrate: selecting one disables the other's field: a purely reactive projection here.
        OnPropertyChanged(nameof(CrfEnabled));
        OnPropertyChanged(nameof(BitrateEnabled));
    }

    partial void OnSelectedResolutionChanged(ReelResolutionOption? value)
    {
        OnPropertyChanged(nameof(IsCustomResolution));
        RevalidateResolution();
    }

    partial void OnCustomWidthChanged(int value) => RevalidateResolution();
    partial void OnCustomHeightChanged(int value) => RevalidateResolution();

    // A custom size (or a switch to/from Custom) can flip the pre-flight banner and the Generate gate; both
    // presets and custom re-run validation so an invalid custom size never silently ships a 0×0 request.
    private void RevalidateResolution()
    {
        UpdateValidation();
        OnPropertyChanged(nameof(ShowErrorBanner));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    // ── Plan building ─────────────────────────────────────────────────────────

    private void Recompute()
    {
        _selectedCount = _selections.Count;

        // Per-demo facts (all candidates of a coalesced group share one demo → one row).
        Dictionary<string, DemoFacts> demoFacts = new(StringComparer.OrdinalIgnoreCase);
        List<CandidateSource> sources = new(_selections.Count);

        // Tray order → output order. Groups are emitted in the order the user FIRST staged something for
        // them, and every clip of a group stays contiguous. That is not cosmetic: ReelJobService only issues
        // a LoadDemoAsync when clip.DemoPath changes from the previous clip, so interleaving demos multiplies
        // the single most expensive step of a render. Ordering here: never inside ClipWindows.Coalesce,
        // which groups by (demo, player, round) and MUST stay order-independent, or merge behaviour becomes
        // position-dependent and two identical trays render differently.
        Dictionary<string, int> groupOrder = new(StringComparer.Ordinal);

        int? RoundStart(DemoCacheRecord record, int tick)
        {
            return DontCrossRoundStart ? ClipWindows.RoundStartFor(record.Rounds.ToClipRounds(), tick) : null;
        }

        foreach (HighlightSelection sel in _selections)
        {
            DemoCacheRecord record = sel.Record;
            CachedHighlightEvent h = sel.Highlight;
            int rate = record.TickRate > 0 ? record.TickRate : 64;
            if (!demoFacts.ContainsKey(record.Path))
            {
                demoFacts[record.Path] = new DemoFacts(
                    record.Sha256, rate, DemoEntry.PrettifyMap(record.Map), record.Map,
                    Path.GetFileName(record.Path), _fileExists(record.Path));
            }

            string groupKey = ClipTrayKeys.Group(record.Path, sel.SteamId64);
            if (!groupOrder.ContainsKey(groupKey))
            {
                groupOrder[groupKey] = groupOrder.Count;
            }

            // FRAME CLOCK end to end: the plan is DV-side, and the TickOffset shim is applied
            // exactly once by the reel job on its way into CS2 demo-tick space (ReelJobService).
            (long start, long end) = ClipWindows.Compute(
                h.Tick, RoundStart(record, h.Tick), rate, LeadInSeconds, LeadOutSeconds,
                record.TickCount, 0, h.ClipStartTick);
            sources.Add(new CandidateSource(
                new ClipWindows.Candidate(
                    record.Path, sel.SteamId64, sel.RawPlayerName, h.RoundNumber, start, end, h.RenderedTitle),
                sel.Key, h.Tick));
        }

        List<ClipWindows.Clip> clips = ClipWindows.Coalesce(sources.Select(s => s.Candidate));
        _mergedCount = clips.Count;

        // Candidates grouped by the SAME key Coalesce merges on, so the display maps each emitted clip back
        // to its contributors by key-scoped overlap (never raw tick overlap, which would cross T/CT firings).
        // The path half is upper-cased HERE TOO: Coalesce uppercases its key, so a casing-variant path merged
        // as one clip but split into two display buckets, and the clip rendered with no contributors at all.
        Dictionary<(string DemoPath, string SteamId64, int RoundNumber), List<CandidateSource>> byGroup = sources
            .GroupBy(s => (DemoPath: s.Candidate.DemoPath.ToUpperInvariant(), s.Candidate.SteamId64,
                s.Candidate.RoundNumber))
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Candidate.StartTick).ToList());

        List<ReelClip> plan = new(clips.Count);
        _movedCount = 0;
        _unresolvedCount = 0;

        ClipGroups.Clear();
        int position = 0;
        List<IGrouping<(string DemoPath, string SteamId64), ClipWindows.Clip>> ordered =
        [
            .. clips.GroupBy(c => (c.DemoPath, c.SteamId64))
                .OrderBy(g => groupOrder.GetValueOrDefault(ClipTrayKeys.Group(g.Key.DemoPath, g.Key.SteamId64),
                    int.MaxValue))
        ];

        foreach (IGrouping<(string DemoPath, string SteamId64), ClipWindows.Clip> demoPlayer in ordered)
        {
            DemoFacts facts = demoFacts[demoPlayer.Key.DemoPath];
            ClipWindows.Clip firstClip = demoPlayer.First();
            string player = DisplayText.Sanitize(firstClip.PlayerName);
            ReelClipGroupViewModel group = new(
                string.IsNullOrEmpty(player) ? facts.MapDisplay : $"{player} · {facts.MapDisplay}",
                ClipTrayKeys.Group(demoPlayer.Key.DemoPath, demoPlayer.Key.SteamId64),
                facts.MapName, facts.MapDisplay, facts.FileName,
                position, ordered.Count, !facts.Exists, () => Tray);

            foreach (ClipWindows.Clip clip in demoPlayer.OrderBy(c => c.StartTick))
            {
                List<CandidateSource> contributingSources = byGroup
                    .GetValueOrDefault(
                        (clip.DemoPath.ToUpperInvariant(), clip.SteamId64, clip.RoundNumber), [])
                    .Where(s => s.Candidate.StartTick <= clip.EndTick && s.Candidate.EndTick >= clip.StartTick)
                    .ToList();

                List<ReelClipContributorViewModel> contributors = contributingSources
                    .Select(s => new ReelClipContributorViewModel(
                        DisplayText.Sanitize(s.Candidate.Title),
                        WindowText(s.Candidate.StartTick, s.Candidate.EndTick),
                        DurationText(s.Candidate.StartTick, s.Candidate.EndTick, facts.TickRate),
                        $"r{s.Candidate.RoundNumber.ToString(CultureInfo.InvariantCulture)}",
                        $"tick {s.SourceTick.ToString("N0", CultureInfo.InvariantCulture)}",
                        s.Key, () => Tray))
                    .ToList();

                double mergedSeconds = DurationSeconds(clip.StartTick, clip.EndTick, facts.TickRate);
                bool moved = !facts.Exists;
                // No spectate name → CSVG can't render the clip. Treated like a moved demo: kept out of the
                // plan, counted, and shown as a per-row error so the block is visible where the clip lives.
                bool unresolved = !moved && string.IsNullOrWhiteSpace(clip.PlayerName);
                if (moved)
                {
                    _movedCount++;
                }
                else if (unresolved)
                {
                    _unresolvedCount++;
                }
                else
                {
                    plan.Add(new ReelClip(
                        clip.DemoPath, facts.Sha256, ParseSteamId(clip.SteamId64), clip.PlayerName,
                        clip.StartTick, clip.EndTick, facts.TickRate, string.Join(" + ", clip.Titles)));
                }

                group.Rows.Add(new ReelClipRowViewModel(
                    contributors, WindowText(clip.StartTick, clip.EndTick),
                    DurationText(clip.StartTick, clip.EndTick, facts.TickRate), mergedSeconds, moved || unresolved));
            }

            ClipGroups.Add(group);
            position++;
        }

        _plan = plan;
        RefreshFfmpegStatus();
        UpdateValidation();

        OnPropertyChanged(nameof(ClipsHeader));
        OnPropertyChanged(nameof(HasClips));
        OnPropertyChanged(nameof(ShowErrorBanner));
        OnPropertyChanged(nameof(TotalDurationText));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private void UpdateValidation()
    {
        if (_selectedCount == 0 || _mergedCount == 0)
        {
            ErrorBanner = "No clips staged — nothing to render.";
        }
        else if (_movedCount > 0)
        {
            // "remove it", not "deselect it": the tray's ✕ is the affordance now, and the checkbox the old
            // copy pointed at no longer exists anywhere on the page.
            ErrorBanner = $"{_movedCount} clip{(_movedCount == 1 ? " has" : "s have")} a problem " +
                          $"(demo moved). Fix or remove {(_movedCount == 1 ? "it" : "them")} to continue.";
        }
        else if (_unresolvedCount > 0)
        {
            // Actionable, not CSVG's raw "PlayerNameToSpectate must not be empty" dump: the demo's cached
            // roster has no slot→name mapping (a legacy names-only cache record), so re-scanning re-parses it
            // and repairs the roster (HighlightScanService), after which the player resolves and this clears.
            ErrorBanner = $"{_unresolvedCount} clip{(_unresolvedCount == 1 ? "" : "s")} can't resolve the " +
                          "player to spectate. Re-scan the demo (Rescan all) to refresh its roster, then retry.";
        }
        else if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            ErrorBanner = "Choose an output folder for the reel.";
        }
        else if (!IsResolutionValid)
        {
            // Only reachable with a hand-typed custom size (presets are always valid). yuv420p needs even
            // dims, so an odd or out-of-range value would fail deep in ffmpeg. Catch it here with a clear ask.
            ErrorBanner = "Enter a custom resolution with even width and height (16–7680 × 16–4320).";
        }
        else if (IsFfmpegMissing)
        {
            // The dedicated ffmpeg strip below the banner carries the install instructions; this line makes
            // the disabled Generate self-explanatory even when the user is looking at the banner alone.
            ErrorBanner = "ffmpeg isn't installed — see the install instructions below, then press Re-check.";
        }
        else
        {
            ErrorBanner = null;
        }
    }

    private void StartAndClose()
    {
        if (_plan.Count == 0)
        {
            return;
        }

        // "Set once": persist the edited reel defaults so the next dialog is pre-seeded.
        _persistDefaults?.Invoke(s =>
        {
            s.Highlights.ClipLeadInSeconds = LeadInSeconds;
            s.Highlights.ClipLeadOutSeconds = LeadOutSeconds;
            s.Highlights.ReelOutputDirectory = OutputFolder;
            s.Highlights.ReelContainerFormat = ContainerFormat;
            s.Highlights.ReelFps = Fps;
            s.Highlights.ReelConcatenate = Concatenate;
            s.Highlights.ReelCaptureAudio = CaptureAudio;
            s.Highlights.ReelCrf = Crf;
            s.Highlights.ReelBitrateKbps = UseCrf ? null : Math.Max(1, BitrateKbps);
            (s.Highlights.ReelWidth, s.Highlights.ReelHeight) = EffectiveResolution;
        });

        (int width, int height) = EffectiveResolution;
        ReelRequest request = new(
            _plan,
            OutputFolder,
            string.IsNullOrWhiteSpace(BaseFileName) ? "reel" : BaseFileName.Trim(),
            ContainerFormat,
            Fps,
            Concatenate,
            CaptureAudio,
            UseCrf ? Crf : null,
            UseCrf ? null : Math.Max(1, BitrateKbps).ToString(CultureInfo.InvariantCulture),
            NoHudPreset,
            IsDryRunOnly,
            width,
            height);

        // The job service throws if one is already running; the caller (App wiring) guards against opening a
        // second dialog while a job runs, but stay defensive so a race never crashes the UI thread.
        try
        {
            _reelJob?.Start(request);
        }
        catch (InvalidOperationException)
        {
            // A reel is already rendering: leave the strip alone; closing still makes sense.
        }

        // Careful: re-arm the single-CS2 interlock. The modal was thrown away after every Generate, so this latch
        // could never outlive one confirmation. The embedded pane lives for the whole app run: without this
        // line the user confirms "yes, restart CS2" ONCE and every subsequent reel starts silently, which is
        // precisely the destructive-action guard rail this strip exists to be.
        _interlockConfirmed = false;

        Closed?.Invoke(this, EventArgs.Empty);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static long ParseSteamId(string? steamId64) =>
        long.TryParse(steamId64, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : 0;

    private static string WindowText(long start, long end) =>
        $"ticks {start.ToString("N0", CultureInfo.InvariantCulture)}–{end.ToString("N0", CultureInfo.InvariantCulture)}";

    private static double DurationSeconds(long start, long end, int rate) =>
        Math.Max(0, end - start) / (double)(rate > 0 ? rate : 64);

    private static string DurationText(long start, long end, int rate) =>
        $"~{Fmt(DurationSeconds(start, end, rate))}s";

    private static string Fmt(double seconds) => seconds.ToString("0.#", CultureInfo.InvariantCulture);

    private static string SuggestBaseName(IReadOnlyList<HighlightSelection> selections)
    {
        if (selections.Count == 0)
        {
            return "reel";
        }

        HighlightSelection first = selections[0];
        string map = DemoEntry.PrettifyMap(first.Record.Map).Replace(' ', '_');
        string player = DisplayText.Sanitize(first.RawPlayerName).Replace(' ', '_');
        string stem = string.IsNullOrEmpty(player) ? map : $"{map}_{player}";
        // Keep it filename-safe (the field is user-editable; this is only the seed).
        return new string(stem.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray())
            .Trim('_') is { Length: > 0 } cleaned
            ? cleaned.ToLowerInvariant()
            : "reel";
    }

    private readonly record struct DemoFacts(
        string? Sha256,
        int TickRate,
        string MapDisplay,
        string? MapName,
        string FileName,
        bool Exists);

    // A staged highlight and the clip candidate it produced, kept together so the tray can show provenance
    // (source tick) and offer a per-clip ✕ (the HighlightKey). ClipWindows.Candidate is a protected-by-policy
    // pure type and carries neither; pairing beside it avoids changing a computation this step must not touch.
    private readonly record struct CandidateSource(ClipWindows.Candidate Candidate, HighlightKey Key, int SourceTick);
}

/// <summary>
///     One entry in the Reels-tab resolution picker: a named capture size, or the <see cref="Custom" />
///     sentinel that unlocks the width/height fields. Record value-equality lets the ComboBox's
///     <c>SelectedItem</c> round-trip and the settings seed match a preset by its dimensions.
/// </summary>
/// <param name="Display">The list label, e.g. "1080p · 1920×1080 (16:9)".</param>
/// <param name="Width">Preset width in px (0 for the Custom sentinel: the custom field supplies it).</param>
/// <param name="Height">Preset height in px (0 for the Custom sentinel).</param>
/// <param name="IsCustom">True only for the Custom sentinel.</param>
public sealed record ReelResolutionOption(string Display, int Width, int Height, bool IsCustom = false)
{
    /// <summary>The "enter your own size" sentinel: always the last entry in the picker.</summary>
    public static ReelResolutionOption Custom { get; } = new("Custom…", 0, 0, true);
}

/// <summary>
///     One (player · demo) group in the clip tray (provenance + reorder). The
///     group is the tray's ordering unit: ▲▼ move it, ✕ clears it, and its position decides where its clips
///     land in the rendered reel.
/// </summary>
public sealed class ReelClipGroupViewModel
{
    private readonly Func<IClipTrayHost?> _tray;

    /// <param name="header">Group header, e.g. "s1mple · Dust II".</param>
    /// <param name="groupKey">Stable (path, steamId) key: the handle the tray mutates by.</param>
    /// <param name="mapName">RAW map name (e.g. <c>de_dust2</c>) for the accent-dot converter.</param>
    /// <param name="mapDisplay">Prettified map name.</param>
    /// <param name="fileName">Demo file name (no directory): the cross-demo provenance line.</param>
    /// <param name="position">Zero-based position in the tray.</param>
    /// <param name="total">Total group count (drives the ▲▼ end-stops).</param>
    /// <param name="hasProblem">The demo file is missing: the staging-time pre-flight.</param>
    /// <param name="tray">Late-bound tray seam; the pane's <c>Tray</c> can be set after the groups exist.</param>
    public ReelClipGroupViewModel(
        string header, string groupKey, string? mapName, string mapDisplay, string fileName,
        int position, int total, bool hasProblem, Func<IClipTrayHost?> tray)
    {
        Header = header;
        GroupKey = groupKey;
        MapName = mapName;
        MapDisplay = mapDisplay;
        FileName = fileName;
        Position = position;
        CanMoveUp = position > 0;
        CanMoveDown = position < total - 1;
        HasProblem = hasProblem;
        _tray = tray;

        // Built ONCE, not as expression-bodied properties: a `=> new(...)` command hands the binding a fresh
        // instance on every get, so CanExecute notifications land on an object nothing is bound to and the
        // ▲▼ buttons freeze in whatever state they were first evaluated in.
        MoveUpCommand = new RelayCommand(() => _tray()?.MoveGroup(GroupKey, -1), () => CanMoveUp);
        MoveDownCommand = new RelayCommand(() => _tray()?.MoveGroup(GroupKey, +1), () => CanMoveDown);
        RemoveCommand = new RelayCommand(() => _tray()?.RemoveGroup(GroupKey));
    }

    /// <summary>Group header, e.g. "s1mple · Dust II".</summary>
    public string Header { get; }

    /// <summary>Stable (path, steamId) key: see <see cref="ClipTrayKeys.Group" />.</summary>
    public string GroupKey { get; }

    /// <summary>RAW map name for the accent-dot converter (never a code-held colour).</summary>
    public string? MapName { get; }

    /// <summary>Prettified map name, e.g. "Dust2".</summary>
    public string MapDisplay { get; }

    /// <summary>Demo file name, mandatory provenance: a 12-clip cross-demo tray is unreadable without it.</summary>
    public string FileName { get; }

    /// <summary>Zero-based tray position (announced to screen readers via the ▲▼ tooltips).</summary>
    public int Position { get; }

    /// <summary>Position label, e.g. "2 of 5": the keyboard-reachable statement of where this group sits.</summary>
    public string PositionDisplay => (Position + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>▲ is offered only when there is somewhere to go.</summary>
    public bool CanMoveUp { get; }

    /// <summary>▼ is offered only when there is somewhere to go.</summary>
    public bool CanMoveDown { get; }

    /// <summary>The owning demo file is missing (drives the group-level ⚠ note).</summary>
    public bool HasProblem { get; }

    /// <summary>The emitted clips for this player+demo, in ascending start tick.</summary>
    public ObservableCollection<ReelClipRowViewModel> Rows { get; } = [];

    /// <summary>Moves this group one position earlier in the reel.</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>Moves this group one position later in the reel.</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Un-stages every clip in this group.</summary>
    public RelayCommand RemoveCommand { get; }
}

/// <summary>
///     One emitted clip in the dialog's clip list. Always lists its contributing highlights; when
///     more than one contributed (<see cref="IsMerged" />) it also shows the merged-window summary line, so
///     coalescing is <em>visible</em>, not silent.
/// </summary>
public sealed class ReelClipRowViewModel(
    IReadOnlyList<ReelClipContributorViewModel> contributors,
    string mergedWindowText,
    string mergedDurationText,
    double durationSeconds,
    bool hasError)
{
    /// <summary>The highlights that fold into this clip (title + window + ~duration each).</summary>
    public IReadOnlyList<ReelClipContributorViewModel> Contributors { get; } = contributors;

    /// <summary>True when two or more highlights coalesced: drives the "→ merged clip …" summary line.</summary>
    public bool IsMerged { get; } = contributors.Count > 1;

    /// <summary>The emitted clip's window, e.g. "ticks 54,105–54,980".</summary>
    public string MergedWindowText { get; } = mergedWindowText;

    /// <summary>The emitted clip's estimated seconds, e.g. "~13.7s".</summary>
    public string MergedDurationText { get; } = mergedDurationText;

    /// <summary>The emitted clip length in seconds (fed into the dialog's running total).</summary>
    public double DurationSeconds { get; } = durationSeconds;

    /// <summary>Pre-flight: the clip's demo file no longer exists (a per-row "⚠ demo moved").</summary>
    public bool HasError { get; } = hasError;

    /// <summary>Tooltip listing the merged sources.</summary>
    public string SourcesTooltip => string.Join("\n", Contributors.Select(c => c.Title));
}

/// <summary>
///     One contributing highlight of an emitted clip (the bracketed source row), i.e. one thing the user
///     actually staged, so it is also the unit the per-clip ✕ removes.
/// </summary>
public sealed class ReelClipContributorViewModel
{
    private readonly Func<IClipTrayHost?> _tray;

    /// <param name="title">Sanitized rendered title.</param>
    /// <param name="windowText">Tick window, e.g. "ticks 54,105–54,650".</param>
    /// <param name="durationText">Estimated seconds, e.g. "~8.5s".</param>
    /// <param name="roundDisplay">Round label, e.g. "r7".</param>
    /// <param name="tickDisplay">Source firing tick, e.g. "tick 54,321".</param>
    /// <param name="key">The staged highlight's identity: the ✕ handle.</param>
    /// <param name="tray">Late-bound tray seam.</param>
    public ReelClipContributorViewModel(
        string title, string windowText, string durationText, string roundDisplay, string tickDisplay,
        HighlightKey key, Func<IClipTrayHost?> tray)
    {
        Title = title;
        WindowText = windowText;
        DurationText = durationText;
        RoundDisplay = roundDisplay;
        TickDisplay = tickDisplay;
        Key = key;
        _tray = tray;
        RemoveCommand = new RelayCommand(() => _tray()?.RemoveClip(Key));
    }

    /// <summary>Sanitized rendered title.</summary>
    public string Title { get; }

    /// <summary>The contributor's own window, e.g. "ticks 54,105–54,650".</summary>
    public string WindowText { get; }

    /// <summary>The contributor's estimated seconds, e.g. "~8.5s".</summary>
    public string DurationText { get; }

    /// <summary>Round label, e.g. "r7".</summary>
    public string RoundDisplay { get; }

    /// <summary>Source firing tick: the provenance a cross-demo tray needs to be auditable.</summary>
    public string TickDisplay { get; }

    /// <summary>The staged highlight's identity.</summary>
    public HighlightKey Key { get; }

    /// <summary>Un-stages just this highlight.</summary>
    public RelayCommand RemoveCommand { get; }
}
