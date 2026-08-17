#region

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Features;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     One row in the Settings feature-toggle list (P2a-ii): a single <see cref="FeatureCatalog" /> entry the
///     user can force on/off regardless of category. The row's DISPLAYED state is authoritative from the
///     <see cref="IFeatureGate" /> (so cascade + group semantics are honoured), while flipping its
///     <see cref="IsEnabled" /> toggle writes an explicit <c>AppSettings.Features.Overrides[id]</c> through
///     the owning <see cref="SettingsViewModel" />.
///     <para>
///         <b>Echo guard.</b> A gate-driven refresh (<see cref="Refresh" />) pushes the gate's decision into
///         <see cref="IsEnabled" /> under <see cref="_applyingRefresh" /> so the change-hook does NOT persist
///         it straight back as a new override — the row-level analog of the VM's <c>_applyingExternal</c>
///         guard. This is deliberately NOT the VM's <c>_writing</c> guard: an <em>external</em> category
///         change refreshes rows while <c>_writing</c> is false, and a <c>_writing</c>-only guard would then
///         materialise a spurious override for every row whose default shifted.
///     </para>
/// </summary>
public sealed partial class FeatureToggleRow : ObservableObject
{
    // The live gate — the source of truth for IsEnabled, and the value a locked row bounces its setter back
    // to (Required / group-follower). Reads only; the row never mutates it.
    private readonly IFeatureGate _gate;
    private readonly SettingsViewModel _owner;

    // true while the owner pushes the AUTHORITATIVE gate state into this row (Refresh): the IsEnabled
    // change-hook then does NOT persist it back as an override. See the class remarks.
    private bool _applyingRefresh;

    /// <summary>Whether the feature resolves visible right now (the gate's decision — GET is authoritative).</summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>
    ///     Whether an explicit override exists for this feature (it differs from the category default) — drives
    ///     the subtle "overridden" indicator and the per-row clear-override affordance.
    /// </summary>
    [ObservableProperty]
    private bool _isOverridden;

    internal FeatureToggleRow(
        SettingsViewModel owner, IFeatureGate gate, FeatureDescriptor descriptor, int indentLevel)
    {
        _owner = owner;
        _gate = gate;
        FeatureId = descriptor.Id;
        Label = descriptor.Label;
        Description = descriptor.Description;
        Scope = descriptor.Scope;
        IndentLevel = indentLevel;
        IsRequired = descriptor.Required;

        // A grouped feature toggles atomically from its LEADER (the gate resolves every member's own-state
        // from the leader). So a NON-leader member's own override is inert — the row must not offer an
        // independent toggle for it (that would persist a phantom override that snaps back). Detect it and
        // present it as "follows <leader>", locked like a Required row.
        if (descriptor.GroupId is { } groupId)
        {
            FeatureDescriptor? leader = FeatureCatalog.GroupLeader(groupId);
            if (leader is not null && !string.Equals(leader.Id, descriptor.Id, StringComparison.Ordinal))
            {
                IsGroupFollower = true;
                FollowsLabel = leader.Label;
            }
        }
    }

    /// <summary>The stable catalog id — the persisted override key.</summary>
    public string FeatureId { get; }

    /// <summary>Short human name (from the descriptor).</summary>
    public string Label { get; }

    /// <summary>One-line explanation (from the descriptor).</summary>
    public string Description { get; }

    /// <summary>Tab / SubFeature / Chrome — for the scope chip and grouping.</summary>
    public FeatureScope Scope { get; }

    /// <summary>0 for a Tab/Chrome row, 1 for a SubFeature nested under its parent tab.</summary>
    public int IndentLevel { get; }

    /// <summary>A Required feature can never be disabled — the toggle is locked on with a "required" hint.</summary>
    public bool IsRequired { get; }

    /// <summary>
    ///     True when this is a NON-leader member of a toggle-group: it follows its group leader (its own
    ///     override is inert), so its toggle is locked here and the leader's toggle drives the whole group.
    /// </summary>
    public bool IsGroupFollower { get; }

    /// <summary>The leader's label for the "follows &lt;leader&gt;" hint (null unless <see cref="IsGroupFollower" />).</summary>
    public string? FollowsLabel { get; }

    /// <summary>The toggle is interactive only when the feature is neither Required nor a group follower.</summary>
    public bool IsInteractive => !IsRequired && !IsGroupFollower;

    /// <summary>Whether a locked-state hint chip should show (Required or group-follower).</summary>
    public bool HasLockHint => IsRequired || IsGroupFollower;

    /// <summary>The locked-state hint text: "required", "follows &lt;leader&gt;", or empty.</summary>
    public string LockHint => IsRequired
        ? "required"
        : IsGroupFollower
            ? $"follows {FollowsLabel}"
            : string.Empty;

    /// <summary>Short scope chip text ("Tab" / "Sub" / "Chrome").</summary>
    public string ScopeLabel => Scope switch
    {
        FeatureScope.Tab => "Tab",
        FeatureScope.SubFeature => "Sub",
        FeatureScope.Chrome => "Chrome",
        _ => Scope.ToString()
    };

    /// <summary>Left margin that renders <see cref="IndentLevel" /> as an indent (20px per level).</summary>
    public Thickness IndentMargin => new(IndentLevel * 20, 0, 0, 0);

    // Push the gate's authoritative decision into the bound state WITHOUT echoing a write (the change-hook is
    // neutered by _applyingRefresh). Concrete Dictionary param (CA1859) — the only caller passes
    // AppSettings.Features.Overrides.
    internal void Refresh(IFeatureGate gate, Dictionary<string, bool> overrides)
    {
        _applyingRefresh = true;
        try
        {
            IsEnabled = gate.IsEnabled(FeatureId);
            IsOverridden = overrides is not null && overrides.ContainsKey(FeatureId);
        }
        finally
        {
            _applyingRefresh = false;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_applyingRefresh)
        {
            return; // a gate-driven refresh, not a user toggle — never persist it back.
        }

        if (IsRequired || IsGroupFollower)
        {
            // Locked row. Required can never be disabled; a group FOLLOWER's own override is inert (the gate
            // resolves the whole group from the leader), so persisting one would be a phantom that snaps
            // back. Bounce the setter to the authoritative gate state WITHOUT writing (the toggle is also
            // disabled in the UI; this guards the programmatic path). Guarded so the bounce is not a toggle.
            _applyingRefresh = true;
            try
            {
                IsEnabled = _gate.IsEnabled(FeatureId);
            }
            finally
            {
                _applyingRefresh = false;
            }

            return;
        }

        _owner.WriteFeatureOverride(FeatureId, value);
    }

    // Clear just this row's override (revert to the category default). Shown only while IsOverridden.
    [RelayCommand]
    private void ClearOverride() => _owner.ClearFeatureOverride(FeatureId);
}
