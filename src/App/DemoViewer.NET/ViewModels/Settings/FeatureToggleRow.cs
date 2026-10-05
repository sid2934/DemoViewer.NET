#region

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.Manifest;
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
///         it straight back as a new override, the row-level analog of the VM's <c>_applyingExternal</c>
///         guard. This is deliberately NOT the VM's <c>_writing</c> guard: an <em>external</em> category
///         change refreshes rows while <c>_writing</c> is false, and a <c>_writing</c>-only guard would then
///         materialise a spurious override for every row whose default shifted.
///     </para>
/// </summary>
public sealed partial class FeatureToggleRow : ObservableObject
{
    // The live gate: the source of truth for IsEnabled, and the value a locked row bounces its setter back
    // to (Required / group-follower). Reads only; the row never mutates it.
    private readonly IFeatureGate _gate;
    private readonly SettingsViewModel _owner;

    // true while the owner pushes the AUTHORITATIVE gate state into this row (Refresh): the IsEnabled
    // change-hook then does NOT persist it back as an override. See the class remarks.
    private bool _applyingRefresh;

    /// <summary>Whether the feature resolves visible right now (the gate's decision: GET is authoritative).</summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>
    ///     True while this row's owning pack (<see cref="OwnerPackId" />) resolves on. Always true for a row
    ///     with no owning pack (including a pack's own master row: a pack is not owned by itself). A pack
    ///     CHILD row is interactive only while its pack is on, "enabled only while the master is on", even
    ///     though the cascade already resolves <see cref="IsEnabled" /> off by itself; the row's own stored
    ///     override is untouched either way, so it keeps its value for when the pack comes back.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    [NotifyPropertyChangedFor(nameof(HasLockHint))]
    [NotifyPropertyChangedFor(nameof(LockHint))]
    private bool _isPackEnabled = true;

    /// <summary>
    ///     Whether an explicit override exists for this feature (it differs from the category default), drives
    ///     the subtle "overridden" indicator and the per-row clear-override affordance.
    /// </summary>
    [ObservableProperty]
    private bool _isOverridden;

    /// <summary>
    ///     True while this row's own pack is running "delete extension data". Set by
    ///     <see cref="SettingsViewModel" /> from the matching <c>ExtensionDataActionViewModel.IsBusy</c>,
    ///     the one case where a pack's MASTER row needs to lock on something other than
    ///     <see cref="IsPackEnabled" /> (a pack does not own itself, so that is always true for its own row):
    ///     flipping the pack on mid-delete is exactly the race the delete's own gate re-checks guard against,
    ///     and locking the switch here keeps the user from starting that race from the UI.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    [NotifyPropertyChangedFor(nameof(HasLockHint))]
    [NotifyPropertyChangedFor(nameof(LockHint))]
    private bool _isDeleteBusy;

