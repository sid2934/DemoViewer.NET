namespace DemoViewer.NET.Playback2D.Core.Utility;

/// <summary>One landing group on the utility map: where a kind of grenade keeps going off.</summary>
/// <param name="Id">The caller's key for the group, handed back by a hit test.</param>
/// <param name="X">Mean landing X of the throws shown.</param>
/// <param name="Y">Mean landing Y.</param>
/// <param name="Z">Mean landing Z: which floor the icon draws on.</param>
/// <param name="IconKey">The baked icon key, e.g. <c>equipment/smokegrenade</c>.</param>
/// <param name="Letter">Drawn when no icon source is wired or the key has no art.</param>
/// <param name="Throws">Throws the group holds after the caller's filters; the icon grows a little with it.</param>
/// <param name="Focused">The group whose throw positions are shown.</param>
public readonly record struct UtilityLanding(string Id, float X, float Y, float Z, string IconKey, char Letter, int Throws, bool Focused);

/// <summary>One throw position of the focused group, with the flight of a throw from it.</summary>
/// <param name="Id">The caller's key for the position, handed back by a hit test.</param>
/// <param name="X">Mean origin X.</param>
/// <param name="Y">Mean origin Y.</param>
/// <param name="Z">Mean origin Z.</param>
/// <param name="Team">2 = T, 3 = CT, 0 when unread: the disc colour.</param>
/// <param name="JumpThrow">Drawn with an outer ring when the position is a jump-throw.</param>
/// <param name="Selected">The position whose details are open.</param>
/// <param name="Trajectory">A representative throw's flight, origin first, landing last.</param>
public sealed record UtilityThrow(string Id, float X, float Y, float Z, int Team, bool JumpThrow, bool Selected,
    IReadOnlyList<GrenadeTrailPoint> Trajectory);

/// <summary>
///     The Utility Book map's input: the landing groups of one map, and, when one is focused, its throw
///     positions and their flights. The App fills it from the grenade index; the layer draws it and the
///     host hit-tests against the same numbers.
///     <para>
///         Replaced whole, never edited in place. <see cref="Version" /> moves on every replacement, which
///         is what the compositor's picture cache keys on. Host-independent: no App type lives here.
///     </para>
/// </summary>
public sealed class UtilityMapDocument
{
    /// <summary>The landing groups, drawn in order, so the caller puts the biggest last to draw on top.</summary>
    public IReadOnlyList<UtilityLanding> Landings { get; private set; } = [];

    /// <summary>The focused group's throw positions, empty with no focus.</summary>
    public IReadOnlyList<UtilityThrow> Throws { get; private set; } = [];

    /// <summary>True while a group is focused: the other groups draw dimmed.</summary>
    public bool HasFocus { get; private set; }

    /// <summary>Bumped on every change.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after every change, on the caller's thread.</summary>
    public event Action? Changed;

    /// <summary>Replaces the content.</summary>
    /// <param name="landings">The landing groups.</param>
    /// <param name="throws">The focused group's throw positions.</param>
    public void Set(IReadOnlyList<UtilityLanding> landings, IReadOnlyList<UtilityThrow> throws)
    {
        ArgumentNullException.ThrowIfNull(landings);
        ArgumentNullException.ThrowIfNull(throws);
        Landings = landings;
        Throws = throws;
        HasFocus = landings.Any(l => l.Focused);
        Version++;
        Changed?.Invoke();
    }
}
