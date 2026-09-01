#region

using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Models;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     The narrow outbound command surface the sync engine issues. Extracted from the
///     CSVG session/engine so the engine core stays CSVG-agnostic and unit tests fake a handful of
///     methods instead of the full session. The engine picks per-capability between the v1.0 unacked
///     seek (echo-ledger confirmation) and the v1.1 arrival-verified acked seek.
/// </summary>
public interface ISyncClient
{
    /// <param name="demoPath">Host path of the demo.</param>
    /// <param name="interactiveDemoUi">
    ///     Request the in-game demo UI (honored only by "user-demo-ui" plugins. Without it
    ///     the CS2→DV direction cannot exist).
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task LoadDemoAsync(string demoPath, bool interactiveDemoUi, CancellationToken cancellationToken);

    Task CloseDemoAsync(CancellationToken cancellationToken);

    Task ResumeDemoAsync(CancellationToken cancellationToken);

    Task PauseDemoAsync(CancellationToken cancellationToken);

    /// <summary>v1.0 fire-and-forget seek; confirmation comes from the tick stream (pre-echo ledger).</summary>
    Task SetDemoTickAsync(int tick, CancellationToken cancellationToken);

    /// <summary>
    ///     v1.1 acked seek ("seek-ack"): awaits the plugin's arrival-verified acknowledgement.
    ///     Returns true when the plugin confirmed arrival; false when it reported failure or the
    ///     ack deadline passed (the client owns the deadline: no engine-side timer needed).
    /// </summary>
    Task<bool> SetDemoTickAckedAsync(int tick, bool? pauseAfterSeek, CancellationToken cancellationToken);

    /// <summary>v1.1 timescale ("timescale-set"): mirrors DV's playback speed. Send-only: no engine readback.</summary>
    Task SetTimescaleAsync(float timescale, CancellationToken cancellationToken);

    /// <summary>
    ///     Spectate a player by exact in-demo name (v1.0 command; send-only: no readback until
    ///     "spectator-report" ships). SteamID64 targeting is the deferred A-P9 validation.
    /// </summary>
    Task SetSpectatorAsync(string playerName, CancellationToken cancellationToken);
}

/// <summary>
///     The production adapter over CSVG's video session. Demo loading stays on the session (it also
///     notifies the capture backend); every other command lives on <see cref="CsvgVideoSession.Engine" />,
///     the per-run <see cref="ICs2EngineSession" />.
/// </summary>
public sealed class CsvgSyncClientAdapter(CsvgVideoSession session) : ISyncClient
{
    public Task LoadDemoAsync(string demoPath, bool interactiveDemoUi, CancellationToken cancellationToken) =>
        session.LoadDemoAsync(demoPath, interactiveDemoUi, cancellationToken);

    public Task CloseDemoAsync(CancellationToken cancellationToken) =>
        session.Engine.CloseDemoAsync(cancellationToken);

    public Task ResumeDemoAsync(CancellationToken cancellationToken) =>
        session.Engine.ResumeDemoAsync(cancellationToken);

    public Task PauseDemoAsync(CancellationToken cancellationToken) =>
        session.Engine.PauseDemoAsync(cancellationToken);

    public Task SetDemoTickAsync(int tick, CancellationToken cancellationToken) =>
        session.Engine.SetDemoTickAsync(tick, cancellationToken);

    public async Task<bool> SetDemoTickAckedAsync(int tick, bool? pauseAfterSeek,
        CancellationToken cancellationToken)
    {
        SeekResult result = await session.Engine
            .SetDemoTickAsync(tick, pauseAfterSeek, true, cancellationToken)
            .ConfigureAwait(false);
        return result.Success;
    }

    public Task SetTimescaleAsync(float timescale, CancellationToken cancellationToken) =>
        session.Engine.SetDemoTimescaleAsync(timescale, cancellationToken);

    public Task SetSpectatorAsync(string playerName, CancellationToken cancellationToken) =>
        session.Engine.SetSpectatorTargetAsync(playerName, cancellationToken);
}
