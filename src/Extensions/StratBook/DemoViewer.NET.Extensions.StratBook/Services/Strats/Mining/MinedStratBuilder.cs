#region

using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;

/// <summary>
///     A mined pattern as a new strat: the medoid round's capture through <see cref="StratFromRound" />, then the
///     pattern's own name, type, site, economy, tempo and a note on where it was seen. <c>extra.mined</c> keeps
///     the pattern key and the member rounds. Not saved; the caller commits it as revision 1.
/// </summary>
public static class MinedStratBuilder
{
    /// <summary>The tag every mined strat carries, so the book can tell them apart.</summary>
    public const string Tag = "mined";

    /// <summary>The <see cref="StratDocument.Extra" /> key.</summary>
    public const string ExtraKey = "mined";

    /// <summary>An execute taken this soon after freeze end is fast (the Execute detector's rush line).</summary>
    public const double FastTakeSeconds = 25;

    /// <summary>An execute taken this late is slow.</summary>
    public const double SlowTakeSeconds = 60;

    /// <summary>Seconds of a setup a strat covers when first contact does not cut it short.</summary>
    public const int SetupSeconds = 35;

    /// <summary>Seconds past the take an execute strat runs when there was no plant.</summary>
    public const int ExecuteTailSeconds = 15;

    /// <summary>The last tick a pattern's strat captures from its medoid round.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="facts">The medoid round's Round Facts row.</param>
    public static int WindowEnd(MinedPattern pattern, RoundFacts facts)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(facts);
        RoundSignature m = pattern.Medoid;
        if (pattern.Kind == PatternKind.Setup)
        {
            int end = m.FreezeEndTick + SetupSeconds * m.TickRate;
            return facts.FirstContactTick is { } contact ? Math.Min(end, contact) : end;
        }

        return facts.PlantTick is { } plant ? plant : m.AnchorTick + ExecuteTailSeconds * m.TickRate;
    }

    /// <summary>The strat.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="capture">The medoid round's capture (<see cref="CachedRoundCapture" />).</param>
    /// <param name="facts">The medoid round's Round Facts row.</param>
    /// <param name="owner">The book it goes in.</param>
    /// <param name="fileName">The medoid demo's file name, for <c>origin</c>.</param>
    /// <param name="teamName">A team's display name, or null.</param>
    /// <param name="nowUtc">The creation time.</param>
    public static StratDocument Document(MinedPattern pattern, RoundCapture capture, RoundFacts facts, StratOwner owner,
        string fileName, Func<Guid, string?> teamName, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(teamName);

        int side = pattern.Side;
        IEnumerable<CapturedPawn> ours = capture.FreezeEnd.Pawns.Where(p => p.Team == side);
        IReadOnlyDictionary<char, ulong> slots = StratFromRound.SlotMap(ours, null, null);
        StratCaptureOptions options = new(side, StratFromRound.Tokens(capture.FreezeEnd.Pawns, side, slots),
            StratClock.RoundSecondsFor(facts.RoundTimeSeconds, null), true, StratFromRound.QuantizedLevel);
        StratOrigin origin = new() { DemoSha256 = pattern.Medoid.Sha256 ?? "", Round = pattern.Medoid.Round, FileName = fileName };

        StratDocument doc = StratFromRound.Document(capture, options, owner, pattern.Map, Name(pattern), origin, facts, nowUtc);
        doc.Type = TypeOf(pattern);
        doc.TargetSite = pattern.Kind == PatternKind.Execute ? pattern.Site : null;
        doc.Economy = Economy(pattern);
        doc.Tempo = Tempo(pattern);
        doc.Tags = [Tag];
        doc.Notes = Notes(pattern, teamName);
        doc.Extra ??= [];
        doc.Extra[ExtraKey] = JsonSerializer.SerializeToElement(new MinedExtra(
            pattern.Key,
            pattern.Support,
            Math.Round(pattern.Spread, 3),
            pattern.UtilityCompared,
            [.. pattern.Members.Select(m => new MinedExtraMember(m.Sha256, Path.GetFileName(m.DemoPath), m.Round, m.Won))]));
        return doc;
    }

    /// <summary><c>execute</c>, <c>default</c> for a T setup, <c>setup</c> for a CT one.</summary>
    /// <param name="pattern">The pattern.</param>
    public static string TypeOf(MinedPattern pattern) =>
        pattern.Kind == PatternKind.Execute ? "execute" : pattern.Side == 2 ? "default" : "setup";

    /// <summary>
    ///     What the pattern is called: the kind and site, then where its common smokes land, else where its players
    ///     stand most at the first anchor.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    public static string Name(MinedPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        string head = pattern.Kind == PatternKind.Execute ? $"{pattern.Site} execute"
            : pattern.Side == 2 ? "T default" : "CT setup";
        List<string> smokes =
        [
            .. pattern.CommonThrows
                .Where(c => c.Throw.Kind == Modules.UtilityBook.GrenadeKind.Smoke && c.Throw.LandingPlace is not null)
                .Select(c => c.Throw.LandingPlace!)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
        ];
        if (smokes.Count > 0)
        {
            return $"{head}: {string.Join(" and ", smokes)} smokes";
        }

        List<string> places =
        [
            .. (pattern.Medoid.Anchors.FirstOrDefault(a => a.Count > 0) ?? [])
                .Where(p => p.Place is not null)
                .GroupBy(p => p.Place!, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count() > 1 ? $"{g.Count()} {g.Key}" : g.Key)
                .Take(3)
        ];
        return places.Count > 0 ? $"{head}: {string.Join(", ", places)}" : head;
    }

    private static string? Economy(MinedPattern pattern) =>
        pattern.Members
            .GroupBy(m => m.Buy)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => (int)g.Key)
            .Select(g => g.Count() * 2 > pattern.Support ? g.Key.ToString().ToLowerInvariant() : "any")
            .FirstOrDefault();

    private static string? Tempo(MinedPattern pattern)
    {
        if (pattern.Kind != PatternKind.Execute)
        {
            return null;
        }

        double take = pattern.Members.Select(m => (m.AnchorTick - m.FreezeEndTick) / (double)Math.Max(1, m.TickRate)).Order()
            .ElementAt(pattern.Support / 2);
        return take <= FastTakeSeconds ? "fast" : take >= SlowTakeSeconds ? "slow" : "mid";
    }

    private static string Notes(MinedPattern pattern, Func<Guid, string?> teamName)
    {
        List<string> teams = [.. pattern.Teams.Select(teamName).OfType<string>().Order(StringComparer.Ordinal)];
        int known = pattern.Members.Count(m => m.Won is not null);
        string seen = string.Create(CultureInfo.InvariantCulture,
            $"Mined from {pattern.Support} rounds in {pattern.Demos} {(pattern.Demos == 1 ? "demo" : "demos")}");
        string by = teams.Count > 0 ? $", run by {string.Join(", ", teams)}" : "";
        string record = known > 0 ? $". Won {pattern.Wins} of {known}." : ".";
        string utility = pattern.UtilityCompared
            ? ""
            : " Utility not compared: some of these demos have no grenade rows, so the match is on positions and timing.";
        return seen + by + record + utility;
    }
}

/// <summary>What <c>extra.mined</c> holds.</summary>
public sealed record MinedExtra(string Key, int Support, double Spread, bool UtilityCompared, IReadOnlyList<MinedExtraMember> Members);

/// <summary>A member round in <c>extra.mined</c>.</summary>
public sealed record MinedExtraMember(string? Sha256, string FileName, int Round, bool? Won);
