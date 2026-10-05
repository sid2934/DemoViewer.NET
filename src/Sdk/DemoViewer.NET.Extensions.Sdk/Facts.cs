using System.Globalization;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>Names one output of one ruleset: a table its <c>show: tables:</c> declares, or its scoreboard.</summary>
/// <param name="RulesetId">The ruleset's id, as its <c>ruleset:</c> key spells it.</param>
/// <param name="Output">The table's name, or <see cref="ScoreboardOutput" />.</param>
public sealed record FactKey(string RulesetId, string Output)
{
    /// <summary>
    ///     The output name of a ruleset's <c>show: scoreboard:</c>. A scoreboard is read off snapshots only a full
    ///     analysis of one demo takes, so it is never written for the library and always reads as
    ///     <see cref="FactStatus.NeedsFullAnalysis" />.
    /// </summary>
    public const string ScoreboardOutput = "scoreboard";

    /// <summary>"<c>ruleset/output</c>".</summary>
    public override string ToString() => RulesetId + "/" + Output;
}

/// <summary>Where one output of a demo stands.</summary>
public enum FactStatus
{
    /// <summary>
    ///     Nothing is written for the demo: the ruleset has not run on it yet, it is not one the library knows, or
    ///     the extension that owns it is off.
    /// </summary>
    Absent,

    /// <summary>Written under the ruleset as it is now.</summary>
    Current,

    /// <summary>Written under an earlier version of the ruleset; the library will write it again.</summary>
    Stale,

    /// <summary>The ruleset did not run on the demo: it does not compose, or the engine left it out of the build.</summary>
    Failed,

    /// <summary>The output is read off snapshots, which only a full analysis of the open demo takes. Never written for the library.</summary>
    NeedsFullAnalysis
}

/// <summary>What a <see cref="FactValue" /> holds.</summary>
public enum FactValueKind
{
    /// <summary>No value.</summary>
    Null,

    /// <summary>A whole number.</summary>
    Whole,

    /// <summary>A number with a fraction.</summary>
    Number,

    /// <summary>True or false.</summary>
    Boolean,

    /// <summary>Text.</summary>
    Text,

    /// <summary>A list of values.</summary>
    List
}

/// <summary>One cell of a <see cref="FactTable" />: null, a whole number, a number, a boolean, text, or a list of those.</summary>
public sealed class FactValue : IEquatable<FactValue>
{
    private readonly bool _boolean;
    private readonly long _integer;
    private readonly IReadOnlyList<FactValue> _items;
    private readonly double _number;
    private readonly string? _text;

    private FactValue(FactValueKind kind, long integer = 0, double number = 0, bool boolean = false, string? text = null,
        IReadOnlyList<FactValue>? items = null)
    {
        Kind = kind;
        _integer = integer;
        _number = number;
        _boolean = boolean;
        _text = text;
        _items = items ?? [];
    }

    /// <summary>The null cell.</summary>
    public static FactValue Null { get; } = new(FactValueKind.Null);

    /// <summary>What the cell holds.</summary>
    public FactValueKind Kind { get; }

    /// <summary>The items of a <see cref="FactValueKind.List" /> cell; empty for every other kind.</summary>
    public IReadOnlyList<FactValue> Items => _items;

    /// <summary>A whole number.</summary>
    /// <param name="value">The value.</param>
    public static FactValue Of(long value) => new(FactValueKind.Whole, value);

    /// <summary>A number with a fraction.</summary>
    /// <param name="value">The value.</param>
    public static FactValue Of(double value) => new(FactValueKind.Number, number: value);

    /// <summary>True or false.</summary>
    /// <param name="value">The value.</param>
    public static FactValue Of(bool value) => new(FactValueKind.Boolean, boolean: value);

    /// <summary>Text; <see cref="Null" /> for null.</summary>
    /// <param name="value">The value.</param>
    public static FactValue Of(string? value) => value is null ? Null : new FactValue(FactValueKind.Text, text: value);

    /// <summary>A list.</summary>
    /// <param name="items">The items.</param>
    public static FactValue Of(IEnumerable<FactValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new FactValue(FactValueKind.List, items: [.. items]);
    }

    /// <summary>The whole number, or null when the cell is not one.</summary>
    public long? AsInteger() => Kind == FactValueKind.Whole ? _integer : null;

    /// <summary>The number, a whole number widened, or null when the cell is neither.</summary>
    public double? AsNumber() => Kind switch
    {
        FactValueKind.Number => _number,
        FactValueKind.Whole => _integer,
        _ => null
    };

    /// <summary>The boolean, or null when the cell is not one.</summary>
    public bool? AsBoolean() => Kind == FactValueKind.Boolean ? _boolean : null;

    /// <summary>The cell as text: null for <see cref="Null" />, invariant culture, a list comma-joined.</summary>
    public string? AsText() => Kind switch
    {
        FactValueKind.Null => null,
        FactValueKind.Whole => _integer.ToString(CultureInfo.InvariantCulture),
        FactValueKind.Number => _number.ToString("R", CultureInfo.InvariantCulture),
        FactValueKind.Boolean => _boolean ? "true" : "false",
        FactValueKind.Text => _text,
        _ => string.Join(",", _items.Select(i => i.AsText() ?? ""))
    };

