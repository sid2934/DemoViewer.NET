#region

using System.Globalization;
using System.Text;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     One step or branch phrased into words, shared by <see cref="RoleSheet" /> and
///     <see cref="StratTextExporter" /> so the two surfaces read the same strat the same way
///     (strat-model.md §3.13, §3.14). Pure over the model; a resolver supplies place names.
/// </summary>
public static class StratStepPhrasing
{
    /// <summary>
    ///     A step as one sentence fragment: <c>B throws smoke A ramp → A site (Stairs)</c> (§3.13's own
    ///     example). <c>all</c> reads as <c>All</c>; the utility kind sits between the verb and the places;
    ///     a landing place prints in parens only when it differs from <c>to</c>; a lineup reference (Lineup
    ///     On A Strat Step) prints last, as <c>[lineup: &lt;title&gt;]</c>. The step's note is not included
    ///     here: the call sheet leaves prose out of its one-liner the way a history summary does, and the
    ///     role sheet appends it itself because §3.14 asks for it there.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="callouts">The owner's callouts for the map; null prints canonical names split into words.</param>
    /// <param name="lineupTitle">
    ///     Resolves a step's <c>utility.lineupId</c> to its card title (<c>GrenadeIndex.DescribeLineup</c>);
    ///     null, or an id it cannot resolve, prints the raw id, the same fallback
    ///     <see cref="StratBranchPhrasing.TargetText" /> uses for an unresolved branch target.
    /// </param>
    /// <param name="clock">The strat's clock, for a rotate time; null is the round clock.</param>
    public static string Phrase(StratStep step, CalloutResolver? callouts, Func<Guid, string?>? lineupTitle = null, StratClockInfo? clock = null)
    {
        ArgumentNullException.ThrowIfNull(step);

        // A step with lines is headed by who takes part; each line's place is phrased by PhraseLine.
        bool lines = StratStepLines.HasLines(step);
        string actor = lines ? ActorText(step.Assignments!) : step.Actor;
        bool plural = lines ? step.Assignments!.Count > 1 : string.Equals(step.Actor, StratVocabulary.ActorAll, StringComparison.Ordinal);
        StringBuilder sentence = new(string.Equals(actor, StratVocabulary.ActorAll, StringComparison.Ordinal) ? "All" : actor);

        // "all" is plural: the base verb ("All move"), not the third person a single slot takes ("B
        // throws"). The same split StratDiffPhrasing.StepSentence already makes for a diff line.
        sentence.Append(' ').Append(plural ? step.Verb : StratDiffPhrasing.ThirdPerson(step.Verb));

        if (step.Utility?.Kind is { } kind)
        {
            sentence.Append(' ').Append(kind);
        }

        string? from = StratLocations.Text(step.From, callouts);
        string? to = lines ? null : StratLocations.Text(step.To, callouts);
        if (from is not null || to is not null)
        {
            // With lines the places are the lines'; a bare place here would read as the destination.
            sentence.Append(lines ? " from " : " ");
            if (from is not null)
            {
                sentence.Append(from);
                if (to is not null)
                {
                    sentence.Append(" → ");
                }
            }

            if (to is not null)
            {
                sentence.Append(to);
            }
        }

        if (!lines && ViaText(step.Via, step.ViaPoints, callouts) is { } via)
        {
            sentence.Append(" via ").Append(via);
        }

        if (step.Lurk is { } lurk && LurkText(lurk, callouts, clock) is { } lurkText)
        {
            sentence.Append(StratLocations.LurkAreas(lurk).Count > 0 ? " " : "; ").Append(lurkText);
        }

        // A point prints in its own parentheses already.
        if (StratLocations.Text(step.Utility?.Landing, callouts) is { } landing && (lines || !string.Equals(landing, to, StringComparison.Ordinal)))
        {
            sentence.Append(string.IsNullOrEmpty(step.Utility!.Landing!.Place) ? " " + landing : " (" + landing + ")");
        }

        if (step.Utility?.LineupId is { } lineupId)
        {
            sentence.Append(" [lineup: ").Append(lineupTitle?.Invoke(lineupId) ?? lineupId.ToString()).Append(']');
        }

        return sentence.ToString();
    }

