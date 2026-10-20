#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.MatchMeta;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>One name an account played under, with when it was first and last seen and in how many demos.</summary>
/// <param name="Name">The RAW name, as the roster carries it: sanitise at the render boundary.</param>
/// <param name="FirstSeenTicks">The match time of the oldest demo with the name, in <see cref="PlayerMatch.TimeTicks" />'s ticks.</param>
/// <param name="LastSeenTicks">The match time of the newest demo with the name.</param>
/// <param name="Demos">How many demos carry the name.</param>
public sealed record PlayerAlias(string Name, long FirstSeenTicks, long LastSeenTicks, int Demos);

/// <summary>One account across the library: every name it played under and how many demos hold it.</summary>
/// <param name="SteamId64">The account.</param>
/// <param name="Aliases">Every name, the most recently seen first.</param>
/// <param name="DemoCount">How many demos hold the account, a demo with two copies once.</param>
/// <param name="FirstSeenTicks">The match time of its oldest demo.</param>
/// <param name="LastSeenTicks">The match time of its newest demo.</param>
public sealed record PlayerEntry(ulong SteamId64, IReadOnlyList<PlayerAlias> Aliases, int DemoCount, long FirstSeenTicks, long LastSeenTicks)
{
    /// <summary>The most recently seen name, or empty when no demo named the account.</summary>
    public string Name => Aliases.Count > 0 ? Aliases[0].Name : "";
}

/// <summary>One demo an account is in.</summary>
/// <param name="Row">The demo's index row, seen from its primary path.</param>
/// <param name="Side">The side the account ended the demo on, 2 = T, 3 = CT; null when it is in no side list.</param>
/// <param name="Names">The account's RAW names in the demo, distinct.</param>
/// <param name="Slots">Every slot the account held, ascending.</param>
/// <param name="LeftEarly">
///     True when only the <c>player_match</c> facet has the account: it ended the demo as a spectator or on an
///     abandoned slot.
/// </param>
public sealed record PlayerMatch(DemoCacheIndexEntry Row, int? Side, IReadOnlyList<string> Names, IReadOnlyList<int> Slots, bool LeftEarly)
{
    /// <summary>What history sorts by, as UTC ticks: the match time, else the file's write time (<see cref="NewestMatchFirst.UtcTicksOf" />).</summary>
    public long TimeTicks => NewestMatchFirst.UtcTicksOf(Row);
}

/// <summary>Which of an account's demos <see cref="PlayerDirectory.Matches" /> returns. Every member left null passes all.</summary>
public sealed record PlayerFilter
{
    public static readonly PlayerFilter All = new();

    /// <summary>The map, compared ignoring case.</summary>
    public string? Map { get; init; }

    /// <summary>Demos at or after this, in <see cref="PlayerMatch.TimeTicks" />'s ticks.</summary>
    public long? FromTicks { get; init; }

    /// <summary>Demos before this, in <see cref="PlayerMatch.TimeTicks" />'s ticks.</summary>
    public long? BeforeTicks { get; init; }

    /// <summary>The side the account ended on, 2 = T, 3 = CT. A demo it left early has none and never passes.</summary>
    public int? Side { get; init; }

    /// <summary>Where the demo came from: the row's <c>MatchSource</c>, else its <c>SourceKind</c>, compared ignoring case.</summary>
    public string? Source { get; init; }

    /// <summary>The game mode ("premier", "competitive", ...), compared ignoring case.</summary>
    public string? GameMode { get; init; }

    /// <summary>Only the newest this many of the demos that pass the rest.</summary>
    public int? Last { get; init; }
}

/// <summary>The key the player caches hold a demo under.</summary>
public static class PlayerContentIds
{
    /// <summary>What a demo is across copies and renames: its content hash, or its one path before it is hashed.</summary>
    /// <param name="row">The demo's index row; null is the empty id.</param>
    public static string ContentId(this DemoCacheIndexEntry? row) =>
        string.IsNullOrEmpty(row?.Sha256) ? row?.Path ?? "" : row.Sha256;
}
