#region

using CommunityToolkit.Mvvm.ComponentModel;
using CS2DemoKit.Analysis.Abstractions;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.ViewModels.Highlights;

/// <summary>
///     Stable identity of one harvested highlight across detail-pane rebuilds and tab switches
///. A <see cref="CachedHighlightEvent" /> carries no primary key, so
///     the clip tray is keyed by this composite — surviving a re-projection of the plan and a switch to
///     another demo (the tray spans demos by construction).
/// </summary>
/// <param name="FilePath">Owning demo path (the cross-demo half of the identity).</param>
/// <param name="RulesetId">Ruleset that emitted the highlight.</param>
/// <param name="HighlightId">Highlight id inside that ruleset.</param>
/// <param name="Tick">Firing tick, FRAME CLOCK (never server tick).</param>
/// <param name="PlayerSlot">Attributed player slot — two players can fire the same rule on the same tick.</param>
public readonly record struct HighlightKey(
    string FilePath,
    string RulesetId,
    string HighlightId,
    int Tick,
    int PlayerSlot);

/// <summary>
///     One staged highlight, with the owning cache record bundled so the reel config pane has EVERYTHING clip
///     assembly needs without re-touching the store: the record carries tickRate / tickCount / rounds / demo
///     path / roster; the highlight carries tick / round / rendered title. This is the shape
///     <see cref="HighlightsTabViewModel.StagedSelections" /> exposes — the config pane builds
///     <c>ClipWindows.Candidate</c> from each.
///     <para>
///         Carries the UNIFIED <see cref="DemoCacheRecord" /> (step 4's absorption). The one shape change
///         that matters: the unified record stores each player ONCE and references them by slot, rather than
///         repeating a name and steamId on every highlight row. So name and steamId are resolved here, in one
///         place, instead of being read off the event by four different consumers — which is also what makes
///         a rename coherent rather than frozen into every event ever harvested.
///     </para>
/// </summary>
/// <param name="Record">The owning demo's cache record (demo facts + roster).</param>
/// <param name="Highlight">The harvested highlight (verbatim from the cache).</param>
public sealed record HighlightSelection(DemoCacheRecord Record, CachedHighlightEvent Highlight)
{
    /// <summary>The composite identity used by the clip tray.</summary>
    public HighlightKey Key => new(Record.Path, Highlight.RulesetId, Highlight.HighlightId,
        Highlight.Tick, Highlight.PlayerSlot);

    /// <summary>The attributed player, resolved by slot against the record's roster; null when absent.</summary>
    public CachedPlayerInfo? Player =>
        Record.Players.FirstOrDefault(p => p.Slot == Highlight.PlayerSlot);

    /// <summary>
    ///     The player's RAW in-demo name — CSVG's <c>spec_player</c> currency, never sanitized. Empty when the
    ///     roster does not carry the slot (a highlight is still worth showing unattributed).
    /// </summary>
    public string RawPlayerName => Player?.Name ?? "";

    /// <summary>SteamID64, or empty. Used as a filter key, so it must not be null.</summary>
    public string SteamId64 => Player?.SteamId64 ?? "";
}

/// <summary>
///     The tray-mutation seam the reel config pane calls back through.
///     <para>
///         <b>Why an interface and not events.</b> The config pane rebuilds its whole
///         <c>ClipGroups</c> collection on every lead-in/lead-out keystroke, so the ▲▼✕ buttons live on
///         objects that are thrown away constantly. The canonical order therefore lives in the TAB view-model
///         (a plain ordered key list) and the buttons call into it; a rebuild mid-interaction then re-derives
///         the tray instead of corrupting it. The alternative — order held on the rebuilt group VMs — loses
///         the user's arrangement the moment they touch a padding field.
///     </para>
/// </summary>
public interface IClipTrayHost
{
    /// <summary>Moves one (player · demo) group by <paramref name="delta" /> positions (−1 = up).</summary>
    /// <param name="groupKey">The group's stable key (see <see cref="ClipTrayKeys.Group" />).</param>
    /// <param name="delta">Signed position delta; clamped to the tray bounds.</param>
    void MoveGroup(string groupKey, int delta);

    /// <summary>Moves one group to an absolute tray position (the drag-and-drop path).</summary>
    /// <param name="groupKey">The group's stable key (see <see cref="ClipTrayKeys.Group" />).</param>
    /// <param name="targetIndex">Zero-based destination; clamped to the tray bounds.</param>
    void MoveGroupTo(string groupKey, int targetIndex);

    /// <summary>Un-stages every clip belonging to one (player · demo) group.</summary>
    /// <param name="groupKey">The group's stable key (see <see cref="ClipTrayKeys.Group" />).</param>
    void RemoveGroup(string groupKey);

    /// <summary>Un-stages one highlight.</summary>
    /// <param name="key">The staged highlight's identity.</param>
    void RemoveClip(HighlightKey key);
}

/// <summary>Key helpers shared by the tray and the plan builder so the two can never disagree.</summary>
public static class ClipTrayKeys
{
    /// <summary>
    ///     The (player · demo) group key. The path half is upper-cased for the SAME reason
    ///     <c>ClipWindows.Coalesce</c> does it: a casing-variant path must not split one demo's clips into two
    ///     tray groups while the coalescer merges them as one.
    /// </summary>
    /// <param name="demoPath">Demo file path.</param>
    /// <param name="steamId64">Attributed player's steamId64 (may be empty).</param>
    public static string Group(string demoPath, string? steamId64) =>
        // U+001F (unit separator) rather than '|' or ':' — both are legal in a POSIX path, so a demo named
        // "a|b.dem" would otherwise collide with a different (path, steamId) pair.
        (demoPath ?? "").ToUpperInvariant() + '\u001f' + (steamId64 ?? "");
}

