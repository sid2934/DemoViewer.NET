#region

using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>What kind of position entry a <c>placed</c> chip shows.</summary>
public enum StratPlacedKind
{
    /// <summary>An authored entry on a travel verb: where the token leaves from, which re-times the leg before.</summary>
    Departure,

    /// <summary>An authored entry beside a position verb's place: the place is not used.</summary>
    Pinned,

    /// <summary>An authored entry with no field beside it: a spot, the round-start seed among them.</summary>
    Spot,

    /// <summary>Where a capture saw the player.</summary>
    Seen,

    /// <summary>An opponent token.</summary>
    Opponent
}

/// <summary>One position entry in a <c>placed</c> chip: its text, and the step's field its point can go into.</summary>
public sealed partial class StratPlacedEntry
{
    private readonly StratStepRow _row;

    internal StratPlacedEntry(StratStepRow row, int positionIndex, string slot, StratPlacedKind kind, string text, string? convertTo, string? warning)
    {
        _row = row;
        PositionIndex = positionIndex;
        Slot = slot;
        Kind = kind;
        Text = text;
        ConvertLabel = convertTo is null ? "" : "Make it " + convertTo;
        Warning = warning;
    }

    /// <summary>The entry's index in the step's <c>positions[]</c>.</summary>
    public int PositionIndex { get; }

    public string Slot { get; }

    public StratPlacedKind Kind { get; }

    /// <summary>The entry as it reads: <c>E leaves from (1374, 412)</c>.</summary>
    public string Text { get; }

    /// <summary>"Make it the to", "Make it lurk area 1"; empty when the step has no field for it.</summary>
    public string ConvertLabel { get; }

    public bool CanConvert => ConvertLabel.Length > 0;

    /// <summary>The zip warning for a departure that forces a leg faster than a run; null otherwise.</summary>
    public string? Warning { get; }

    /// <summary>Removes the entry: one undo entry.</summary>
    [RelayCommand]
    private void Clear() => _row.Owner.ClearPlaced(_row.Id, [PositionIndex]);

    /// <summary>Moves the point into the field and removes the entry: one undo entry.</summary>
    [RelayCommand]
    private void Convert() => _row.Owner.ConvertPlaced(_row.Id, PositionIndex);
}

/// <summary>
///     A chip in a step row's <c>placed</c> strip: one position entry, or a group of them (<c>spots (5)</c>,
///     <c>seen (5)</c>, <c>opponents (5)</c>) whose flyout lists each.
/// </summary>
public sealed partial class StratPlacedChip
{
    private readonly StratStepRow _row;

    internal StratPlacedChip(StratStepRow row, string text, IReadOnlyList<StratPlacedEntry> entries, bool isGroup)
    {
        _row = row;
        Text = text;
        Entries = entries;
        IsGroup = isGroup;
    }

    public string Text { get; }

    public IReadOnlyList<StratPlacedEntry> Entries { get; }

    /// <summary>A group: the flyout lists its entries.</summary>
    public bool IsGroup { get; }

    public bool IsSingle => !IsGroup;

    /// <summary>A departure: drawn with the caution border, since it re-times the leg before it.</summary>
    public bool IsCaution => !IsGroup && Entries[0].Kind == StratPlacedKind.Departure;

    public string ConvertLabel => IsGroup ? "" : Entries[0].ConvertLabel;

    public bool CanConvert => !IsGroup && Entries[0].CanConvert;

    public string ToolTip => IsGroup
        ? "Click for each entry. ✕ clears them all."
        : Entries[0].Kind switch
        {
            StratPlacedKind.Departure => (Entries[0].Warning is { } warning ? warning + ". " : "")
                                         + "Where the token leaves from at the step's time; it re-times the leg before. ✕ clears it.",
            StratPlacedKind.Pinned => $"Not using the step's place: {Entries[0].Slot} is pinned here. ✕ clears it.",
            StratPlacedKind.Seen => "Where the capture saw the player. Clearing it changes how the captured strat plays.",
            StratPlacedKind.Opponent => "An opponent's spot at this step. ✕ clears it.",
            _ => "An exact spot at this step. ✕ clears it."
        };

    public string Slot => Entries[0].Slot;

    /// <summary>Clears every entry the chip shows: one undo entry.</summary>
    [RelayCommand]
    private void Clear() => _row.Owner.ClearPlaced(_row.Id, [.. Entries.Select(e => e.PositionIndex)]);

    [RelayCommand]
    private void Convert()
    {
        if (!IsGroup)
        {
            Entries[0].ConvertCommand.Execute(null);
        }
    }

