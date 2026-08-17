namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     The dot's semantic bucket for the 2D Playback in-context CS2 indicator (csvg-integration
///     ux-design.md). It maps onto the walled-off <c>Pb2d*</c> HUD palette in the view (NOT the
///     app-chrome ramp — design-system D21), so this abstraction stays free of any brush/token.
/// </summary>
public enum LiveSyncHudDot
{
    /// <summary>
    ///     No indicator — the session is Disconnected (the indicator is hidden anyway via
    ///     <see cref="ILiveSyncHudState.IsActive" />).
    /// </summary>
    None,

    /// <summary>Bringing a session up (connecting / loading) — a neutral HUD-bright dot; pulses.</summary>
    Working,

    /// <summary>Synced / Following / Holding — <c>Pb2dPositive</c> green. Following pulses; inferred pause is hollow.</summary>
    Good,

    /// <summary>Genuinely uncertain (seek unconfirmed / demo changed) — the HUD caution-equivalent (<c>Pb2dTeamT</c>).</summary>
    Degraded,

    /// <summary>Session lost / failed — the HUD red (<c>Pb2dHeadshot</c>).</summary>
    Error
}

/// <summary>
///     A read-only projection of the live-sync engine state, shaped for the 2D Playback tab's in-context
///     CS2 indicator. It is DELIBERATELY decoupled from the App-layer
///     <c>Services.LiveSync</c> contract (which is WASM-poison and App-only) so the module abstraction stays
///     engine-free: the shell adapts the engine state onto this and pushes it in through
///     <see cref="IModuleContext.LiveSyncHud" />.
///     <para>
///         The <em>word</em> in <see cref="Label" /> is the accessible carrier of state; the dot is a
///         redundant colour cue (WCAG 1.4.1). <see cref="IsActive" /> already folds in the
///         <c>chrome.livesync</c> gate AND the "session state ≠ Off/Disconnected" rule, so the view's
///         visibility is a single bound flag.
///     </para>
/// </summary>
public interface ILiveSyncHudState
{
    /// <summary>
    ///     Whether the 2D indicator should be shown at all — true only while the <c>chrome.livesync</c>
    ///     gate is on AND the engine is in a non-Disconnected state. The 2D tab being active is
    ///     handled separately (the module only reads this while activated).
    /// </summary>
    bool IsActive { get; }

    /// <summary>The dot's semantic bucket (drives the walled-off <c>Ellipse.pb2dDot.*</c> class selector).</summary>
    LiveSyncHudDot Dot { get; }

    /// <summary>True while the dot runs the subtle opacity pulse (Following / working states).</summary>
    bool IsPulsing { get; }

    /// <summary>True to render the dot as a hollow ring — the inferred-pause "(inferred)" treatment.</summary>
    bool IsHollow { get; }

    /// <summary>The compact chip text, e.g. <c>"CS2 · Following"</c> — the accessible carrier of state.</summary>
    string Label { get; }

    /// <summary>Raised on the UI thread whenever any of the above changes (engine transition or gate flip).</summary>
    event EventHandler? Changed;
}
