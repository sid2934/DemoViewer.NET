#region

using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.Features;

/// <summary>
///     Where a gated feature lives in the shell, for cascade + settings-UI grouping.
/// </summary>
public enum FeatureScope
{
    /// <summary>A top-level workspace tab (Library, Stats, Parser, …). Has no parent; can be cascaded FROM.</summary>
    Tab,

    /// <summary>A sub-surface OWNED by a tab (parser hex pane, entity schema lens, …). Cascades off with its parent tab.</summary>
    SubFeature,

    /// <summary>Global chrome not owned by any single tab (toolbar/NavStrip/debugger rail). No parent → never cascaded.</summary>
    Chrome
}

/// <summary>
///     One code-defined, stable gate entry: a feature/surface the feature-gating layer can show or hide
///     per user category. Immutable data: the live on/off decision is computed by <see cref="IFeatureGate" />
///     from these fields plus the user's category and explicit overrides. The single source of truth for the
///     set of descriptors is <see cref="FeatureCatalog" />.
/// </summary>
/// <param name="Id">
///     Stable identifier used everywhere the feature is referenced (settings overrides, gate queries, UI
///     bindings), e.g. <c>"tab.parser"</c>, <c>"parser.hex"</c>, <c>"chrome.debugger"</c>. Never renamed
///     once shipped: it is the persisted override key.
/// </param>
/// <param name="Scope">Tab / SubFeature / Chrome: drives cascade and settings-UI grouping.</param>
/// <param name="Label">Short human name for the settings UI and the "N features hidden" messaging.</param>
/// <param name="Description">One-line explanation of the feature for the settings UI.</param>
/// <param name="ParentId">
///     For a <see cref="FeatureScope.SubFeature" />, the id of the owning tab, the cascade edge: when the
///     parent tab resolves disabled, this sub-feature is implicitly off. <c>null</c> for tabs and chrome
///     (chrome is global, not tab-owned).
/// </param>
/// <param name="GroupId">
///     Optional id of a group whose members toggle atomically. Every member resolves to the group LEADER's
///     resolved own-state (see <see cref="IFeatureGate" />). The leader's <see cref="Defaults" /> are
///     authoritative for the group; a non-leader member's own <see cref="Defaults" />/override entry is
///     informational only (never consulted while grouped).
/// </param>
/// <param name="Required">
///     When <c>true</c> the feature can never be disabled by an override or category default (a Required tab
///     is always visible). A Required <em>sub-feature</em> is still hidden by cascade when its parent tab is
///     off ("on when the tab is on").
/// </param>
/// <param name="Defaults">
///     Category → default-enabled map. Used when no explicit override exists. Every category should have an
///     entry; a missing entry is treated as <c>false</c>.
/// </param>
public sealed record FeatureDescriptor(
    string Id,
    FeatureScope Scope,
    string Label,
    string Description,
    string? ParentId,
    string? GroupId,
    bool Required,
    IReadOnlyDictionary<UserCategory, bool> Defaults);