    /// <summary>
    ///     A step's position entries as chips (drag-semantics.md §6): one per entry that overrides or replaces a field
    ///     the row shows, the rest grouped by kind; a group of one reads as its entry. Carried entries are bookkeeping
    ///     and never show.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="step">The step.</param>
    /// <param name="placeAt">The place under a point, or null while the map's places are not loaded.</param>
    /// <param name="warnings">Validator warnings by position index.</param>
    /// <param name="legacySeen">A capture written before the mark: every entry reads as seen.</param>
    /// <param name="start">Entries an older file's start is read from, which the Start row shows instead.</param>
    internal static List<StratPlacedChip> For(StratStepRow row, StratStep step, Func<double, double, double, string?>? placeAt,
        IReadOnlyDictionary<int, string>? warnings = null, bool legacySeen = false, IReadOnlySet<int>? start = null)
    {
        List<StratPlacedChip> chips = [];
        List<StratPlacedEntry> spots = [], seen = [], opponents = [];
        StepMotion motion = StratStepFields.MotionOf(step.Verb);
        for (int k = 0; k < step.Positions.Count; k++)
        {
            StepPosition position = step.Positions[k];
            // An older file's start entries are shown by the Start row.
            if (position.Carried == true || string.IsNullOrEmpty(position.Slot) || start?.Contains(k) == true)
            {
                continue;
            }

            string point = StratLocations.PointText(position.X, position.Y);
            string slot = position.Slot;
            string? convertTo = ConvertWord(step, slot);
            string? warning = warnings is not null && warnings.TryGetValue(k, out string? w) ? w : null;
            if (!StratVocabulary.Slots.Contains(slot))
            {
                string yaw = position.YawDegrees is { } y ? " " + y.ToString("0", CultureInfo.InvariantCulture) + "°" : "";
                opponents.Add(new StratPlacedEntry(row, k, slot, StratPlacedKind.Opponent, $"{slot} at {point}{yaw}", null, null));
                continue;
            }

            bool names = StratStepLines.Involves(step, slot);
            PlaceRef? field = names ? StratStepLines.LocationFor(step, slot) : null;
            if (position.Observed == true || legacySeen)
            {
                StratPlacedEntry entry = new(row, k, slot, StratPlacedKind.Seen, $"{slot} seen at {point}", convertTo, null);
                if (Disagrees(field, position, placeAt))
                {
                    chips.Add(new StratPlacedChip(row, entry.Text, [entry], false));
                }
                else
                {
                    seen.Add(entry);
                }

                continue;
            }

            if (names && motion is StepMotion.Travel or StepMotion.Lurk)
            {
                StratPlacedEntry entry = new(row, k, slot, StratPlacedKind.Departure, $"{slot} leaves from {point}", convertTo, warning);
                chips.Add(new StratPlacedChip(row, entry.Text, [entry], false));
            }
            else if (names && motion == StepMotion.Position && StratLocations.IsSet(field))
            {
                StratPlacedEntry entry = new(row, k, slot, StratPlacedKind.Pinned, $"{slot} pinned at {point}", convertTo, null);
                chips.Add(new StratPlacedChip(row, entry.Text, [entry], false));
            }
            else
            {
                spots.Add(new StratPlacedEntry(row, k, slot, StratPlacedKind.Spot, $"{slot} at {point}", convertTo, null));
            }
        }

        Group(row, chips, "spots", spots);
        Group(row, chips, "seen", seen);
        Group(row, chips, "opponents", opponents);
        return chips;
    }

    private static void Group(StratStepRow row, List<StratPlacedChip> chips, string name, List<StratPlacedEntry> entries)
    {
        if (entries.Count == 1)
        {
            chips.Add(new StratPlacedChip(row, entries[0].Text, entries, false));
        }
        else if (entries.Count > 1)
        {
            chips.Add(new StratPlacedChip(row, string.Create(CultureInfo.InvariantCulture, $"{name} ({entries.Count})"), entries, true));
        }
    }

    // The field the verb table names for the slot, in the row's words: "the to", "the at", "lurk area 1".
    private static string? ConvertWord(StratStep step, string slot) =>
        StratDragPatches.FieldFor(step, slot)?.Kind switch
        {
            StratLocationKind.LurkArea => "lurk area 1",
            StratLocationKind.To => "the " + StratStepFields.ToLabel(step.Verb),
            _ => null
        };

    // A capture's spot disagrees with the step's place when the place is a point elsewhere or a place it is not in.
    private static bool Disagrees(PlaceRef? field, StepPosition position, Func<double, double, double, string?>? placeAt)
    {
        if (!StratLocations.IsSet(field))
        {
            return false;
        }

        if (StratLocations.HasPoint(field))
        {
            return Math.Abs(field!.X!.Value - position.X) > 1 || Math.Abs(field.Y!.Value - position.Y) > 1;
        }

        return placeAt is not null && !string.Equals(placeAt(position.X, position.Y, position.LevelMinZ), field!.Place, StringComparison.Ordinal);
    }
}
