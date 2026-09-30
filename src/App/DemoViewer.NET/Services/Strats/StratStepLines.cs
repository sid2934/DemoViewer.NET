namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     The one reading of who a step names and where each goes. A step with <c>assignments</c> is read from its
///     lines only; its <c>actor</c> is a summary for older readers and its <c>to</c> is not used. A step without
///     them reads as it always did: one line for a single-slot actor, none for <c>all</c>.
/// </summary>
public static class StratStepLines
{
    /// <summary>Whether the step stores lines.</summary>
    /// <param name="step">The step.</param>
    public static bool HasLines(StratStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Assignments is { Count: > 0 };
    }

    /// <summary>
    ///     The stored lines; else one line of <c>(actor, to)</c> for a single-slot actor; else none. An implicit
    ///     line is a new object, so writing to it writes nothing.
    /// </summary>
    /// <param name="step">The step.</param>
    public static IReadOnlyList<StepAssignment> Of(StratStep step)
    {
        if (HasLines(step))
        {
            return step.Assignments!;
        }

        return StratVocabulary.Slots.Contains(step.Actor) ? [new StepAssignment { Slot = step.Actor, To = step.To }] : [];
    }

    /// <summary>The <c>actor</c> a step with these lines stores: the slot of a single line, else <c>all</c>.</summary>
    /// <param name="lines">The lines.</param>
    public static string ActorFor(IReadOnlyList<StepAssignment> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count == 1 ? lines[0].Slot : StratVocabulary.ActorAll;
    }

    /// <summary>Who the step names: <see cref="ActorFor" /> of its lines, else its stored actor.</summary>
    /// <param name="step">The step.</param>
    public static string ActorOf(StratStep step) => HasLines(step) ? ActorFor(step.Assignments!) : step.Actor;

    /// <summary>Whether <paramref name="slot" /> takes part: it has a line, or with no lines the actor is it or <c>all</c>.</summary>
    /// <param name="step">The step.</param>
    /// <param name="slot">A slot letter.</param>
    public static bool Involves(StratStep step, string slot)
    {
        if (HasLines(step))
        {
            return step.Assignments!.Exists(a => string.Equals(a.Slot, slot, StringComparison.Ordinal));
        }

        return string.Equals(step.Actor, slot, StringComparison.Ordinal)
               || string.Equals(step.Actor, StratVocabulary.ActorAll, StringComparison.Ordinal);
    }

    /// <summary>The slot's line, stored or implicit; null when it has none, which a step for <c>all</c> without lines never gives.</summary>
    /// <param name="step">The step.</param>
    /// <param name="slot">A slot letter.</param>
    public static StepAssignment? LineFor(StratStep step, string slot) =>
        Of(step).FirstOrDefault(a => string.Equals(a.Slot, slot, StringComparison.Ordinal));

    /// <summary>Where <paramref name="slot" /> goes at the step: its line's place, or the step's for a step for <c>all</c>.</summary>
    /// <param name="step">The step.</param>
    /// <param name="slot">A slot letter.</param>
    public static string? ToFor(StratStep step, string slot)
    {
        if (HasLines(step))
        {
            return LineFor(step, slot)?.To?.Place;
        }

        return Involves(step, slot) ? step.To?.Place : null;
    }

    /// <summary>
    ///     Where <paramref name="slot" /> goes at the step, place and point: its line's <c>to</c>, or the step's own for a
    ///     step without lines that names it. Null when it goes nowhere.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="slot">A slot letter.</param>
    public static PlaceRef? LocationFor(StratStep step, string slot)
    {
        if (!StratVocabulary.Slots.Contains(slot))
        {
            return null;
        }

        if (HasLines(step))
        {
            return LineFor(step, slot)?.To;
        }

        return Involves(step, slot) ? step.To : null;
    }

    /// <summary>Every place a step sends someone: each line's, or the step's own <c>to</c>.</summary>
    /// <param name="step">The step.</param>
    public static IEnumerable<string> Destinations(StratStep step)
    {
        if (!HasLines(step))
        {
            return step.To?.Place is { Length: > 0 } to ? [to] : [];
        }

        return step.Assignments!.Select(a => a.To?.Place).OfType<string>().Where(p => p.Length > 0);
    }

    /// <summary>
    ///     The step as <paramref name="line" />'s slot alone: the step's verb, from, utility, lurk and note, the line's
    ///     slot and place. For phrasing one player's part; never stored.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="line">One of its lines.</param>
    public static StratStep AsSingle(StratStep step, StepAssignment line)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(line);
        return new StratStep
        {
            Id = step.Id,
            AtSeconds = step.AtSeconds,
            Actor = line.Slot,
            Verb = step.Verb,
            From = step.From,
            To = line.To,
            Utility = step.Utility,
            Note = step.Note,
            Lurk = step.Lurk
        };
    }

    /// <summary>The line's watched places, first first; empty with no watch.</summary>
    /// <param name="line">A line, or null.</param>
    public static IReadOnlyList<string> Watching(StepAssignment? line) =>
        line?.Watch?.Places is { } places ? [.. places.Where(p => !string.IsNullOrEmpty(p))] : [];
}