    /// <summary>
    ///     One line of a step: <c>B → Palace, watching A site, CT</c>, or <c>at</c> for a verb whose place is
    ///     where it stands (hold, peek, fake, plant, defuse).
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="verb">The step's verb.</param>
    /// <param name="callouts">The owner's callouts for the map; null prints canonical names split into words.</param>
    public static string PhraseLine(StepAssignment line, string verb, CalloutResolver? callouts)
    {
        ArgumentNullException.ThrowIfNull(line);
        StringBuilder text = new(line.Slot);
        if (StratLocations.Text(line.To, callouts) is { } to)
        {
            text.Append(StratStepFields.ToLabel(verb) == "to" ? " → " : " at ").Append(to);
        }

        if (ViaText(line.Via, line.ViaPoints, callouts) is { } via)
        {
            text.Append(" via ").Append(via);
        }

        if (Watching(line, callouts) is { } watching)
        {
            text.Append(", ").Append(watching);
        }

        return text.ToString();
    }

    /// <summary>What a travel goes through, in order: <c>Outside Long, Long Doors</c>; null with nothing.</summary>
    /// <param name="via">The <c>via</c> places.</param>
    /// <param name="points">The <c>viaPoints</c>.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    public static string? ViaText(IReadOnlyList<string>? via, IReadOnlyList<PlaceRef>? points, CalloutResolver? callouts)
    {
        IReadOnlyList<PlaceRef> entries = StratLocations.Via(via, points);
        return entries.Count == 0 ? null : string.Join(", ", entries.Select(e => StratLocations.Text(e, callouts)));
    }

    /// <summary>
    ///     A lurk's areas and rotate: <c>Palace, Connector; rotate to B site at 0:40 or on the call</c>. Just the rotate
    ///     without areas, just the areas without a rotate, null with neither.
    /// </summary>
    /// <param name="lurk">The step's lurk.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    /// <param name="clock">The strat's clock, for the rotate time; null is the round clock.</param>
    public static string? LurkText(StepLurk lurk, CalloutResolver? callouts, StratClockInfo? clock = null)
    {
        ArgumentNullException.ThrowIfNull(lurk);
        string areas = string.Join(", ", StratLocations.LurkAreas(lurk).Select(a => StratLocations.Text(a, callouts)));
        string? rotate = lurk.Rotate is { } r ? RotateText(r, callouts, clock) : null;
        return (areas.Length, rotate) switch
        {
            (0, null) => null,
            (0, _) => rotate,
            (_, null) => areas,
            _ => areas + "; " + rotate
        };
    }

    /// <summary><c>rotate to B site at 0:40 or on the call</c>; null for a rotate that says nothing.</summary>
    /// <param name="rotate">The rotate.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    /// <param name="clock">The strat's clock; null is the round clock.</param>
    public static string? RotateText(LurkRotate rotate, CalloutResolver? callouts, StratClockInfo? clock = null)
    {
        ArgumentNullException.ThrowIfNull(rotate);
        StringBuilder text = new("rotate");
        if (StratLocations.Text(rotate.To, callouts) is { } to)
        {
            text.Append(" to ").Append(to);
        }

        if (rotate.AtSeconds is { } at)
        {
            text.Append(" at ").Append(StratClock.Format(clock, at));
        }

        if (rotate.When is { Length: > 0 } when)
        {
            text.Append(rotate.AtSeconds is null ? " " : " or ").Append(when);
        }

        return text.Length == "rotate".Length ? null : text.ToString();
    }

