namespace DemoViewer.NET.Services.LiveSync;

/// <summary>
///     The connected CSVG plugin's advertised capability set, projected onto the engine features
///     each token unlocks (the degradation matrix). A v1.0-era
///     plugin advertises nothing — every flag false — and the engine runs the fully-degraded
///     baseline (echo ledger, inference fallback, hidden demo UI, speed lock). CSVG token names
///     stay engine-side; this record is deliberately CSVG-type-free for the App/UI layer.
/// </summary>
/// <param name="DemoStateEvents">Engine-truth demo state events + queries (CS2→DV mirroring source).</param>
/// <param name="CommandAck">Per-command acks for command_id-carrying requests.</param>
/// <param name="SeekAck">Arrival-verified seek acknowledgements + pause-after-seek.</param>
/// <param name="TimescaleSet">Demo timescale command (speed mirroring DV→CS2).</param>
/// <param name="DemoIdentity">Loaded-demo path reporting (CS2-side demo-change detection).</param>
/// <param name="EnginePauseDetection">Pause state is engine truth, not command echo.</param>
/// <param name="LoadFailureDetection">Load failures emit real statuses instead of burning the timeout.</param>
/// <param name="SpectateBySteamId">Spectator targeting by SteamID64 with name fallback.</param>
/// <param name="UserDemoUi">The interactive in-game demo UI can be requested at load.</param>
public sealed record LiveSyncCapabilities(
    bool DemoStateEvents,
    bool CommandAck,
    bool SeekAck,
    bool TimescaleSet,
    bool DemoIdentity,
    bool EnginePauseDetection,
    bool LoadFailureDetection,
    bool SpectateBySteamId,
    bool UserDemoUi)
{
    /// <summary>The v1.0 baseline — nothing advertised, everything degraded.</summary>
    public static LiveSyncCapabilities None { get; } = new(
        false, false, false, false, false, false, false, false, false);

    /// <summary>
    ///     True when the plugin advertised nothing (v1.0-era build) — drives the flyout's
    ///     "plugin 1.0 — update CSVG for exact pause sync" note.
    /// </summary>
    public bool IsV10Baseline => this == None;
}
