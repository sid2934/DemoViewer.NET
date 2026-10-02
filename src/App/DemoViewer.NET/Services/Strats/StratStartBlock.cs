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
    public static StratStart? Effective(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Start ?? Legacy(document, out _);
    }

    /// <summary>
    ///     The start an older file implies, or null. Only for a file with no start block, no origin and no mined tag whose
    ///     first step is at the strat's start: that step's entries (a carried copy excluded) are the start. A token it
    ///     does not place starts at its first entry on a later step when that entry is a carried copy (marked, or in a
    ///     file from before the mark an unmarked entry on a step that does not name it), since a copy of a token nothing
    ///     placed before can only be of a round-start entry since removed.
    /// </summary>
    /// <param name="document">The strat.</param>
    /// <param name="sources">Where each entry was read from, in the start's order.</param>
    public static StratStart? Legacy(StratDocument document, out IReadOnlyList<StartSource> sources)
    {
        ArgumentNullException.ThrowIfNull(document);
        sources = [];
        if (document.Start is not null || document.Origin is not null || document.Tags.Contains(MinedStratBuilder.Tag, StringComparer.Ordinal)
            || document.Steps.Count == 0 || StratClock.StratTickOf(document.Clock, document.Steps[0].AtSeconds) != 0)
        {
            return null;
        }

        bool unmarkedCopies = !document.Steps.Any(s => s.Positions.Any(p => p.Carried is not null));
        Dictionary<string, StartSource> found = new(StringComparer.Ordinal);
        foreach (string slot in Tokens)
        {
            if (LastFor(document.Steps[0], slot) is { } own)
            {
                if (document.Steps[0].Positions[own].Carried != true)
                {
                    found[slot] = new StartSource(0, own);
                }

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

        if (found.Count == 0)
        {
            return null;
        }

        List<StartSource> ordered = [.. Tokens.Where(found.ContainsKey).Select(t => found[t])];
        sources = ordered;
        return new StratStart
        {
            Kind = StratStart.SpawnKind,
            Positions = [.. ordered.Select(s => FromPosition(document.Steps[s.Step].Positions[s.Position]))]
        };
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
    public static List<PatchOp> Ops(StratDocument document, IReadOnlyDictionary<string, StartPosition?> changes,
        IReadOnlyCollection<StartSource>? legacyCarried = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(changes);
        StratStart current = Clone(Effective(document) ?? new StratStart());
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
