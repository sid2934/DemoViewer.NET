using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoViewer.NET.Models;

/// <summary>
///     Per-tab UI session snapshot. Restored on the next launch when the same
///     demo is reopened. Kept deliberately small + value-typed for clean JSON round-tripping.
///     <para>
///         The design doc sketched <c>ExpandedCardIds</c> / <c>SelectedCardId</c>, but the real
///         <see cref="ViewModels.HarvestCardViewModel" /> has no stable id and its cards are rebuilt
///         per frame selection, so card-level expansion is intrinsically tied to the live tree and
///         not durable across restarts. The durable, re-resolvable bits are persisted instead:
///         the frame index, the selected field-node path, and the active hex pane.
///     </para>
/// </summary>
public sealed record TabSessionState(
    int? SelectedFrameIndex,
    string? SelectedNodePath,
    bool ShowRawHex);

/// <summary>
///     Whole-session snapshot persisted to <c>%AppData%/DemoViewer.NET/session.json</c>.
///     Per-tab states are nullable so a tab that was never populated round-trips as <c>null</c>.
///     <para>
///         <b>Active-tab persistence is NAME-BASED, full stop.</b> <see cref="ActiveTabId" />, a stable
///         <c>WorkspaceTabDescriptor.TabId</c> string, is the only key. There is deliberately no index
///         fallback: the tab set is DYNAMIC (feature gating adds and removes tabs, and new built-ins land
///         mid-strip), so a positional index silently means a different tab from one build to the next. It
///         did exactly that when the Match Overview tab was inserted at position 1. A stale or gated-out
///         id falls back to the first tab, which is Library.
///     </para>
///     <para>
///         An older <c>session.json</c> that predates <c>ActiveTabId</c> deserializes it as <c>null</c>
///         (STJ fills a missing constructor arg with <c>default</c>) and simply lands on Library: a
///         one-time, self-healing loss of a remembered tab, which is the accepted cost of never restoring
///         the WRONG tab.
///     </para>
/// </summary>
/// <param name="Parser">Parser tab state, or null when it was never populated.</param>
/// <param name="Entity">Entity Tracking tab state.</param>
/// <param name="Analysis">Analysis tab state.</param>
/// <param name="DebuggerVisible">Shell flag: the graph debugger panel.</param>
/// <param name="OutputVisible">Shell flag: the output pane.</param>
/// <param name="ActiveTabId">The selected tab's stable <c>TabId</c>. See the remarks above.</param>
/// <param name="ModuleTabs">
///     State for MODULE-contributed tabs, keyed by <c>TabId</c>, the same stable, name-based key
///     <paramref name="ActiveTabId" /> uses, and for the same reason: the tab set is dynamic, so anything
///     positional silently means a different tab from one build to the next.
///     <para>
///         Held as raw <c>JsonElement</c> rather than a typed member because the shell cannot know a
///         module's shape: that is what makes it extensible. Each tab VM deserializes its own blob in
///         <c>RestoreState</c>. A key whose tab no longer exists (module removed, feature gated off) is
///         simply never handed to anyone.
///     </para>
/// </param>
/// <param name="Window">
///     Main-window geometry (v0.6.0). Nullable trailing param like <paramref name="ActiveTabId" />, so
///     pre-0.6.0 files bind <c>null</c> and the window simply opens at the platform default once.
/// </param>
/// <param name="Packs">
///     Per-pack session state, keyed by pack id (e.g. <c>"net.demoviewer.pack.stratbook"</c>), opaque to
///     the shell. A pack that has never been enabled this session is never asked for its state, so its
///     entry here is carried through unread and unwritten. A pack that HAS been enabled (at startup or by
///     a live toggle) is restored into once and its live value is trusted from then on, even after the
///     pack goes off again, so a value set while it was on is never lost to a later disable.
/// </param>
/// <param name="Hubs">
///     The host's own state of each hub tab, keyed by hub id. An entry for a hub no extension contributes
///     this session is carried through unread.
/// </param>
public sealed record SessionPayload(
    TabSessionState? Parser,
    TabSessionState? Entity,
    TabSessionState? Analysis,
    bool DebuggerVisible,
    bool OutputVisible,
    string? ActiveTabId = null,
    Dictionary<string, JsonElement>? ModuleTabs = null,
    WindowBoundsState? Window = null,
    Dictionary<string, JsonElement>? Packs = null,
    Dictionary<string, HubSessionState>? Hubs = null) : IJsonOnDeserialized
{
    // Redeclares the positional property with a setter: OnDeserialized below needs to fold into it, which
    // an init-only property (the compiler's default for a positional parameter) does not allow. Private:
    // nothing outside this type writes Packs after construction.
    public Dictionary<string, JsonElement>? Packs { get; private set; } = Packs;

    /// <summary>The host's state of each hub tab. Settable for the same reason as <see cref="Packs" />.</summary>
    public Dictionary<string, HubSessionState>? Hubs { get; private set; } = Hubs;

    /// <summary>A pre-<see cref="Packs" /> file's top-level members, held only until <c>OnDeserialized</c> folds them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownMembers { get; set; }

    // The Strat Book pack's id (StratBookPack.PackId), literal because Models must not depend on
    // Services or the pack, and because this is the shape the PACK wrote, not a core concept.
    private const string LegacyStratBookPackId = "net.demoviewer.pack.stratbook";

    // The Strat Book hub's id. Its rail state used to live in the pack's blob as "RailCollapsed".
    private const string LegacyStratBookHubId = "stratbook.hub";

    // A file written before Packs existed carried the Strat Book pack's layout flat as "StratBook". Folded
    // once, keyed under LegacyStratBookPackId; an existing Packs entry for that id wins.
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (UnknownMembers is { Count: > 0 } members
            && members.TryGetValue("StratBook", out JsonElement legacy)
            && legacy.ValueKind == JsonValueKind.Object)
        {
            Packs ??= new(StringComparer.Ordinal);
            if (!Packs.ContainsKey(LegacyStratBookPackId))
            {
                Packs[LegacyStratBookPackId] = legacy.Clone();
            }
        }

        UnknownMembers = null;

        if (Packs is not null
            && Packs.TryGetValue(LegacyStratBookPackId, out JsonElement pack)
            && pack.ValueKind == JsonValueKind.Object
            && pack.TryGetProperty("RailCollapsed", out JsonElement rail)
            && rail.ValueKind is JsonValueKind.True or JsonValueKind.False
            && Hubs?.ContainsKey(LegacyStratBookHubId) != true)
        {
            Hubs ??= new(StringComparer.Ordinal);
            Hubs[LegacyStratBookHubId] = new HubSessionState(rail.GetBoolean());
        }
    }
}

