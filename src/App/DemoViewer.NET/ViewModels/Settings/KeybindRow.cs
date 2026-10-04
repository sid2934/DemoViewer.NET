#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     One row in Settings' "2D playback controls" list: an action, the gesture currently resolving
///     to it, and the affordances to rebind or reset it.
///     <para>
///         The row is <b>display state only</b>: every write goes back through the owning
///         <see cref="SettingsViewModel" />, which validates the candidate against
///         <c>Playback2DKeymapProfile</c> BEFORE persisting. That is why a refused rebind can show
///         <see cref="Conflict" /> here instead of silently doing nothing: the row is told why.
///     </para>
///     <para>
///         A RESERVED action (today <c>Home</c> / fit camera) is listed but not bindable. Hiding it would
///         make the gesture look free, which is the opposite of what the reservation means.
///     </para>
/// </summary>
public sealed partial class KeybindRow : ObservableObject
{
    private readonly SettingsViewModel _owner;

    /// <summary>Why the last rebind attempt was refused, or "". Shown inline under the row.</summary>
    [ObservableProperty]
    private string _conflict = "";

    /// <summary>The gesture text currently resolving to this action ("Shift+E"), or "" when unbound.</summary>
    [ObservableProperty]
    private string _gesture = "";

    /// <summary>True while this row is waiting for the next keypress (the capture affordance is armed).</summary>
    [ObservableProperty]
    private bool _isCapturing;

    /// <summary>Whether this row's gesture comes from the user's overrides rather than the shipped table.</summary>
    [ObservableProperty]
    private bool _isOverridden;

    internal KeybindRow(SettingsViewModel owner, Playback2DBinding binding, string? packLabel = null,
        string? packFeatureId = null)
    {
        _owner = owner;
        Action = binding.Action;
        Label = binding.Description;
        IsReserved = binding.IsReserved;
        PackLabel = packLabel;
        PackFeatureId = packFeatureId;
        ScopeLabel = binding.Scope switch
        {
            Playback2DBindingScope.WhenToolActive => "while drawing",
            Playback2DBindingScope.WhenPaletteFocused => "while tagging",
            Playback2DBindingScope.WhenSuggestionSelected => "while reviewing suggestions",
            _ => "always"
        };

        // Seeded from the SHIPPED profile and replaced by the first Refresh. Reading the static table
        // here instead would work today and be the wrong door: gesture text is the profile's to answer.
        _gesture = Playback2DKeymapProfile.Default.GestureText(binding.Action);
    }

    /// <summary>The action this row binds. The persisted override key: never renamed.</summary>
    public Playback2DAction Action { get; }

    /// <summary>Human description, straight from the keymap table so the two can never drift.</summary>
    public string Label { get; }

    /// <summary>"always", "while drawing" or "while tagging": the scope chip, and the reason two rows can share a key.</summary>
    public string ScopeLabel { get; }

    /// <summary>Declared but unroutable: listed so the gesture does not look free, but not rebindable.</summary>
    public bool IsReserved { get; }

    /// <summary>Whether the rebind affordance is live (everything except a reserved row).</summary>
    public bool IsBindable => !IsReserved;

    /// <summary>Null for a core row; the owning pack's display label (e.g. "Strat Book extension") otherwise.</summary>
    public string? PackLabel { get; }

    /// <summary>The owning pack's umbrella gate id, read live by <see cref="IsVisible" />. Null for a core row.</summary>
    public string? PackFeatureId { get; }

    /// <summary>Whether this row's label chip should show (a pack row only).</summary>
    public bool HasPackLabel => PackLabel is { Length: > 0 };

    /// <summary>
    ///     False while this row's pack is off: its chord does nothing and the row has no business in the
    ///     list. Always true for a core row. Live: re-read on every <see cref="Refresh" />, which the
    ///     gate's own <c>Changed</c> handler triggers.
    /// </summary>
    public bool IsVisible => PackFeatureId is null || _owner.IsPackFeatureEnabled(PackFeatureId);

    /// <summary>The capture button's caption: the prompt while armed, otherwise the current gesture.</summary>
    public string CaptureLabel => IsCapturing
        ? "press a key…"
        : Gesture.Length > 0
            ? Gesture
            : "unbound";

    /// <summary>Whether a refusal reason is showing.</summary>
    public bool HasConflict => Conflict.Length > 0;

    // Push the resolved profile's answer into the bound state. No echo guard is needed (unlike
    // FeatureToggleRow): nothing here persists on change: the rebind and reset paths are explicit
    // commands, so a refresh cannot write anything back.
    internal void Refresh(Playback2DKeymapProfile profile)
    {
        Gesture = profile.GestureText(Action);
        IsOverridden = profile.IsOverridden(Action);
        OnPropertyChanged(nameof(IsVisible));
    }

    partial void OnGestureChanged(string value) => OnPropertyChanged(nameof(CaptureLabel));

    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(CaptureLabel));

    partial void OnConflictChanged(string value) => OnPropertyChanged(nameof(HasConflict));

    // Arms the capture: the NEXT keypress anywhere in the Settings view becomes this row's gesture. The
    // view's tunnelling handler is what routes it here: see SettingsView.axaml.cs.
    [RelayCommand]
    private void BeginCapture() => _owner.BeginKeybindCapture(this);

    // Drops this action's override, reverting it to the shipped gesture. Shown only while overridden.
    [RelayCommand]
    private void Reset() => _owner.ResetKeybind(this);
}