    /// <summary><c>watching A site, CT</c>, or null when the line watches no place.</summary>
    /// <param name="line">A line, or null.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    public static string? Watching(StepAssignment? line, CalloutResolver? callouts)
    {
        IReadOnlyList<PlaceRef> watched = StratLocations.Watched(line?.Watch);
        return watched.Count == 0 ? null : "watching " + string.Join(", ", watched.Select(p => StratLocations.Text(p, callouts)));
    }

    /// <summary>
    ///     A step on one line of text, for a list with no room for sub-lines (the step track, the Detected
    ///     preview): <see cref="Phrase" />, then each line after a colon, separated by semicolons.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="callouts">Place names; null for canonical ones.</param>
    /// <param name="lineupTitle">Resolves a lineup id to its title.</param>
    /// <param name="clock">The strat's clock; null is the round clock.</param>
    public static string PhraseWithLines(StratStep step, CalloutResolver? callouts, Func<Guid, string?>? lineupTitle = null,
        StratClockInfo? clock = null)
    {
        string head = Phrase(step, callouts, lineupTitle, clock);
        return StratStepLines.HasLines(step)
            ? head + ": " + string.Join("; ", step.Assignments!.Select(l => PhraseLine(l, step.Verb, callouts)))
            : head;
    }

    // "All" when the lines name every slot, else the slots in line order.
    private static string ActorText(List<StepAssignment> lines) =>
        StratVocabulary.Slots.All(s => lines.Exists(l => string.Equals(l.Slot, s, StringComparison.Ordinal)))
            ? StratVocabulary.ActorAll
            : string.Join(", ", lines.Select(l => l.Slot));
}

/// <summary>Where a branch goes: shared text for the role sheet's and the call sheet's "if … → …" lines.</summary>
public static class StratBranchPhrasing
{
    /// <summary>
    ///     <c>&lt;target strat name&gt;</c>, or <c>&lt;target strat name&gt;, step &lt;n&gt;</c> when the
    ///     branch names a step. The target strat is looked up through <paramref name="lookup" /> unless it
    ///     is <paramref name="doc" /> itself (a step chain, <c>stratId == doc.Id</c>); an id
    ///     <paramref name="lookup" /> cannot resolve prints as its raw GUID rather than nothing.
    /// </summary>
    /// <param name="target">The branch's target.</param>
    /// <param name="doc">The strat the branch belongs to.</param>
    /// <param name="lookup">Resolves another strat by id; null when only this document is at hand.</param>
    public static string TargetText(BranchTarget target, StratDocument doc, Func<Guid, StratDocument?>? lookup)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(doc);
        StratDocument? resolved = target.StratId == doc.Id ? doc : lookup?.Invoke(target.StratId);
        string name = resolved?.Name is { Length: > 0 } n ? n : target.StratId.ToString();
        if (target.StepId is not { } stepId)
        {
            return name;
        }

        int index = resolved?.Steps.FindIndex(s => s.Id == stepId) ?? -1;
        string step = index >= 0 ? (index + 1).ToString(CultureInfo.InvariantCulture) : "?";
        return $"{name}, step {step}";
    }
}

/// <summary>The sheet's masthead: the strat's identity plus the one slot this sheet is for (§3.14).</summary>
/// <param name="StratName">The strat's name.</param>
/// <param name="Map">The parser's map spelling.</param>
/// <param name="Side"><c>T</c> or <c>CT</c>.</param>
/// <param name="Type">The strat type.</param>
/// <param name="TargetSite"><c>A</c>, <c>B</c> or null.</param>
/// <param name="Economy">The buy type, or null.</param>
/// <param name="Tempo">The tempo, or null.</param>
/// <param name="TriggerText">The trigger's human text, or null.</param>
/// <param name="Status">The lifecycle status.</param>
/// <param name="Revision">The strat's revision.</param>
/// <param name="Slot">The slot letter this sheet is for.</param>
/// <param name="SlotName">The resolved player name; null prints a blank line to write on (§3.14).</param>
/// <param name="Role">The slot's role, or null.</param>
/// <param name="StartText">Where the slot starts (<c>spawn</c>, or its place), or null when the strat gives it no start.</param>
/// <param name="Clock">The strat's clock, so a line's time prints as the strat counts it; null is the round clock.</param>
public sealed record RoleSheetHeader(
    string StratName,
    string Map,
    string Side,
    string Type,
    string? TargetSite,
    string? Economy,
    string? Tempo,
    string? TriggerText,
    string Status,
    int Revision,
    string Slot,
    string? SlotName,
    string? Role,
    string? StartText = null,
    StratClockInfo? Clock = null);