/// <summary>
///     Persisted main-window geometry. <see cref="Width" />/<see cref="Height" /> are DIPs (Avalonia
///     window sizes); <see cref="X" />/<see cref="Y" /> are PHYSICAL pixels (Avalonia
///     <c>PixelPoint.Position</c>): the two unit systems must never be mixed at restore.
///     <para>
///         Always the last-NORMAL bounds: while the window is maximized the tracker keeps the bounds it
///         had before maximizing (so un-maximizing after a restart returns to the right size), and
///         <see cref="Maximized" /> re-applies the maximized state separately. A minimized window is
///         never captured: restoring into the taskbar reads as a broken launch.
///     </para>
/// </summary>
/// <param name="Width">Client width in DIPs (last Normal state).</param>
/// <param name="Height">Client height in DIPs (last Normal state).</param>
/// <param name="X">Window X in physical pixels, or null when never moved/tracked.</param>
/// <param name="Y">Window Y in physical pixels.</param>
/// <param name="Maximized">Whether the window was maximized at exit.</param>
public sealed record WindowBoundsState(
    double Width,
    double Height,
    int? X,
    int? Y,
    bool Maximized);

/// <summary>The host's state of one hub tab.</summary>
/// <param name="RailCollapsed">Whether the section rail was collapsed.</param>
public sealed record HubSessionState(bool RailCollapsed);
