#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Levels;

/// <summary>
///     The level strip: manual floor picking, the Stacked ⇄ Single toggle, and the AUTO chip.
///     <para>
///         <b>Chips are ordered highest floor first</b>, matching the stacked view's own top-to-bottom
///         reading order (the pre-v2 band layout puts the highest floor on the top band) so the two
///         representations of "which floor" cannot disagree about which way is up.
///     </para>
///     <para>
///         <b>A single-floor map sees none of this.</b> <see cref="HasMultipleLevels" /> collapses the
///         whole strip, which is most maps and the reason the tab looks unchanged on them (plan D9).
///     </para>
/// </summary>
public sealed partial class LevelStripViewModel : ObservableObject
{
    private readonly List<LevelChipViewModel> _scratch = [];
    private bool _applying;

    // A command that persists at the end of its own body owns the save for that whole gesture. Without
    // this the IsAutoEnabled it flips on the way would raise a SECOND SettingsChanged, and every raise is
    // a full read-serialize-temp-write-move-reload of settings.json — two of those per chip click.
    private bool _inGesture;
    private ILevelSurface? _surface;

    [ObservableProperty]
    private bool _hasMultipleLevels;

    /// <summary>
    ///     Whether AutoFollow is offered at all — the <c>playback2d.levels.auto</c> feature gate. With
    ///     the gate off the strip still picks levels; only the AUTO chip disappears (plan D8).
    /// </summary>
    [ObservableProperty]
    private bool _isAutoAvailable = true;

    [ObservableProperty]
    private bool _isAutoEnabled = true;

    [ObservableProperty]
    private bool _isSingleMode;

    /// <summary>The chips, highest level first.</summary>
    public ObservableCollection<LevelChipViewModel> Chips { get; } = [];

    /// <summary>Raised when the user changed a setting worth persisting.</summary>
    public event Action? SettingsChanged;

    /// <summary>The label on the display-mode toggle.</summary>
    public string DisplayModeLabel => IsSingleMode ? "SINGLE" : "STACK";

    /// <summary>Tooltip for the display-mode toggle.</summary>
    public string DisplayModeTooltip => IsSingleMode
        ? "Showing one floor at full height. Switch back to stacked bands."
        : "Showing every floor as a band. Switch to one floor at full height.";

    /// <summary>The level currently shown, when a surface is bound.</summary>
    public MapLevelId ActiveLevelId => _surface?.ActiveLevelId ?? MapLevelId.None;

    /// <summary>Picks a level. Switches to a single pane and turns AutoFollow off.</summary>
    [RelayCommand]
    public void Select(LevelChipViewModel? chip)
    {
        if (chip is null || _surface is null)
        {
            return;
        }

        // Both halves of the gesture are persisted by the ONE raise below: PickLevel also puts the
        // surface into Single, which Refresh mirrors onto IsSingleMode.
        _inGesture = true;
        try
        {
            _surface.PickLevel(chip.Id);
            IsAutoEnabled = false;
            Refresh();
        }
        finally
        {
            _inGesture = false;
        }

        SettingsChanged?.Invoke();
    }