/// <summary>
///     One printed line: this slot's own step, or another slot's step kept as context (§3.14's dependency
///     rule). <see cref="IsContext" /> is what the sheet greys out.
/// </summary>
/// <param name="StepId">The step.</param>
/// <param name="AtSeconds">Round clock remaining, for ordering and display.</param>
/// <param name="Actor">
///     The step's own actor (a slot letter or <c>all</c>), not necessarily this sheet's slot; on a step with
///     lines, this slot for its own line.
/// </param>
/// <param name="Text">The phrased line, with this slot's own note appended when it has one.</param>
/// <param name="IsContext">True when this is another slot's step kept for context, not one of this slot's own.</param>
public sealed record RoleSheetLine(Guid StepId, double AtSeconds, string Actor, string Text, bool IsContext);

/// <summary>One branch line attached to one of this slot's own steps.</summary>
/// <param name="BranchId">The branch.</param>
/// <param name="AfterStepId">The step it follows.</param>
/// <param name="Text"><c>if &lt;condition&gt; → &lt;target&gt;[, step &lt;n&gt;]</c>.</param>
public sealed record RoleSheetBranchLine(Guid BranchId, Guid AfterStepId, string Text);

/// <summary>One point of the slot's mini-map polyline (§3.14), from a step's <see cref="StepPosition" />.</summary>
/// <param name="AtSeconds">The step's round-clock time.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="LevelMinZ">The level's quantized lower Z, never a floor index.</param>
/// <param name="YawDegrees">Facing, when the position carries one.</param>
public sealed record RoleSheetPoint(double AtSeconds, double X, double Y, double LevelMinZ, double? YawDegrees);

