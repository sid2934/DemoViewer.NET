using System.Text.Json;

namespace DemoViewer.NET.Models;

/// <summary>
///     Per-tab UI session snapshot. Restored on the next launch when the same
///     demo is reopened. Kept deliberately small + value-typed for clean JSON round-tripping.
///     <para>
///         The design doc sketched <c>ExpandedCardIds</c> / <c>SelectedCardId</c>, but the real
///         <see cref="ViewModels.HarvestCardViewModel" /> has no stable id and its cards are rebuilt
///         per frame selection — so card-level expansion is intrinsically tied to the live tree and
///         not durable across restarts. We persist the durable, re-resolvable bits instead:
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
///         <b>Active-tab persistence is NAME-BASED, full stop.</b> <see cref="ActiveTabId" /> — a stable
///         <c>WorkspaceTabDescriptor.TabId</c> string — is the only key. There is deliberately no index
///         fallback: the tab set is DYNAMIC (feature gating adds and removes tabs, and new built-ins land
///         mid-strip), so a positional index silently means a different tab from one build to the next. It
///         did exactly that when the Match Overview tab was inserted at position 1. A stale or gated-out
///         id falls back to the first tab, which is Library.
///     </para>
///     <para>
///         An older <c>session.json</c> that predates <c>ActiveTabId</c> deserializes it as <c>null</c>
///         (STJ fills a missing constructor arg with <c>default</c>) and simply lands on Library — a
///         one-time, self-healing loss of a remembered tab, which is the accepted cost of never restoring
///         the WRONG tab.
///     </para>
/// </summary>
/// <param name="Parser">Parser tab state, or null when it was never populated.</param>
/// <param name="Entity">Entity Tracking tab state.</param>
/// <param name="Analysis">Analysis tab state.</param>
/// <param name="DebuggerVisible">Shell flag — the graph debugger panel.</param>
/// <param name="OutputVisible">Shell flag — the output pane.</param>
/// <param name="ActiveTabId">The selected tab's stable <c>TabId</c>. See the remarks above.</param>
/// <param name="ModuleTabs">
///     State for MODULE-contributed tabs, keyed by <c>TabId</c> — the same stable, name-based key
///     <paramref name="ActiveTabId" /> uses, and for the same reason: the tab set is dynamic, so anything
///     positional silently means a different tab from one build to the next.
///     <para>
///         Held as raw <c>JsonElement</c> rather than a typed member because the shell cannot know a
///         module's shape — that is what makes it extensible. Each tab VM deserializes its own blob in
///         <c>RestoreState</c>. A key whose tab no longer exists (module removed, feature gated off) is
///         simply never handed to anyone.
///     </para>
/// </param>
/// <param name="Window">
///     Main-window geometry (v0.6.0). Nullable trailing param like <paramref name="ActiveTabId" />, so
///     pre-0.6.0 files bind <c>null</c> and the window simply opens at the platform default once.
/// </param>
public sealed record SessionPayload(
    TabSessionState? Parser,
    TabSessionState? Entity,
    TabSessionState? Analysis,
    bool DebuggerVisible,
    bool OutputVisible,
    string? ActiveTabId = null,
    Dictionary<string, JsonElement>? ModuleTabs = null,
    WindowBoundsState? Window = null);

/// <summary>
///     Persisted main-window geometry. <see cref="Width" />/<see cref="Height" /> are DIPs (Avalonia
///     window sizes); <see cref="X" />/<see cref="Y" /> are PHYSICAL pixels (Avalonia
///     <c>PixelPoint.Position</c>) — the two unit systems must never be mixed at restore.
///     <para>
///         Always the last-NORMAL bounds: while the window is maximized the tracker keeps the bounds it
///         had before maximizing (so un-maximizing after a restart returns to the right size), and
///         <see cref="Maximized" /> re-applies the maximized state separately. A minimized window is
///         never captured — restoring into the taskbar reads as a broken launch.
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
