#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats.Mining;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>A start entry read from a step of an older file: the step and the entry's index in its <c>positions[]</c>.</summary>
/// <param name="Step">The step's index.</param>
/// <param name="Position">The entry's index.</param>
public readonly record struct StartSource(int Step, int Position);

/// <summary>
///     Reads and writes a strat's start (docs/strat-format.md, "The start"). A file written before the start block has
///     none; when it is a hand-made strat whose first step is at the strat's start, the start is read from it, and from
///     the copies Add step made of tokens that step no longer places. Nothing is written on open: the block is written
///     the first time the start is edited (<see cref="Ops" />), as one undo entry.
/// </summary>
public static class StratStartBlock
{
    /// <summary>Every token a start may place, in file order.</summary>
    public static IReadOnlyList<string> Tokens { get; } = [.. StratVocabulary.Slots, .. StratVocabulary.OpponentSlots];

    /// <summary>The strat's start: its block, else the one an older file implies; null for none.</summary>
    /// <param name="document">The strat.</param>
    /// <param name="spawns">The map's spawns, which give an older file's own five their spawn when nothing else does; null for none.</param>
    public static StratStart? Effective(StratDocument document, StratSpawns? spawns = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Start ?? Legacy(document, out _, spawns);
    }

    /// <summary>
    ///     The start an older file implies, or null. Only for a file with no start block, no origin and no mined tag whose
    ///     first step is at the strat's start: that step's entries (a carried copy excluded) are the start. When it places
    ///     someone, one of A to E it does not place starts at its first entry on a later step when that entry is a carried
    ///     copy (marked, or in a file from before the mark an unmarked entry on a step that does not name it), since a copy
    ///     of a token nothing placed before can only be of a round-start entry since removed. With the map's spawns, one
    ///     of A to E that still has no start starts at its spawn, as a new strat's would, so a first step at the start
    ///     walks from there.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="sources">Where each entry was read from, in the start's order; a spawn has none.</param>
    /// <param name="spawns">The map's spawns; null gives no token a spawn.</param>
    public static StratStart? Legacy(StratDocument document, out IReadOnlyList<StartSource> sources, StratSpawns? spawns = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        sources = [];
        if (document.Start is not null || document.Origin is not null || document.Tags.Contains(MinedStratBuilder.Tag, StringComparer.Ordinal))
        {
            return null;
        }

        bool seeded = document.Steps.Count > 0 && StratClock.StratTickOf(document.Clock, document.Steps[0].AtSeconds) == 0;
        bool unmarkedCopies = !document.Steps.Any(s => s.Positions.Any(p => p.Carried is not null));
        Dictionary<string, StartSource> found = new(StringComparer.Ordinal);
        foreach (string slot in seeded ? Tokens : [])
        {
            if (LastFor(document.Steps[0], slot) is { } own && document.Steps[0].Positions[own].Carried != true)
            {
                found[slot] = new StartSource(0, own);
            }
        }

        // Only a round-start step that still places someone is a seed, and only our five are recovered from copies:
        // an opponent is placed by hand, never sent anywhere, so one missing from the seed was taken out on purpose.
        foreach (string slot in found.Count == 0 ? [] : StratVocabulary.Slots.Where(s => !found.ContainsKey(s)).ToList())
        {
            if (LastFor(document.Steps[0], slot) is not null)
            {
                continue;
            }

            for (int k = 1; k < document.Steps.Count; k++)
            {
                if (LastFor(document.Steps[k], slot) is not { } entry)
                {
                    continue;
                }

                StepPosition position = document.Steps[k].Positions[entry];
                bool names = StratVocabulary.Slots.Contains(slot) && StratStepLines.Involves(document.Steps[k], slot);
                if (position.Carried == true || (unmarkedCopies && position.Carried is null && position.Observed != true && !names))
                {
                    found[slot] = new StartSource(k, entry);
                }

                break;
            }
        }

        // A strat that begins at the start gets our five at spawn where nothing else says, as New Strat seeded them; an
        // opponent missing from the seed was taken out by hand.
        Dictionary<string, StartPosition> spawned = new(StringComparer.Ordinal);
        StratStart? spawnStart = spawns?.StartFor(document);
        if (seeded && spawnStart is not null)
        {
            foreach (StartPosition spot in spawnStart.Positions.Where(p => StratVocabulary.Slots.Contains(p.Slot) && !found.ContainsKey(p.Slot)))
            {
                spawned[spot.Slot] = spot;
            }
        }

        if (found.Count == 0 && spawned.Count == 0)
        {
            return null;
        }

        List<StartSource> ordered = [.. Tokens.Where(found.ContainsKey).Select(t => found[t])];
        sources = ordered;
        List<StartPosition> positions =
        [
            .. Tokens.Select(t => found.TryGetValue(t, out StartSource source)
                    ? FromPosition(document.Steps[source.Step].Positions[source.Position])
                    : spawned.GetValueOrDefault(t))
                .OfType<StartPosition>()
        ];

        // Spawn only when every entry stands on the map's spawn spot for its token; anything else was moved by hand.
        bool atSpawns = spawnStart is not null && positions.All(p => For(spawnStart, p.Slot) is { X: { } sx, Y: { } sy }
                                                                     && Math.Abs(sx - (p.X ?? double.NaN)) <= 1 && Math.Abs(sy - (p.Y ?? double.NaN)) <= 1);
        return new StratStart { Kind = atSpawns ? StratStart.SpawnKind : StratStart.CustomKind, Positions = positions };
    }

