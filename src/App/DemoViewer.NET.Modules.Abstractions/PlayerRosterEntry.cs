namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     Stable per-player identity for the duration of the demo. Exposed via
///     <c>IModuleContext.Players</c>. IDENTITY ONLY — there is deliberately NO <c>Team</c> here,
///     because team is per-tick volatile state and lives on <see cref="IPlayerState.Team" />. A module
///     reads <see cref="IPlayerState" /> each push for markers and joins to this roster by
///     <see cref="Slot" /> for name / SteamID. This makes the two <c>Players</c> surfaces
///     non-redundant by construction.
/// </summary>
public sealed class PlayerRosterEntry
{
    /// <summary>Stable 0-based player slot.</summary>
    public int Slot { get; init; }

    /// <summary>Steam ID (for avatar lookup later).</summary>
    public ulong SteamId { get; init; }

    /// <summary>Display name.</summary>
    public string Name { get; init; } = "";
}
