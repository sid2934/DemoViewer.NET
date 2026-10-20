#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview;

/// <summary>
///     The rule every "Open player overview" entry point follows: a player opens by a real SteamID64 only. A bot, a
///     coach or a player the demo names without a SteamID offers no action and says why.
/// </summary>
public static class PlayerOverviewLink
{
    /// <summary>The entry's label.</summary>
    public const string Label = "Open player overview";

    /// <summary>The tooltip on an entry that offers no action.</summary>
    public const string NoSteamIdTip = "this demo carries no SteamID for this player";

    /// <summary>The account the player opens as, or 0 when the entry offers no action.</summary>
    /// <param name="steamId64">The SteamID64 the demo carries for the player.</param>
    /// <param name="isBot">True for a bot.</param>
    /// <param name="isCoach">True for a coach.</param>
    public static ulong Of(ulong steamId64, bool isBot, bool isCoach) => isBot || isCoach ? 0 : steamId64;

    /// <summary>As <see cref="Of(ulong, bool, bool)" /> over a decimal SteamID64; an unparsable one is 0.</summary>
    /// <param name="steamId64">The SteamID64 as text.</param>
    /// <param name="isBot">True for a bot.</param>
    /// <param name="isCoach">True for a coach.</param>
    public static ulong Of(string? steamId64, bool isBot, bool isCoach) =>
        ulong.TryParse(steamId64, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? Of(id, isBot, isCoach) : 0;
}
