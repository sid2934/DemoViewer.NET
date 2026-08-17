#region

using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Settings;

#endregion

namespace DemoViewer.NET.ViewModels.Setup;

/// <summary>
///     Backs the first-run setup wizard: a short stepped flow — Welcome → pick a
///     <b>category</b> → add demo <b>folders</b> → Done — that runs once on a fresh desktop install (no
///     persisted <c>settings.json</c>) and is relaunchable from Settings. Derives from
///     <see cref="ViewModelBase" /> so the app's <c>ViewLocator</c> resolves
///     <c>Views.Setup.FirstRunWizardView</c> for it (desktop window <em>and</em> the WASM overlay).
///     <para>
///         The VM is <b>seeded from <see cref="SettingsService.Current" /></b>: on a genuine first run
///         that is the <see cref="UserCategory.PowerUser" /> default with no folders (so PowerUser is
///         pre-selected); on a re-run from Settings it shows the user's real choices. This is
///         what lets <see cref="Skip" /> be a <em>basis-preserving</em> write — it materialises
///         <c>settings.json</c> (so <see cref="SettingsService.NeedsFirstRun" /> flips false and the
///         wizard never re-triggers) without ever clobbering an existing configuration.
///     </para>
///     <para>
///         The host (a modal <c>FirstRunWizardWindow</c> on desktop, the shell overlay on WASM) closes the
///         wizard on the <see cref="Completed" /> event, raised by both <see cref="Finish" /> and
///         <see cref="Skip" />.
///     </para>
/// </summary>
public sealed partial class FirstRunWizardViewModel : ViewModelBase
{
    // 0 = Welcome, 1 = Category, 2 = Folders, 3 = Done. The last index is the Finish step.
    private const int LastStep = 3;

    private readonly SettingsService _settings;

    // The CS2 demos-folder lookup, run once at construction: the found "replays" folder (or null) plus the
    // Steam libraries actually searched. Drives the folders-step suggestion (found) or the not-found notice.
    private readonly Cs2DemosLookup _cs2Lookup;

    /// <summary>The current step index (0..3). Bound to the view's step-panel visibility + progress.</summary>
    [ObservableProperty]
    private int _currentStep;

    /// <summary>
    ///     Done-page opt-in: run the Visual Walkthrough after setup (default on). Only honoured on
    ///     <see cref="Finish" /> (reaching the Done page) — a <see cref="Skip" /> never starts the tour.
    /// </summary>
    [ObservableProperty]
    private bool _startWalkthrough = true;

    /// <summary>The selected category card (bound to the ListBox SelectedItem). Applied on Finish.</summary>
    [ObservableProperty]
    private CategoryOption _selectedCategoryOption;

    // Desktop folder-picker source, handed in by the view code-behind (mirrors SettingsView's handoff).
    // Null on WASM / headless — the folder picker is then unavailable (see CanAddFolder).
    private IStorageProvider? _storageProvider;