    /// <summary>A step entry as a start entry: its point, level and yaw; the carried and observed marks are not kept.</summary>
    /// <param name="position">The entry.</param>
    public static StartPosition FromPosition(StepPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return new StartPosition
        {
            Slot = position.Slot, X = position.X, Y = position.Y, LevelMinZ = position.LevelMinZ, YawDegrees = position.YawDegrees
        };
    }

    /// <summary>The token's entry in a start, or null.</summary>
    /// <param name="start">The start, or null.</param>
    /// <param name="slot">The token.</param>
    public static StartPosition? For(StratStart? start, string slot) =>
        start?.Positions.LastOrDefault(p => string.Equals(p.Slot, slot, StringComparison.Ordinal));

    /// <summary>A start entry as a location, for the location field and the exports.</summary>
    /// <param name="position">The entry.</param>
    public static PlaceRef AsLocation(StartPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return new PlaceRef { Place = position.Place, X = position.X, Y = position.Y, LevelMinZ = position.LevelMinZ };
    }

    /// <summary>
    ///     The ops that set tokens' starts, one undo entry. A token mapped to null loses its start. In a file with no
    ///     block the implied start (<see cref="Legacy" />) is written first, the step entries it was read from removed so
    ///     they no longer stand in for it, and in a file from before the carried mark every unmarked copy the
    ///     projection reads as carried is marked, so writing the first mark changes nothing it plays. Any edit makes the
    ///     start <c>custom</c>. Empty when nothing changes.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="changes">Token to its new start, or null to clear it.</param>
    /// <param name="legacyCarried">
    ///     The step entries the projection reads as carried copies in a file from before the mark, as (step, entry); the
    ///     caller asks the projection. Ignored when the file has a block or marks.
    /// </param>
    /// <param name="spawns">As <see cref="Effective" />: the start the first write starts from.</param>
    public static List<PatchOp> Ops(StratDocument document, IReadOnlyDictionary<string, StartPosition?> changes,
        IReadOnlyCollection<StartSource>? legacyCarried = null, StratSpawns? spawns = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(changes);
        StratStart current = Clone(Effective(document, spawns) ?? new StratStart());
        List<StartPosition> next = [.. current.Positions];
        foreach ((string slot, StartPosition? value) in changes)
        {
            int at = next.FindLastIndex(p => string.Equals(p.Slot, slot, StringComparison.Ordinal));
            if (value is null)
            {
                if (at >= 0)
                {
                    next.RemoveAt(at);
                }

                continue;
            }

            StartPosition entry = Clone(value);
            entry.Slot = slot;
            if (at >= 0)
            {
                next[at] = entry;
            }
            else
            {
                next.Add(entry);
            }
        }

        next = [.. next.OrderBy(p => Order(p.Slot))];
        if (Same(next, current.Positions))
        {
            return [];
        }

        return Write(document, new StratStart { Kind = StratStart.CustomKind, Positions = next, Extra = current.Extra }, legacyCarried);
    }

