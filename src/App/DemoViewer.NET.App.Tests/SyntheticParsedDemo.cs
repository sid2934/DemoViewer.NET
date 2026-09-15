#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Builds synthetic <see cref="ParsedDemo" /> fixtures for tests that need a demo-shaped object
///     without a demo file: library/queue/stats surfaces where the assertion is about our own
///     plumbing, not about parsing. The single place in this assembly that knows the
///     constructor's shape; defaults describe a minimal well-formed demo, so a caller sets only
///     what its assertion depends on.
/// </summary>
internal static class SyntheticParsedDemo
{
    internal static ParsedDemo Create(
        IReadOnlyList<DemoFrame>? frames = null,
        IReadOnlyList<GameEvent>? allGameEvents = null,
        IReadOnlyDictionary<int, PlayerInfo>? players = null,
        RuntimeSchema? schema = null,
        string mapName = "de_test",
        int tickCount = 6400,
        float tickInterval = 1f / 64,
        string serverName = "test",
        string clientName = "test",
        string gameDirectory = "csgo",
        int buildNumber = 0,
        int serverStartTick = 0,
        int patchVersion = 0,
        string demoVersionName = "valve_demo_2",
        string demoVersionGuid = "",
        string addons = "",
        // DemoProfile.Unknown is a static property, not a constant, so it cannot be a default
        // parameter value; null means "unknown" and is resolved below.
        DemoProfile? profile = null) =>
        new(
            frames ?? [],
            allGameEvents ?? [],
            players ?? new Dictionary<int, PlayerInfo>(),
            schema,
            mapName,
            tickCount,
            tickInterval,
            serverName,
            clientName,
            gameDirectory,
            buildNumber,
            serverStartTick,
            patchVersion,
            demoVersionName,
            demoVersionGuid,
            addons,
            profile ?? DemoProfile.Unknown);
}