    /// <summary>
    ///     Constructs over the live <see cref="SettingsService" />, seeding the bound state from its
    ///     current values (PowerUser + no folders on a genuine first run; the persisted choices on a re-run).
    /// </summary>
    /// <param name="settings">The live settings service (mutated on Finish / Skip).</param>
    /// <param name="cs2DemosProbe">
    ///     Looks up the CS2 downloaded-demos folder (and what was searched) to offer on the folders step.
    ///     Defaults to the real <see cref="Cs2InstallLocator" />; injected in tests.
    /// </param>
    public FirstRunWizardViewModel(SettingsService settings, Func<Cs2DemosLookup>? cs2DemosProbe = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        // The three category cards + copy come from the single shared source, so the wizard and the
        // Settings screen never drift apart.
        Categories = SettingsViewModel.BuildCategoryOptions();

        AppSettings current = settings.Current;
        _selectedCategoryOption = OptionFor(current.UserCategory);
        foreach (string folder in current.Library.Folders)
        {
            Folders.Add(folder);
        }

        // Run the CS2 lookup once. The suggestion's added/addable state tracks the Folders list.
        _cs2Lookup = (cs2DemosProbe ?? Cs2InstallLocator.FindDemos)();
        SearchedDirectories = _cs2Lookup.SearchedDirectories;
        Folders.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsDetectedFolderAdded));
            OnPropertyChanged(nameof(CanAddDetectedFolder));
        };
    }

    /// <summary>The auto-detected CS2 downloaded-demos folder, or null when none was found.</summary>
    public string? DetectedDemosFolder => _cs2Lookup.DemosDirectory;

    /// <summary>True when a CS2 demos folder was detected — drives the folders-step suggestion's visibility.</summary>
    public bool HasDetectedDemosFolder => DetectedDemosFolder is not null;

    /// <summary>True once the detected folder is in the pending set (the suggestion then shows "Added").</summary>
    public bool IsDetectedFolderAdded =>
        DetectedDemosFolder is { } d && Folders.Contains(d, StringComparer.Ordinal);

    /// <summary>True when the detected folder exists and has not yet been added (enables the Add button).</summary>
    public bool CanAddDetectedFolder => HasDetectedDemosFolder && !IsDetectedFolderAdded;

    /// <summary>
    ///     True when auto-detection found nothing — drives the "couldn't auto-detect" notice on the folders
    ///     step (mutually exclusive with <see cref="HasDetectedDemosFolder" />).
    /// </summary>
    public bool ShowNotFoundNotice => DetectedDemosFolder is null;

    /// <summary>The Steam library directories searched (empty when no Steam install was found).</summary>
    public IReadOnlyList<string> SearchedDirectories { get; }

    /// <summary>True when at least one Steam library was searched (so the notice can list them).</summary>
    public bool HasSearchedDirectories => SearchedDirectories.Count > 0;

    /// <summary>The not-found notice text — adapts to whether any Steam libraries were searched.</summary>
    public string NotFoundMessage => HasSearchedDirectories
        ? "Couldn't find your Counter-Strike 2 demos folder automatically. Searched these Steam libraries:"
        : "Couldn't auto-detect your Counter-Strike 2 demos folder — no Steam installation was found in the "
          + "usual locations. Add your demos folder manually below.";

    /// <summary>The three selectable user-category cards, each with a one-line description.</summary>
    public IReadOnlyList<CategoryOption> Categories { get; }

    /// <summary>Demo folders the user is adding, applied to <c>AppSettings.Library.Folders</c> on Finish.</summary>
    public ObservableCollection<string> Folders { get; } = [];

    /// <summary>
    ///     Whether the folder picker is available. The browser sandbox has no OS folder picker, so Add is
    ///     disabled there (the folders step is optional anyway).
    /// </summary>
    public bool CanAddFolder { get; } = !OperatingSystem.IsBrowser();

    /// <summary>The effective user category — the selected card's value. Convenience for callers/tests.</summary>
    public UserCategory SelectedCategory => SelectedCategoryOption.Value;

    // ── Step-driven view state (raised together in OnCurrentStepChanged) ──────────────────────────
    /// <summary>True on the Welcome step (0).</summary>
    public bool IsWelcomeStep => CurrentStep == 0;

    /// <summary>True on the pick-your-category step (1).</summary>
    public bool IsCategoryStep => CurrentStep == 1;

    /// <summary>True on the add-your-folders step (2).</summary>
    public bool IsFoldersStep => CurrentStep == 2;

    /// <summary>True on the Done step (3).</summary>
    public bool IsDoneStep => CurrentStep == LastStep;

    /// <summary>Back is offered on every step past the first.</summary>
    public bool CanGoBack => CurrentStep > 0;

    /// <summary>Next is offered on every step before the last.</summary>
    public bool ShowNext => CurrentStep < LastStep;

    /// <summary>Finish is offered only on the last step.</summary>
    public bool ShowFinish => CurrentStep >= LastStep;

    /// <summary>Skip is offered on every step before the last (the last step's action is Finish).</summary>
    public bool ShowSkip => CurrentStep < LastStep;

    /// <summary>Progress-bar fill fraction (0..1) — the 1-based current step over the total step count.</summary>
    public double StepProgress => (double)(CurrentStep + 1) / (LastStep + 1);

    /// <summary>Header caption, e.g. "Step 2 of 4".</summary>
    public string StepIndicatorText => $"Step {CurrentStep + 1} of {LastStep + 1}";

    /// <summary>Raised when the wizard is done (Finish or Skip). The host closes the window / clears the overlay.</summary>
    public event EventHandler? Completed;

    /// <summary>
    ///     Supplies the desktop folder-picker source (mirrors <c>SettingsView</c>'s storage-provider handoff).
    ///     Null on WASM / headless leaves the picker unavailable.
    /// </summary>
    public void SetStorageProvider(IStorageProvider? provider) => _storageProvider = provider;

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWelcomeStep));
        OnPropertyChanged(nameof(IsCategoryStep));
        OnPropertyChanged(nameof(IsFoldersStep));
        OnPropertyChanged(nameof(IsDoneStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(ShowNext));
        OnPropertyChanged(nameof(ShowFinish));
        OnPropertyChanged(nameof(ShowSkip));
        OnPropertyChanged(nameof(StepProgress));
        OnPropertyChanged(nameof(StepIndicatorText));
    }

    partial void OnSelectedCategoryOptionChanged(CategoryOption value) =>
        OnPropertyChanged(nameof(SelectedCategory));

    /// <summary>Advances to the next step (clamped at the last step).</summary>
    [RelayCommand]
    private void Next()
    {
        if (CurrentStep < LastStep)
        {
            CurrentStep++;
        }
    }

    /// <summary>Returns to the previous step (clamped at the first step).</summary>
    [RelayCommand]
    private void Back()
    {
        if (CurrentStep > 0)
        {
            CurrentStep--;
        }
    }

    /// <summary>Selects a category by value (e.g. a card button, or a test).</summary>
    [RelayCommand]
    private void SelectCategory(UserCategory category) => SelectedCategoryOption = OptionFor(category);

    /// <summary>
    ///     Adds one or more folders via the OS folder picker (desktop). No-op when no picker is wired
    ///     (WASM / headless) — the Add button is disabled there via <see cref="CanAddFolder" />.
    /// </summary>
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (_storageProvider is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> picked = await _storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Add demo folder",
                AllowMultiple = true
            });

        foreach (string path in picked
                     .Select(f => f.TryGetLocalPath())
                     .Where(p => !string.IsNullOrEmpty(p))
                     .Cast<string>())
        {
            if (!Folders.Contains(path, StringComparer.Ordinal))
            {
                Folders.Add(path);
            }
        }
    }

    /// <summary>
    ///     Adds the auto-detected CS2 demos folder to the pending set (the folders-step suggestion's one-click
    ///     Add). No-op if nothing was detected or it is already present.
    /// </summary>
    [RelayCommand]
    private void AddDetectedFolder()
    {
        if (DetectedDemosFolder is { } folder && !Folders.Contains(folder, StringComparer.Ordinal))
        {
            Folders.Add(folder);
        }
    }

    /// <summary>Removes a folder from the pending set (the per-row ✕ affordance).</summary>
    [RelayCommand]
    private void RemoveFolder(string path) => Folders.Remove(path);

    /// <summary>
    ///     Applies the chosen category + folders and marks setup complete
    ///     (<see cref="AppSettings.FirstRunCompleted" /> → true), which flips
    ///     <see cref="SettingsService.NeedsFirstRun" /> to false (so the wizard never re-triggers) and applies
    ///     the category's gate defaults automatically. Raises <see cref="Completed" />.
    /// </summary>
    [RelayCommand]
    private void Finish()
    {
        _settings.Write(s =>
        {
            s.UserCategory = SelectedCategoryOption.Value;
            s.Library.Folders = Folders.ToArray();
            s.FirstRunCompleted = true;
        });
        // Honour the Done-page opt-in only on Finish; the host reads this after Completed to start the tour.
        ShouldStartWalkthrough = StartWalkthrough;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Whether the host should launch the Visual Walkthrough after the wizard closes. True only when the
    ///     user reached the Done page via <see cref="Finish" /> with the opt-in on; a <see cref="Skip" />
    ///     leaves it false. Read once by the composition root on the <see cref="Completed" /> event.
    /// </summary>
    public bool ShouldStartWalkthrough { get; private set; }

    /// <summary>
    ///     Dismisses the wizard without applying any new choice, but still marks setup complete
    ///     (<see cref="AppSettings.FirstRunCompleted" /> → true) so <see cref="SettingsService.NeedsFirstRun" />
    ///     flips false and the wizard never re-triggers. The rest of the basis is preserved unchanged — on a
    ///     genuine first run that is the PowerUser default with no folders; on a re-run it keeps the user's
    ///     existing configuration rather than clobbering it. Raises <see cref="Completed" />.
    /// </summary>
    [RelayCommand]
    private void Skip()
    {
        _settings.Write(s => s.FirstRunCompleted = true);
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private CategoryOption OptionFor(UserCategory category)
    {
        foreach (CategoryOption option in Categories)
        {
            if (option.Value == category)
            {
                return option;
            }
        }

        return Categories[1]; // PowerUser — the default tier
    }
}