    /// <summary>
    ///     The ops that make the start <paramref name="written" /> (its kind and entries), one undo entry; the first write
    ///     into an older file as <see cref="Ops" /> describes. Empty when it already is.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="written">The whole start.</param>
    /// <param name="legacyCarried">As <see cref="Ops" />.</param>
    public static List<PatchOp> Write(StratDocument document, StratStart written, IReadOnlyCollection<StartSource>? legacyCarried = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(written);
        List<StartPosition> next = written.Positions;
        if (document.Start is { } existing)
        {
            List<PatchOp> edit = [];
            if (!Same(next, existing.Positions))
            {
                edit.Add(PatchOp.ReplaceOp("/start/positions", Node(existing.Positions), Node(next)));
            }

            if (!string.Equals(existing.Kind, written.Kind, StringComparison.Ordinal))
            {
                edit.Add(PatchOp.ReplaceOp("/start/kind", JsonValue.Create(existing.Kind), JsonValue.Create(written.Kind)));
            }

            return edit;
        }

        return FirstWrite(document, written, legacyCarried);
    }

    // The block, then the step entries it replaces and the marks the legacy rule stood for.
    private static List<PatchOp> FirstWrite(StratDocument document, StratStart written, IReadOnlyCollection<StartSource>? legacyCarried)
    {
        Legacy(document, out IReadOnlyList<StartSource> sources);

        List<PatchOp> ops = [PatchOp.AddOp("/start", JsonSerializer.SerializeToNode(written, StratJsonContext.Default.StratStart))];
        bool unmarked = !document.Steps.Any(s => s.Positions.Any(p => p.Carried is not null));
        if (unmarked && legacyCarried is not null)
        {
            HashSet<StartSource> removed = [.. sources];
            foreach (StartSource copy in legacyCarried.Where(c => !removed.Contains(c)).OrderBy(c => c.Step).ThenBy(c => c.Position))
            {
                ops.Add(PatchOp.AddOp(Invariant($"/steps/{copy.Step}/positions/{copy.Position}/carried"), JsonValue.Create(true)));
            }
        }

        // Highest index first, so every pointer still names the entry it meant.
        foreach (StartSource source in sources.OrderByDescending(s => s.Step).ThenByDescending(s => s.Position))
        {
            StepPosition from = document.Steps[source.Step].Positions[source.Position];
            ops.Add(PatchOp.RemoveOp(Invariant($"/steps/{source.Step}/positions/{source.Position}"),
                JsonSerializer.SerializeToNode(from, StratJsonContext.Default.StepPosition)));
        }

        return ops;
    }

    /// <summary>The location field a token's start is edited through: the Start row's and a map pick's.</summary>
    /// <param name="slot">The token.</param>
    public static StratLocationField FieldFor(string slot) => new(Guid.Empty, slot, StratLocationKind.Start);

    /// <summary>A token's start as a location, or null when it has none.</summary>
    /// <param name="document">The strat.</param>
    /// <param name="slot">The token.</param>
    /// <param name="spawns">As <see cref="Effective" />.</param>
    public static PlaceRef? LocationOf(StratDocument document, string slot, StratSpawns? spawns = null) =>
        For(Effective(document, spawns), slot) is { } entry ? AsLocation(entry) : null;

    /// <summary>
    ///     The ops that set a token's start to a location (its facing kept), or clear it for null, one undo entry
    ///     through <see cref="Ops" />.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="slot">The token.</param>
    /// <param name="location">The new start, or null.</param>
    /// <param name="legacyCarried">As <see cref="Ops" />.</param>
    /// <param name="yawDegrees">A new facing; null keeps the stored one.</param>
    /// <param name="spawns">As <see cref="Effective" />.</param>
    public static List<PatchOp> WriteLocation(StratDocument document, string slot, PlaceRef? location,
        IReadOnlyCollection<StartSource>? legacyCarried = null, double? yawDegrees = null, StratSpawns? spawns = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        StartPosition? current = For(Effective(document, spawns), slot);

        // The same spot written back (a field losing focus, the same pick) is no edit: it would drop a capture's mark.
        if (current is not null && location is not null && StratLocations.IsSet(location)
            && string.Equals(current.Place, location.Place, StringComparison.Ordinal) && current.X == location.X && current.Y == location.Y
            && current.LevelMinZ == location.LevelMinZ && (yawDegrees is null || current.YawDegrees == yawDegrees))
        {
            return [];
        }

        StartPosition? next = location is null || !StratLocations.IsSet(location)
            ? null
            : new StartPosition
            {
                Slot = slot, Place = location.Place, X = location.X, Y = location.Y, LevelMinZ = location.LevelMinZ,
                YawDegrees = yawDegrees ?? current?.YawDegrees, Extra = current?.Extra
            };
        return Ops(document, new Dictionary<string, StartPosition?> { [slot] = next }, legacyCarried, spawns);
    }