    /// <summary>Flips between stacked bands and a single floor.</summary>
    [RelayCommand]
    public void ToggleDisplayMode()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.DisplayMode = _surface.DisplayMode == LevelDisplayMode.Single
            ? LevelDisplayMode.Stacked
            : LevelDisplayMode.Single;
        Refresh();
        SettingsChanged?.Invoke();
    }

    /// <summary>
    ///     Re-arms AutoFollow.
    ///     <para>
    ///         <b>Deliberately NOT a <c>[RelayCommand]</c>.</b> The AUTO chip is a <c>ToggleButton</c> whose
    ///         <c>IsChecked</c> binds straight to <see cref="IsAutoEnabled" />, and a command on it would
    ///         fight that binding on the un-check half — so a command here could never be the user's path,
    ///         which is exactly how D6 finding 13 happened: the generated <c>EnableAutoCommand</c> was the
    ///         only thing that persisted, and nothing bound it. Persistence now lives in
    ///         <c>OnIsAutoEnabledChanged</c>, on the path the user actually takes.
    ///     </para>
    /// </summary>
    public void EnableAuto()
    {
        if (!IsAutoAvailable)
        {
            return;
        }

        IsAutoEnabled = true; // applies to the surface, refreshes and persists — see the handler
        Refresh();
    }

    /// <summary>
    ///     Binds to the mounted surface. Idempotent, and unbinding (null) drops the subscription — the
    ///     tab's view is destroyed and rebuilt on every activation while this view-model is cached.
    /// </summary>
    /// <param name="surface">The v2 host, or null.</param>
    public void Bind(ILevelSurface? surface)
    {
        if (ReferenceEquals(_surface, surface))
        {
            return;
        }

        if (_surface is not null)
        {
            _surface.LevelStateChanged -= Refresh;
        }

        _surface = surface;

        if (_surface is null)
        {
            Chips.Clear();
            HasMultipleLevels = false;
            return;
        }

        _surface.LevelStateChanged += Refresh;
        ApplyToSurface();
        Refresh();
    }

    /// <summary>
    ///     Restores the persisted display mode and AutoFollow flag, and applies them to the surface.
    /// </summary>
    /// <param name="mode">The persisted <c>Playback2D:LevelDisplayMode</c>.</param>
    /// <param name="autoFollow">The persisted <c>Playback2D:AutoLevelFollow</c>.</param>
    public void ApplySettings(LevelDisplayMode mode, bool autoFollow)
    {
        _applying = true;
        try
        {
            IsSingleMode = mode == LevelDisplayMode.Single;
            IsAutoEnabled = autoFollow;
        }
        finally
        {
            _applying = false;
        }

        ApplyToSurface();
        Refresh();
    }

    /// <summary>The display mode to persist.</summary>
    public LevelDisplayMode DisplayMode =>
        IsSingleMode ? LevelDisplayMode.Single : LevelDisplayMode.Stacked;

    /// <summary>Re-reads the surface's level set and active level.</summary>
    public void Refresh()
    {
        if (_surface is null)
        {
            return;
        }

        IReadOnlyList<MapLevel> levels = _surface.Levels.Levels;
        MapLevelId active = _surface.ActiveLevelId;

        _scratch.Clear();
        for (int i = levels.Count - 1; i >= 0; i--) // highest first
        {
            _scratch.Add(new LevelChipViewModel(levels[i], levels[i].Id == active));
        }

        if (!SameChips(_scratch))
        {
            Chips.Clear();
            for (int i = 0; i < _scratch.Count; i++)
            {
                Chips.Add(_scratch[i]);
            }
        }
        else
        {
            for (int i = 0; i < Chips.Count; i++)
            {
                Chips[i].IsActive = _scratch[i].IsActive;
            }
        }

        HasMultipleLevels = levels.Count > 1;

        _applying = true;
        try
        {
            IsSingleMode = _surface.DisplayMode == LevelDisplayMode.Single;
            IsAutoEnabled = IsAutoAvailable && _surface.AutoLevelFollow;
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnIsAutoAvailableChanged(bool value)
    {
        if (!value)
        {
            // Under _applying on purpose: the GATE went off, which is not the user changing their mind.
            // Persisting it would overwrite a real preference with the state of a feature flag, and a
            // release that shipped the gate off would take AutoFollow away for good.
            _applying = true;
            try
            {
                IsAutoEnabled = false;
            }
            finally
            {
                _applying = false;
            }
        }

        ApplyToSurface();
    }

    /// <summary>
    ///     D6 finding 13. The AUTO chip is a <c>ToggleButton</c> bound straight to
    ///     <see cref="IsAutoEnabled" />, so <b>every real AUTO flip arrives here</b> and none of them ever
    ///     touched <c>EnableAutoCommand</c> — the only path that used to raise
    ///     <see cref="SettingsChanged" />. The toggle applied instantly, looked right, and was forgotten on
    ///     the next launch. Persistence belongs on the path the user takes, not on the one the test drove.
    /// </summary>
    partial void OnIsAutoEnabledChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        ApplyToSurface();
        Refresh();

        if (!_inGesture)
        {
            SettingsChanged?.Invoke();
        }
    }

    partial void OnIsSingleModeChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayModeLabel));
        OnPropertyChanged(nameof(DisplayModeTooltip));
        if (!_applying)
        {
            ApplyToSurface();
        }
    }

    private void ApplyToSurface()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.DisplayMode = IsSingleMode ? LevelDisplayMode.Single : LevelDisplayMode.Stacked;
        _surface.AutoLevelFollow = IsAutoAvailable && IsAutoEnabled;
    }

    // Rebuilding the collection resets the ListBox/ItemsControl containers, which loses focus and blinks
    // the strip. The level set changes at most a handful of times per demo, so compare first.
    private bool SameChips(List<LevelChipViewModel> fresh)
    {
        if (Chips.Count != fresh.Count)
        {
            return false;
        }

        for (int i = 0; i < fresh.Count; i++)
        {
            if (Chips[i].Id != fresh[i].Id || Chips[i].HasRadar != fresh[i].HasRadar ||
                !string.Equals(Chips[i].ZRange, fresh[i].ZRange, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