/// <summary>
///     A multi-select highlight-type filter chip, keyed by the qualified <c>{rulesetId}.{highlightId}</c>
///     (includes historical ids still present in cached rows). None selected = all types.
///     <para>
///         <b>Re-homed, not orphaned.</b> The four filters were discovery affordances over the library-wide
///         card grid; the redesign re-pointed them at <see cref="AddClipsPickerViewModel" />'s highlight-row
///         list, which reuses these item types verbatim rather than re-deriving the counting rules.
///     </para>
/// </summary>
/// <param name="typeKey">The qualified type key (<c>CachedHighlightEvent.TypeKey</c>).</param>
/// <param name="display">Friendly label shown on the chip.</param>
/// <param name="count">How many highlights of this type exist across the library.</param>
public partial class HighlightTypeFilterItem(string typeKey, string display, int count) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The qualified type key (<c>CachedHighlightEvent.TypeKey</c>).</summary>
    public string TypeKey { get; } = typeKey;

    /// <summary>Friendly label shown on the chip.</summary>
    public string Display { get; } = display;

    /// <summary>How many highlights of this type exist across the library.</summary>
    public int Count { get; } = count;

    /// <summary>Chip label with count, e.g. "ace (4)".</summary>
    public string Label => $"{Display} ({Count})";
}

/// <summary>
///     A multi-select highlight-KIND filter chip (editorial track — skill / funny / lowlight), so a user can
///     pull the comedic and lowlight firings out of the main skill reel. None selected = all kinds. Mirrors
///     <see cref="HighlightTypeFilterItem" />, keyed by the <see cref="HighlightKind" /> enum rather than a
///     string.
/// </summary>
/// <param name="kind">The editorial track this chip selects.</param>
/// <param name="display">Friendly label shown on the chip (Highlight / Funny / Lowlight).</param>
/// <param name="count">How many highlights of this kind exist across the library.</param>
public partial class HighlightKindFilterItem(HighlightKind kind, string display, int count) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The editorial track this chip selects.</summary>
    public HighlightKind Kind { get; } = kind;

    /// <summary>Friendly label shown on the chip.</summary>
    public string Display { get; } = display;

    /// <summary>How many highlights of this kind exist across the library.</summary>
    public int Count { get; } = count;

    /// <summary>Chip label with count, e.g. "Funny (4)".</summary>
    public string Label => $"{Display} ({Count})";
}

/// <summary>
///     A multi-select player filter item, keyed by steamId64, with a sanitized display
///     name and a highlight count. None selected = all players. Parked for the Add-clips picker — see
///     <see cref="HighlightTypeFilterItem" />.
/// </summary>
/// <param name="steamId64">steamId64 (the stable aggregation key; falls back to the raw name when empty).</param>
/// <param name="display">Sanitized player name.</param>
/// <param name="count">How many highlights this player has across the library.</param>
public partial class PlayerFilterItem(string steamId64, string display, int count) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>steamId64 (the stable aggregation key; falls back to the raw name when empty).</summary>
    public string SteamId64 { get; } = steamId64;

    /// <summary>Sanitized player name.</summary>
    public string Display { get; } = display;

    /// <summary>How many highlights this player has across the library.</summary>
    public int Count { get; } = count;

    /// <summary>Row label with count, e.g. "s1mple (6)".</summary>
    public string Label => $"{Display} ({Count})";
}

/// <summary>
///     One staged clip in the persisted tray snapshot. A PLAIN DTO, not
///     <see cref="HighlightKey" />: the record struct's positional shape is a serialization contract nobody
///     agreed to, and a delimited string would silently corrupt on a path containing the delimiter.
/// </summary>
public sealed class StagedClipState
{
    /// <summary>Owning demo path.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Ruleset that emitted the highlight.</summary>
    public string RulesetId { get; set; } = "";

    /// <summary>Highlight id inside that ruleset.</summary>
    public string HighlightId { get; set; } = "";

    /// <summary>Firing tick (frame clock).</summary>
    public int Tick { get; set; }

    /// <summary>Attributed player slot.</summary>
    public int PlayerSlot { get; set; }
}

/// <summary>
///     The cross-session snapshot blob: the ordered clip tray plus the
///     splitter ratios. Plain, binder-safe fields so it round-trips through the session store.
///     <para>
///         <b>Inert until the shell persists it.</b> <c>IWorkspaceTabViewModel.SnapshotState()</c> has a
///         default returning null and NO call site outside tests — module tab state is not written to disk
///         today. The tray therefore survives tab switches (it lives in the retained VM) but NOT an app
///         restart until the shell calls this — a shell obligation.
///     </para>
/// </summary>
public sealed class HighlightsSessionState
{
    /// <summary>The staged clips, IN TRAY ORDER. Vanished keys are dropped on restore.</summary>
    public StagedClipState[] StagedClips { get; set; } = [];

    /// <summary>Tray column star weight (splitter position); paired with <see cref="ConfigColumnStars" />.</summary>
    public double TrayColumnStars { get; set; } = 1.4;

    /// <summary>Config-pane column star weight (splitter position).</summary>
    public double ConfigColumnStars { get; set; } = 1.0;
}