    /// <summary>A copy, unknown fields included.</summary>
    public static StratStart Clone(StratStart start) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(start, StratJsonContext.Default.StratStart), StratJsonContext.Default.StratStart)!;

    private static StartPosition Clone(StartPosition position) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(position, StratJsonContext.Default.StartPosition),
            StratJsonContext.Default.StartPosition)!;

    private static JsonNode Node(List<StartPosition> positions) =>
        JsonSerializer.SerializeToNode(positions, StratJsonContext.Default.ListStartPosition)!;

    private static bool Same(List<StartPosition> a, List<StartPosition> b) =>
        Node(a).ToJsonString() == Node(b).ToJsonString();

    private static int Order(string slot)
    {
        for (int i = 0; i < Tokens.Count; i++)
        {
            if (string.Equals(Tokens[i], slot, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return Tokens.Count;
    }

    // The last finite entry for a slot wins, as the projection reads a step.
    private static int? LastFor(StratStep step, string slot)
    {
        int? found = null;
        for (int j = 0; j < step.Positions.Count; j++)
        {
            StepPosition p = step.Positions[j];
            if (string.Equals(p.Slot, slot, StringComparison.Ordinal) && double.IsFinite(p.X) && double.IsFinite(p.Y))
            {
                found = j;
            }
        }

        return found;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
///     The start in words, for the call sheet, the role sheets, LAN print, the history and the Start row:
///     <c>spawn</c>, <c>as captured</c>, or each player's place (<c>A Long Doors, B (1234, -561)</c>).
/// </summary>
public static class StratStartPhrasing
{
    /// <summary>
    ///     A strat's start as its readers print it: an older file's start that is not the map's spawns reads
    ///     <c>from step 1</c>, since it is that step's spots, not a choice anyone made in the Start row.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    /// <param name="spawns">The map's spawns, as <see cref="StratStartBlock.Effective" />.</param>
    public static string? Text(StratDocument document, CalloutResolver? callouts, StratSpawns? spawns = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        StratStart? start = StratStartBlock.Effective(document, spawns);
        return document.Start is null && start is { Kind: StratStart.CustomKind } ? FromStepOne : Text(start, callouts);
    }

    /// <summary>One token's start as its role sheet prints it; <see cref="Text(StratDocument, CalloutResolver, StratSpawns)" />'s rule.</summary>
    /// <param name="document">The strat.</param>
    /// <param name="slot">The token.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    public static string? SlotText(StratDocument document, string slot, CalloutResolver? callouts)
    {
        ArgumentNullException.ThrowIfNull(document);
        StratStart? start = StratStartBlock.Effective(document);
        return document.Start is null && start is { Kind: StratStart.CustomKind } && StratStartBlock.For(start, slot) is not null
            ? FromStepOne
            : SlotText(start, slot, callouts);
    }

    private const string FromStepOne = "from step 1";

    /// <summary>The whole start, or null when it places no one.</summary>
    /// <param name="start">The start, or null.</param>
    /// <param name="callouts">The owner's words for the map's places; null splits canonical names.</param>
    public static string? Text(StratStart? start, CalloutResolver? callouts)
    {
        if (start is not { Positions.Count: > 0 })
        {
            return null;
        }

        switch (start.Kind)
        {
            case StratStart.SpawnKind:
                return "spawn";
            case StratStart.CapturedKind:
                return "as captured";
        }

        List<string> players =
        [
            .. StratVocabulary.Slots.Select(slot => (slot, text: SlotText(start, slot, callouts)))
                .Where(p => p.text is not null)
                .Select(p => p.slot + " " + p.text)
        ];
        return players.Count > 0 ? string.Join(", ", players) : "opponents placed";
    }

    /// <summary>One token's start: <c>spawn</c> on a spawn start, else its place or point; null when it has none.</summary>
    /// <param name="start">The start, or null.</param>
    /// <param name="slot">The token.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    public static string? SlotText(StratStart? start, string slot, CalloutResolver? callouts)
    {
        if (StratStartBlock.For(start, slot) is not { } entry)
        {
            return null;
        }

        return string.Equals(start!.Kind, StratStart.SpawnKind, StringComparison.Ordinal)
            ? "spawn"
            : StratLocations.Text(StratStartBlock.AsLocation(entry), callouts) ?? "spawn";
    }
}