    /// <param name="owner">The settings view model that persists this row's overrides.</param>
    /// <param name="gate">The live gate; the source of truth for <see cref="IsEnabled" />.</param>
    /// <param name="descriptor">The catalog row.</param>
    /// <param name="indentLevel">Nesting depth for the indent.</param>
    /// <param name="platformUnavailable">True when the feature cannot exist on this host.</param>
    /// <param name="version">The extension's version from its manifest; a pack master row only.</param>
    /// <param name="incompatibility">
    ///     Why the extension cannot load on this app (<c>PackStatus.Problem</c>), or null. A row with one is
    ///     locked off: its pack composed nothing, so no override could take effect.
    /// </param>
    /// <param name="source">Where the extension's assembly came from (<c>PackStatus.Source</c>); a pack master row only.</param>
    /// <param name="loadNote">
    ///     Why a staged update of this extension was not loaded (<c>PackStatus.Rejected</c>, one line per
    ///     candidate), or null. Informational: the row stays interactive, since the bundled copy is running.
    /// </param>
    /// <param name="update">The extension's update line; a pack master row only, else null.</param>
    internal FeatureToggleRow(
        SettingsViewModel owner, IFeatureGate gate, FeatureDescriptor descriptor, int indentLevel,
        bool platformUnavailable = false, string? version = null, string? incompatibility = null,
        PackSource? source = null, string? loadNote = null, ExtensionUpdateRow? update = null)
    {
        Update = update;
        _owner = owner;
        _gate = gate;
        FeatureId = descriptor.Id;
        Label = descriptor.Label;
        Description = descriptor.Description;
        Scope = descriptor.Scope;
        IndentLevel = indentLevel;
        IsRequired = descriptor.Required;
        IsPlatformUnavailable = platformUnavailable;
        OwnerPackId = descriptor.OwnerPackId;
        Version = version;
        Incompatibility = incompatibility;
        Source = source;
        LoadNote = loadNote;

        // A grouped feature toggles atomically from its LEADER (the gate resolves every member's own-state
        // from the leader). So a NON-leader member's own override is inert. The row must not offer an
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

    /// <summary>The stable catalog id: the persisted override key.</summary>
    public string FeatureId { get; }

    /// <summary>Short human name (from the descriptor).</summary>
    public string Label { get; }

    /// <summary>One-line explanation (from the descriptor).</summary>
    public string Description { get; }

    /// <summary>Tab / SubFeature / Chrome: for the scope chip and grouping.</summary>
    public FeatureScope Scope { get; }

    /// <summary>0 for a Tab/Chrome row, 1 for a SubFeature nested under its parent tab.</summary>
    public int IndentLevel { get; }

    /// <summary>A Required feature can never be disabled: the toggle is locked on with a "required" hint.</summary>
    public bool IsRequired { get; }

    /// <summary>
    ///     True when this is a NON-leader member of a toggle-group: it follows its group leader (its own
    ///     override is inert), so its toggle is locked here and the leader's toggle drives the whole group.
    /// </summary>
    public bool IsGroupFollower { get; }

    /// <summary>The leader's label for the "follows &lt;leader&gt;" hint (null unless <see cref="IsGroupFollower" />).</summary>
    public string? FollowsLabel { get; }

    /// <summary>
    ///     True when this feature cannot exist on THIS host whatever the user's override says: the
    ///     browser head and one of <c>ShellModuleFeatureGate.DesktopOnlyIds</c>.
    ///     <para>
    ///         This list binds the raw <see cref="IFeatureGate" />, which resolves catalog and override
    ///         state and knows nothing about the platform; modules read the same ids through
    ///         <c>ShellModuleFeatureGate</c>, which ANDs the platform in. So the browser showed a live,
    ///         ON "Video export" toggle for a capability forced off one layer out, and flipping it
    ///         persisted an override that nothing would ever honour.
    ///     </para>
    /// </summary>
    public bool IsPlatformUnavailable { get; }

    /// <summary>
    ///     The pack that owns this row (<see cref="FeatureDescriptor.OwnerPackId" />), or null for a row not
    ///     contributed by any pack, including a pack's own master row. Drives <see cref="IsPackEnabled" />.
    /// </summary>
    public string? OwnerPackId { get; }

    /// <summary>The extension's version (its manifest's), shown beside a pack master row's label; null elsewhere.</summary>
    public string? Version { get; }

    /// <summary>Whether <see cref="Version" /> is set.</summary>
    public bool HasVersion => Version is not null;

    /// <summary>
    ///     Why this extension cannot load on this app, in user terms, or null when it can. Set only on the
    ///     master row of a pack that failed the compatibility check, which Settings synthesizes
    ///     since such a pack has no catalog row.
    /// </summary>
    public string? Incompatibility { get; }

    /// <summary>Whether <see cref="Incompatibility" /> is set.</summary>
    public bool IsIncompatible => Incompatibility is not null;

    /// <summary>Where the extension's assembly came from; null on every row but a pack master row.</summary>
    public PackSource? Source { get; }

    /// <summary>The source in user terms beside the version, "(bundled)" or "(installed update)"; null when <see cref="Source" /> is.</summary>
    public string? SourceLabel => Source is null ? null : $"({Source.Label})";

    /// <summary>Whether <see cref="SourceLabel" /> is set.</summary>
    public bool HasSource => Source is not null;

    /// <summary>
    ///     Why a staged update of this extension did not load, one line per rejected candidate, or
    ///     null. Shown under the description; unlike <see cref="Incompatibility" /> it locks nothing, since the
    ///     copy that is running works.
    /// </summary>
    public string? LoadNote { get; }

    /// <summary>Whether <see cref="LoadNote" /> is set.</summary>
    public bool HasLoadNote => LoadNote is not null;

    /// <summary>
    ///     The extension's update line: installed against the feed, with Check and Update. Set on a
    ///     pack master row only; null on every other row.
    /// </summary>
    public ExtensionUpdateRow? Update { get; }

    /// <summary>Whether <see cref="Update" /> is set.</summary>
    public bool HasUpdate => Update is not null;

    /// <summary>
    ///     On an extension's master row: whether the extension was switched off for this session after errors,
    ///     and the ways out. Null on every other row.
    /// </summary>
    public ExtensionFaultNotice? Fault { get; internal set; }

    /// <summary>
    ///     The toggle is interactive only when the feature is neither Required, nor a group follower, nor
    ///     unavailable on this platform, nor an incompatible extension, nor a pack child whose pack is
    ///     currently off.
    /// </summary>
    public bool IsInteractive =>
        !IsRequired && !IsGroupFollower && !IsPlatformUnavailable && !IsIncompatible && IsPackEnabled && !IsDeleteBusy;

    /// <summary>Whether a locked-state hint chip should show.</summary>
    public bool HasLockHint =>
        IsRequired || IsGroupFollower || IsPlatformUnavailable || IsIncompatible || !IsPackEnabled || IsDeleteBusy;

    /// <summary>
    ///     The locked-state hint text. The platform answer comes FIRST: it is the one the user cannot
    ///     change from anywhere, so telling them "required" or "follows X" would send them looking for a
    ///     lever that would not help. An incompatible extension is the same kind of answer.
    /// </summary>
    public string LockHint => IsPlatformUnavailable
        ? "unavailable in the browser"
        : IsIncompatible
            ? "incompatible"
            : IsRequired
                ? "required"
                : IsGroupFollower
                    ? $"follows {FollowsLabel}"
                    : !IsPackEnabled
                        ? "extension is off"
                        : IsDeleteBusy
                            ? "deleting extension data"
                            : string.Empty;

    /// <summary>Short scope chip text ("Tab" / "Sub" / "Chrome" / "Extension").</summary>
    public string ScopeLabel => Scope switch
    {
        FeatureScope.Tab => "Tab",
        FeatureScope.SubFeature => "Sub",
        FeatureScope.Chrome => "Chrome",
        FeatureScope.Pack => "Extension",
        _ => Scope.ToString()
    };

    /// <summary>Left margin that renders <see cref="IndentLevel" /> as an indent (20px per level).</summary>
    public Thickness IndentMargin => new(IndentLevel * 20, 0, 0, 0);

    // Push the gate's authoritative decision into the bound state WITHOUT echoing a write (the change-hook is
    // neutered by _applyingRefresh). Concrete Dictionary param (CA1859): the only caller passes
    // AppSettings.Features.Overrides.
    internal void Refresh(IFeatureGate gate, Dictionary<string, bool> overrides)
    {
        _applyingRefresh = true;
        try
        {
            // A platform-unavailable row shows OFF regardless of what the raw gate answers: the gate
            // resolves catalog + override and does not know the host, and this row has to agree with
            // what the module will actually see through ShellModuleFeatureGate.
            IsEnabled = !IsPlatformUnavailable && !IsIncompatible && gate.IsEnabled(FeatureId);
            IsOverridden = overrides is not null && overrides.ContainsKey(FeatureId);
            IsPackEnabled = OwnerPackId is null || gate.IsEnabled(OwnerPackId);
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
            return; // a gate-driven refresh, not a user toggle: never persist it back.
        }

        if (IsPlatformUnavailable || IsIncompatible)
        {
            // Locked the hardest of the three: no override the user could write would make the module's
            // own gate answer true here, so persisting one would be a preference that can never take
            // effect and would then follow them to a desktop head where they never asked for it. An
            // incompatible extension composed nothing, so an override for it is just as inert.
            _applyingRefresh = true;
            try
            {
                IsEnabled = false;
            }
            finally
            {
                _applyingRefresh = false;
            }

            return;
        }

        if (IsRequired || IsGroupFollower || !IsPackEnabled || IsDeleteBusy)
        {
            // Locked row. Required can never be disabled; a group FOLLOWER's own override is inert (the gate
            // resolves the whole group from the leader); a pack CHILD while its pack is off is locked the
            // same way, so a stray programmatic set never writes a new override here: the row's EXISTING
            // override (if any) is untouched, which is how it "keeps its own value" for when the pack comes
            // back. A pack's own MASTER row mid-delete (IsDeleteBusy) is locked the same way, so a stray
            // flip cannot race the delete. Bounce the setter to the authoritative gate state WITHOUT writing
            // (the toggle is also disabled in the UI; this guards the programmatic path). Guarded so the
            // bounce is not a toggle.
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