    /// <inheritdoc />
    public bool Equals(FactValue? other) =>
        other is not null && Kind == other.Kind && Kind switch
        {
            FactValueKind.Null => true,
            FactValueKind.Whole => _integer == other._integer,
            FactValueKind.Number => _number.Equals(other._number),
            FactValueKind.Boolean => _boolean == other._boolean,
            FactValueKind.Text => string.Equals(_text, other._text, StringComparison.Ordinal),
            _ => _items.SequenceEqual(other._items)
        };

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FactValue);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, AsText());

    /// <summary>The cell as text, "null" for <see cref="Null" />.</summary>
    public override string ToString() => AsText() ?? "null";
}

/// <summary>One row of a <see cref="FactTable" />.</summary>
/// <param name="Dimensions">The row's key columns, such as <c>round_number</c> and <c>side</c>.</param>
/// <param name="Values">The row's value columns.</param>
public sealed record FactRow(IReadOnlyDictionary<string, FactValue> Dimensions, IReadOnlyDictionary<string, FactValue> Values);

/// <summary>One output of one ruleset for one demo, as the library stored it.</summary>
/// <param name="Key">Which output.</param>
/// <param name="Fingerprint">What it was computed under; <see cref="IAnalysisFacts.Status" /> compares it with the ruleset now.</param>
/// <param name="Schema">The host's storage shape when it was written.</param>
/// <param name="Grain">The table's <c>per:</c>: <c>player_match</c>, <c>player_round</c>, <c>team_round</c> and so on.</param>
/// <param name="Dimensions">The key columns, in declared order.</param>
/// <param name="Values">The value columns, in declared order.</param>
/// <param name="ColumnClocks">Per tick column, the clock it is on: <c>"frame"</c> or <c>"server"</c>.</param>
/// <param name="Rows">The rows.</param>
public sealed record FactTable(
    FactKey Key,
    string Fingerprint,
    int Schema,
    string Grain,
    IReadOnlyList<string> Dimensions,
    IReadOnlyList<string> Values,
    IReadOnlyDictionary<string, string> ColumnClocks,
    IReadOnlyList<FactRow> Rows);

/// <summary>One highlight the library's scan found in a demo.</summary>
/// <param name="RulesetId">The ruleset that declares it.</param>
/// <param name="HighlightId">Its id inside the ruleset.</param>
/// <param name="Tick">When it fired, in the demo's frame clock.</param>
/// <param name="PlayerSlot">The player it is about, by slot.</param>
/// <param name="RoundNumber">The round it fired in.</param>
/// <param name="Title">The rendered title.</param>
/// <param name="Score">The authored score.</param>
/// <param name="Kind">The authored kind, by name.</param>
public sealed record LibraryHighlight(
    string RulesetId,
    string HighlightId,
    int Tick,
    int PlayerSlot,
    int RoundNumber,
    string Title,
    int Score,
    string Kind);

/// <summary>
///     Every analysis output the library holds for its demos: the highlights, Round Facts, and the tables of every
///     ruleset the host runs beside them, its own and each extension's. Facts are library data, so an extension
///     reads another's as freely as its own; the outputs of a ruleset whose extension is off read as absent.
///     <para>
///         <see cref="Declared" />, <see cref="Status" /> and <see cref="IsCurrent" /> read the library's index and
///         open no file. <see cref="TryGet" />, <see cref="Highlights" /> and the Round Facts rows read one file each:
///         call them off the UI thread. A write of a demo's facts raises
///         <see cref="IExtensionLibrary.Changed" /> for it.
///     </para>
/// </summary>
public interface IAnalysisFacts
{
    /// <summary>
    ///     Every output the host runs now, by ruleset and output: each table of each ruleset that is on, and each
    ///     scoreboard (<see cref="FactKey.ScoreboardOutput" />). Round Facts is read through <see cref="RoundFacts" />.
    /// </summary>
    IReadOnlyList<FactKey> Declared { get; }

    /// <summary>Where <paramref name="key" /> stands for the demo at <paramref name="demoPath" />.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="key">The output.</param>
    FactStatus Status(string demoPath, FactKey key);

    /// <summary>True when <see cref="Status" /> is <see cref="FactStatus.Current" />.</summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="key">The output.</param>
    bool IsCurrent(string demoPath, FactKey key);

    /// <summary>
    ///     The table as last written for the demo, current or stale, or null when none is: absent, never written,
    ///     or an output only a full analysis produces.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    /// <param name="key">The output.</param>
    FactTable? TryGet(string demoPath, FactKey key);

    /// <summary>The highlights the library's scan found in the demo; empty when it has not scanned it.</summary>
    /// <param name="demoPath">The demo's path.</param>
    IReadOnlyList<LibraryHighlight> Highlights(string demoPath);

    /// <summary>The per-round, per-side rows of the core <c>round_facts</c> ruleset.</summary>
    IRoundFacts RoundFacts { get; }
}