/// <summary>What Role View and LAN Print print for one slot of one strat: pure, no UI type in it.</summary>
/// <param name="Header">The masthead.</param>
/// <param name="Lines">Own and context lines, in step order.</param>
/// <param name="Branches">This slot's own branches.</param>
/// <param name="Positions">The mini-map polyline; empty until Step Authoring writes positions.</param>
public sealed record RoleSheet(
    RoleSheetHeader Header,
    IReadOnlyList<RoleSheetLine> Lines,
    IReadOnlyList<RoleSheetBranchLine> Branches,
    IReadOnlyList<RoleSheetPoint> Positions)
{
    /// <summary>
    ///     One slot's sheet over a strat (strat-model.md §3.14). Pure: player names, when wanted, are
    ///     resolved by the caller (Team Identity plus the book default, §3.5's three-level rule) and handed
    ///     in as <paramref name="roster" /> rather than looked up here.
    /// </summary>
    /// <param name="doc">The strat.</param>
    /// <param name="slot">The slot letter (<c>A</c> to <c>E</c>) to sheet.</param>
    /// <param name="callouts">The owner's callouts for the map; null prints canonical names split into words.</param>
    /// <param name="roster">Slot letter to resolved player name, already picked by strat pin, book default or neither.</param>
    /// <param name="lookup">Resolves another strat referenced by a branch target; null leaves it as a raw id.</param>
    /// <param name="lineupTitle">Resolves a step's lineup reference to its card title; null leaves it as a raw id (§3.13's phrasing).</param>
    public static RoleSheet Derive(StratDocument doc, string slot, CalloutResolver? callouts = null,
        IReadOnlyDictionary<string, string?>? roster = null, Func<Guid, StratDocument?>? lookup = null,
        Func<Guid, string?>? lineupTitle = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(slot);

        StratSlot? slotModel = doc.Slots.Find(s => string.Equals(s.Slot, slot, StringComparison.Ordinal));
        RoleSheetHeader header = new(doc.Name, doc.Map, doc.Side, doc.Type, doc.TargetSite, doc.Economy, doc.Tempo,
            doc.Trigger?.Text, doc.Status, doc.Revision, slot, roster?.GetValueOrDefault(slot), slotModel?.Role,
            StratStartPhrasing.SlotText(StratStartBlock.Effective(doc), slot, callouts), doc.Clock);

        HashSet<Guid> ownStepIds = [.. doc.Steps.Where(s => StratStepLines.Involves(s, slot)).Select(s => s.Id)];

        // Another slot's step earns a grey context line when it feeds one of this slot's own moves: its
        // `to` (a line's, on a step with lines) lands where this slot starts or ends, at the same or earlier
        // real time: a larger `atSeconds` on the round clock, which counts down, a smaller one from a trigger.
        HashSet<Guid> contextStepIds = [];
        foreach (StratStep mine in doc.Steps.Where(s => ownStepIds.Contains(s.Id)))
        {
            string? myFrom = mine.From?.Place;
            string? myTo = StratStepLines.ToFor(mine, slot);
            foreach (StratStep other in doc.Steps)
            {
                if (ownStepIds.Contains(other.Id) || StratClock.IsAfter(doc.Clock, other.AtSeconds, mine.AtSeconds))
                {
                    continue;
                }

                if (StratStepLines.Destinations(other).Any(p => string.Equals(p, myFrom, StringComparison.Ordinal)
                                                                || string.Equals(p, myTo, StringComparison.Ordinal)))
                {
                    contextStepIds.Add(other.Id);
                }
            }
        }

        List<RoleSheetLine> lines = [];
        foreach (StratStep step in doc.Steps)
        {
            bool mine = ownStepIds.Contains(step.Id);
            if (!mine && !contextStepIds.Contains(step.Id))
            {
                continue;
            }

            string text;
            if (mine && StratStepLines.HasLines(step) && StratStepLines.LineFor(step, slot) is { } line)
            {
                // Only this slot's line: its place and what it watches, not the other players'.
                text = StratStepPhrasing.Phrase(StratStepLines.AsSingle(step, line), callouts, lineupTitle, doc.Clock);
                if (StratStepPhrasing.Watching(line, callouts) is { } watching)
                {
                    text += ", " + watching;
                }
            }
            else
            {
                text = StratStepPhrasing.PhraseWithLines(step, callouts, lineupTitle, doc.Clock);
            }

            if (mine && step.Note is { Length: > 0 } note)
            {
                text += " (" + note + ")";
            }

            lines.Add(new RoleSheetLine(step.Id, step.AtSeconds, mine && StratStepLines.HasLines(step) ? slot : StratStepLines.ActorOf(step), text, !mine));
        }

        List<RoleSheetBranchLine> branches =
        [
            .. doc.Branches.Where(b => ownStepIds.Contains(b.AfterStepId))
                .Select(b => new RoleSheetBranchLine(b.Id, b.AfterStepId,
                    $"if {b.Condition.Text} → {StratBranchPhrasing.TargetText(b.Target, doc, lookup)}"))
        ];

        List<RoleSheetPoint> positions = [];
        foreach (StratStep step in doc.Steps)
        {
            StepPosition? position = step.Positions.Find(p => string.Equals(p.Slot, slot, StringComparison.Ordinal));
            if (position is not null)
            {
                positions.Add(new RoleSheetPoint(step.AtSeconds, position.X, position.Y, position.LevelMinZ, position.YawDegrees));
            }
        }

        return new RoleSheet(header, lines, branches, positions);
    }
}
