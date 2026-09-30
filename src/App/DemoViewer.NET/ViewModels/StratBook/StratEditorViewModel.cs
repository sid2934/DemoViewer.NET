#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The strat editor in the Strat Book tab (strat-model.md §3.11): metadata, the five slots, the step table on
///     the round clock and the branches, all projected from the open <see cref="StratSession" /> and all edited
///     through it, one <see cref="PatchOp" /> per field change, so every edit is one undo entry and one line of the
///     next commit.
///     <para>
///         <b>Projection is one way.</b> <see cref="Project" /> rewrites every bound value from the document after
///         any change, undo included, with <see cref="IsProjecting" /> set so the setters it trips emit nothing. A row
///         is rebuilt only when the list's identity changed (a step added, removed or moved); a field edit updates
///         the rows in place, which keeps focus where the user is typing.
///     </para>
///     <para>
///         <b>Places are stored canonical.</b> A step's from, to and landing are typed as the owner's callouts or the
///         canonical names and resolved through the <see cref="CalloutResolver" />; what does not resolve is stored
///         as typed and the validator warns, the "unknown places warn, never refuse" rule.
///     </para>
/// </summary>
public sealed partial class StratEditorViewModel : ObservableObject
{
    /// <summary>What a combo box shows for a null vocabulary field.</summary>
    public const string None = "none";

    private readonly IStratLineupCatalog? _lineups;
    private readonly PlaceCentreResolver? _placeCentres;
    private readonly StratSession _session;
    private readonly ThrowOriginResolver? _throwOrigins;
    private EditBurst? _burst;
    private CalloutResolver _places = new([]);

    [ObservableProperty]
    private string _economy = None;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _notes = "";

    [ObservableProperty]
    private string _side = StratVocabulary.SideT;

    [ObservableProperty]
    private string _status = nameof(StratStatus.Theory);

    [ObservableProperty]
    private string _tagsText = "";

    [ObservableProperty]
    private string _targetSite = None;

    [ObservableProperty]
    private string _tempo = None;

    [ObservableProperty]
    private string _triggerText = "";

    [ObservableProperty]
    private string _type = "default";

    /// <param name="session">The session the editor reads and writes.</param>
    /// <param name="lineups">
    ///     The Utility Book lineups a step can reference (Lineup On A Strat Step): a row's choices, the name of a
    ///     stored id (an alias id included) and its techniques. Null offers only "none" and shows a stored id raw.
    /// </param>
    /// <param name="throwOrigins">
    ///     The canvas projection's lineup origin resolver, so a new step carries a thrower where the canvas shows it;
    ///     null carries authored positions only.
    /// </param>
    /// <param name="placeCentres">The canvas's place centres, so a new step carries a watching token's facing.</param>
    public StratEditorViewModel(StratSession session, IStratLineupCatalog? lineups = null, ThrowOriginResolver? throwOrigins = null,
        PlaceCentreResolver? placeCentres = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _lineups = lineups;
        _throwOrigins = throwOrigins;
        _placeCentres = placeCentres;
    }

    public static IReadOnlyList<string> Sides { get; } = [StratVocabulary.SideT, StratVocabulary.SideCt];

    public static IReadOnlyList<string> Types { get; } = StratVocabulary.Types;

    public static IReadOnlyList<string> TargetSites { get; } = [None, .. StratVocabulary.TargetSites];

    public static IReadOnlyList<string> Economies { get; } = [None, .. StratVocabulary.Economies];

    public static IReadOnlyList<string> Tempos { get; } = [None, .. StratVocabulary.Tempos];

    public static IReadOnlyList<string> Statuses { get; } = Enum.GetNames<StratStatus>();

    public static IReadOnlyList<string> Actors { get; } = [StratVocabulary.ActorAll, .. StratVocabulary.Slots];

    public static IReadOnlyList<string> Verbs { get; } = StratVocabulary.Verbs;

    public static IReadOnlyList<string> UtilityKinds { get; } = [None, .. StratVocabulary.UtilityKinds];

    /// <summary>True while <see cref="Project" /> writes the bound values; a setter tripped by it emits no op.</summary>
    public bool IsProjecting { get; private set; }

    public bool HasDocument => _session.Document is not null;

    /// <summary>The strat's map, in the parser's spelling. Fixed here; a strat is made on a map.</summary>
    public string Map { get; private set; } = "";

    /// <summary>The strat clock, stated: every time in the table counts down from it.</summary>
    public string ClockLine { get; private set; } = "";

    public ObservableCollection<StratSlotRow> Slots { get; } = [];

    public ObservableCollection<StratStepRow> Steps { get; } = [];

    public ObservableCollection<StratBranchRow> Branches { get; } = [];

    /// <summary>Who a slot can be pinned to: the owner's latest roster, or the me accounts; the first is "book default".</summary>
    public ObservableCollection<StratPinOption> PinOptions { get; } = [];

    /// <summary>This strat's steps, for a branch's "after" choice.</summary>
    public ObservableCollection<StratStepOption> StepOptions { get; } = [];

    /// <summary>This strat and the owner's other strats on the map, for a branch's target.</summary>
    public ObservableCollection<StratTargetOption> TargetStrats { get; } = [];

    /// <summary>The validator's findings, one line each, severity first.</summary>
    public ObservableCollection<string> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    /// <summary>
    ///     What the editor offers beside the document: the map's places with the owner's words, the pin
    ///     candidates, and the owner's strats on the map. Set by the tab when a strat opens or the book changes.
    /// </summary>
    /// <param name="places">The resolver for the strat's owner and map.</param>
    /// <param name="pins">Pin candidates, "book default" excluded.</param>
    /// <param name="targets">The owner's strats on the map, this one included.</param>
    public void Configure(CalloutResolver places, IReadOnlyList<StratPinOption> pins, IReadOnlyList<StratTargetOption> targets)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(targets);
        _places = places;
        IsProjecting = true;
        try
        {
            Replace(PinOptions, [StratPinOption.BookDefault, .. pins]);
            Replace(TargetStrats, targets);
        }
        finally
        {
            IsProjecting = false;
        }

        Project();
    }

    /// <summary>Rewrites every bound value from the session's document.</summary>
    public void Project()
    {
        IsProjecting = true;
        try
        {
            ProjectCore();
        }
        finally
        {
            IsProjecting = false;
        }

        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(Map));
        OnPropertyChanged(nameof(ClockLine));
        OnPropertyChanged(nameof(HasIssues));
    }

    /// <summary>The owner's words over the map's places: what the location fields list and resolve typed text through.</summary>
    public CalloutResolver Places => _places;

    /// <summary>
    ///     One location field written as <paramref name="entries" />, one undo entry, through
    ///     <see cref="StratLocationPatches" />. Nothing while projecting or when it already holds them.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="entries">What it should hold.</param>
    internal void WriteLocation(StratLocationField field, IReadOnlyList<PlaceRef> entries)
    {
        if (IsProjecting || _session.Document is not { } document)
        {
            return;
        }

        int index = document.Steps.FindIndex(s => s.Id == field.StepId);
        if (index < 0)
        {
            return;
        }

        EndEditBurst();
        List<PatchOp> ops = StratLocationPatches.Write(document, index, field, entries);
        if (ops.Count > 0)
        {
            _session.Apply(ops);
        }
    }

    /// <summary>Typed text as a field's locations, keeping stored entries its text still names (<see cref="PlaceFieldModel.Parse(string, IReadOnlyList{PlaceRef}, CalloutResolver?, bool, double?)" />).</summary>
    /// <param name="text">The field's text.</param>
    /// <param name="current">The field's stored value.</param>
    /// <param name="multi">A list field.</param>
    internal List<PlaceRef> ParseLocations(string? text, IReadOnlyList<PlaceRef> current, bool multi) =>
        PlaceFieldModel.Parse(text ?? "", current, _places, multi, DefaultLevelMinZ);

    /// <summary>The level a typed coordinate takes when its field holds no point: the strat canvas's default level.</summary>
    public double? DefaultLevelMinZ => _session.Document?.Canvas?.DefaultLevelMinZ;

    /// <summary>What a location field shows: callouts, places the map lacks as stored, points as coordinates.</summary>
    /// <param name="value">The field's locations.</param>
    internal string DisplayLocations(IReadOnlyList<PlaceRef> value) => PlaceFieldModel.DisplayOf(value, _places);

    /// <summary>What the step table shows for a stored place: the owner's word for a canonical one, else the text as stored.</summary>
    /// <param name="place">A stored place, or null.</param>
    public string DisplayPlace(string? place) =>
        string.IsNullOrEmpty(place) ? "" : _places.IsCanonical(place) ? _places.Display(place) : place;

    /// <summary>The place to store for what was typed: canonical when it resolves, else the trimmed text, else null.</summary>
    /// <param name="text">What the user typed.</param>
    public string? ResolvePlace(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : _places.Resolve(text) ?? text.Trim();

    /// <summary>
    ///     The lineup choices a step's row offers for its utility kind (Lineup On A Strat Step): "none" first,
    ///     then every lineup the catalog has for this strat's map and that kind. Just "none" without a kind, a
    ///     catalog, or while the map is being grouped.
    /// </summary>
    /// <param name="utilityKind">The step's utility kind, or <see cref="None" /> for no utility.</param>
    internal IReadOnlyList<StratLineupOption> LineupOptionsFor(string utilityKind) =>
        utilityKind == None || _lineups is null
            ? [StratLineupOption.None]
            : [StratLineupOption.None, .. _lineups.Options(Map, utilityKind)];

    /// <summary>The lineup a stored id names on this strat's map, an alias id included, or null.</summary>
    /// <param name="lineupId">A stored lineup id.</param>
    internal StratLineupChoice? ResolveLineup(Guid lineupId) => _lineups?.Resolve(Map, lineupId);

    /// <summary>
    ///     A lineup picked on the map, as one undo entry: the kind first (a kind change elsewhere drops the lineup),
    ///     then the lineup and its technique. The landing is kept. Nothing when the step is gone.
    /// </summary>
    /// <param name="stepId">The step.</param>
    /// <param name="utilityKind">The lineup's strat utility kind.</param>
    /// <param name="lineupId">The lineup's primary id.</param>
    /// <param name="technique">The technique key, or null for the most thrown.</param>
    /// <returns>Whether it wrote.</returns>
    public bool ApplyLineupPick(Guid stepId, string utilityKind, Guid lineupId, string? technique)
    {
        EndEditBurst();
        if (_session.Document is not { } document || document.Steps.FindIndex(s => s.Id == stepId) is var index && index < 0)
        {
            return false;
        }

        StratStep step = document.Steps[index];
        string path = StepPath(index) + "/utility";
        List<PatchOp> ops = [];

        // The same lineup (a stored alias id included) keeps the stored id; a null technique is the most thrown.
        StratLineupChoice? stored = step.Utility?.LineupId is { } storedId ? ResolveLineup(storedId) : null;
        bool sameLineup = step.Utility?.LineupId == lineupId || stored?.Id == lineupId;
        string? mostThrown = (stored ?? ResolveLineup(lineupId))?.Techniques is { Count: > 0 } techniques ? techniques[0].Key : null;
        bool sameTechnique = string.Equals(step.Utility?.Technique ?? mostThrown, technique ?? mostThrown, StringComparison.Ordinal);
        if (step.Utility is not null && sameLineup)
        {
            if (!string.Equals(step.Utility.Kind, utilityKind, StringComparison.Ordinal))
            {
                ops.Add(PatchOp.ReplaceOp(path + "/kind", null, JsonValue.Create(utilityKind)));
            }

            if (!sameTechnique)
            {
                ops.Add(technique is null
                    ? PatchOp.RemoveOp(path + "/technique", null)
                    : PatchOp.ReplaceOp(path + "/technique", null, JsonValue.Create(technique)));
            }

            if (ops.Count == 0)
            {
                return false;
            }
        }
        else if (step.Utility is null)
        {
            JsonObject utility = new() { ["kind"] = utilityKind, ["lineupId"] = lineupId.ToString() };
            if (technique is not null)
            {
                utility["technique"] = technique;
            }

            ops.Add(PatchOp.AddOp(path, utility));
        }
        else
        {
            if (!string.Equals(step.Utility.Kind, utilityKind, StringComparison.Ordinal))
            {
                ops.Add(PatchOp.ReplaceOp(path + "/kind", null, JsonValue.Create(utilityKind)));
            }

            ops.Add(PatchOp.ReplaceOp(path + "/lineupId", null, JsonValue.Create(lineupId)));
            if (technique is not null)
            {
                ops.Add(PatchOp.ReplaceOp(path + "/technique", null, JsonValue.Create(technique)));
            }
            else if (step.Utility.Technique is not null)
            {
                ops.Add(PatchOp.RemoveOp(path + "/technique", null));
            }
        }

        _session.Apply(ops);
        return true;
    }

    // ── Edits ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One field edit as one op. Nothing while projecting or without a strat.</summary>
    /// <param name="path">The pointer.</param>
    /// <param name="value">The new value; null writes JSON null, which the file omits.</param>
    internal void Replace(string path, JsonNode? value)
    {
        if (IsProjecting || _session.Document is null)
        {
            return;
        }

        _session.Apply(PatchOp.ReplaceOp(path, null, value));
    }

    /// <summary>Several ops as one undo entry.</summary>
    internal void Apply(IReadOnlyList<PatchOp> ops)
    {
        if (IsProjecting || _session.Document is null)
        {
            return;
        }

        _session.Apply(ops);
    }

    /// <summary>
    ///     A verb change and the removal of every member the new verb does not use (<see cref="StratStepFields" />):
    ///     the row hides those members, and exports print whatever is set. Part of the combo box's burst, so stepping
    ///     through verbs loses nothing.
    /// </summary>
    /// <param name="index">The step's index.</param>
    /// <param name="verb">The new verb.</param>
    internal void ChangeVerb(int index, string verb) => ApplyInBurst(index, "verb", (start, path) =>
    {
        if (string.Equals(start.Verb, verb, StringComparison.Ordinal))
        {
            return [];
        }

        // Each op is also applied to `mid`, so the lines writer computes against the step as those ops leave it.
        StratStep mid = CloneStep(start);
        mid.Verb = verb;
        List<PatchOp> ops = [PatchOp.ReplaceOp(path + "/verb", null, JsonValue.Create(verb))];
        if (mid.From is not null && !StratStepFields.Uses(verb, StratStepField.From))
        {
            ops.Add(PatchOp.RemoveOp(path + "/from", null));
            mid.From = null;
        }

        bool clearTo = !StratStepFields.Uses(verb, StratStepField.To);
        if (mid.To is not null && clearTo)
        {
            ops.Add(PatchOp.RemoveOp(path + "/to", null));
            mid.To = null;
        }

        if (mid.Utility is not null && !StratStepFields.Uses(verb, StratStepField.Utility))
        {
            ops.Add(PatchOp.RemoveOp(path + "/utility", null));
            mid.Utility = null;
        }

        if (mid.Lurk is not null && !StratStepFields.Uses(verb, StratStepField.Lurk))
        {
            ops.Add(PatchOp.RemoveOp(path + "/lurk", null));
            mid.Lurk = null;
        }

        bool clearWatch = !StratStepFields.Uses(verb, StratStepField.Watch);
        List<StepAssignment> lines = StratLinePatches.Copy(mid);
        bool linesChanged = false;
        foreach (StepAssignment line in lines)
        {
            if (clearTo && line.To is not null)
            {
                line.To = null;
                linesChanged = true;
            }

            if (clearWatch && line.Watch is not null)
            {
                line.Watch = null;
                linesChanged = true;
            }
        }

        if (linesChanged)
        {
            ops.AddRange(StratLinePatches.Write(mid, path, lines));
        }

        return ops;
    });

    /// <summary>
    ///     Who takes the step, as one undo entry through <see cref="StratLinePatches.SetWho" />. An empty set writes
    ///     nothing: a step always names someone.
    /// </summary>
    /// <param name="index">The step's index.</param>
    /// <param name="slots">The slots that take part.</param>
    /// <returns>Whether it wrote.</returns>
    public bool SetWho(int index, IReadOnlyCollection<string> slots)
    {
        if (IsProjecting || _session.Document is not { } document || index < 0 || index >= document.Steps.Count)
        {
            return false;
        }

        return SetWho(document.Steps[index].Id, slots);
    }

    /// <summary><see cref="SetWho(int, IReadOnlyCollection{string})" /> by step id; nothing when the step is gone.</summary>
    /// <param name="stepId">The step.</param>
    /// <param name="slots">The slots that take part.</param>
    /// <returns>Whether it wrote.</returns>
    public bool SetWho(Guid stepId, IReadOnlyCollection<string> slots)
    {
        if (IsProjecting || _session.Document is not { } document || document.Steps.FindIndex(s => s.Id == stepId) is var index && index < 0)
        {
            return false;
        }

        EndEditBurst();
        List<PatchOp> ops = StratLinePatches.SetWho(document.Steps[index], StepPath(index), slots);
        if (ops.Count == 0)
        {
            return false;
        }

        _session.Apply(ops);
        return true;
    }

    /// <summary>The open strat's id, or null.</summary>
    internal Guid? DocumentId => _session.Document?.Id;

    /// <summary>One edit of every line at once, for a row that shows its lines as one; one undo entry.</summary>
    /// <param name="index">The step's index.</param>
    /// <param name="edit">Changes one line.</param>
    internal void EditAllLines(int index, Action<StepAssignment> edit)
    {
        if (IsProjecting || _session.Document is not { } document || index < 0 || index >= document.Steps.Count)
        {
            return;
        }

        EndEditBurst();
        List<PatchOp> ops = StratLinePatches.EditAll(document.Steps[index], StepPath(index), edit);
        if (ops.Count > 0)
        {
            _session.Apply(ops);
        }
    }

    /// <summary>One edit of a step's lurk, one undo entry, written through <see cref="StratLurkPatches" />.</summary>
    /// <param name="index">The step's index.</param>
    /// <param name="edit">Changes a copy of the lurk.</param>
    internal void EditLurk(int index, Action<StepLurk> edit)
    {
        if (IsProjecting || _session.Document is not { } document || index < 0 || index >= document.Steps.Count)
        {
            return;
        }

        EndEditBurst();
        List<PatchOp> ops = StratLurkPatches.Edit(document.Steps[index], StepPath(index), edit);
        if (ops.Count > 0)
        {
            _session.Apply(ops);
        }
    }

    /// <summary>Whether the step's row shows its lines one per player; false for a row that shows them as one, or no row.</summary>
    /// <param name="stepId">The step.</param>
    public bool ShowsLinesApart(Guid stepId) => Steps.FirstOrDefault(r => r.Id == stepId) is { ShowCompact: false, HasLines: true };

    /// <summary>A row switched between its lines as one and one per player; Set On Map reads <see cref="ShowsLinesApart" />.</summary>
    public event Action? LinesViewChanged;

    internal void RaiseLinesViewChanged() => LinesViewChanged?.Invoke();

    private static StratStep CloneStep(StratStep step) =>
        JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep)!.Deserialize(StratJsonContext.Default.StratStep)!;

    // ── Lines ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A line's slot. Part of the combo box's burst, so wheeling through slots loses nothing.</summary>
    /// <param name="index">The step's index.</param>
    /// <param name="line">The line's index in <see cref="StratStepLines.Of" />.</param>
    /// <param name="slot">The new slot.</param>
    /// <param name="expandAll">The row shows a step for everyone as a line per slot.</param>
    internal void ChangeLineSlot(int index, int line, string slot, bool expandAll = false) => ApplyInBurst(index, "slot" + line, (start, path) =>
    {
        List<StepAssignment> lines = StratLinePatches.Copy(start, expandAll);
        if (line >= lines.Count || lines.Exists(l => string.Equals(l.Slot, slot, StringComparison.Ordinal)))
        {
            return [];
        }

        lines[line].Slot = slot;
        return StratLinePatches.Write(start, path, lines);
    });

    /// <summary>One edit of a step's lines, as one undo entry, written in the stored shape (<see cref="StratLinePatches" />).</summary>
    /// <param name="index">The step's index.</param>
    /// <param name="edit">Changes a copy of the lines; the step as it stands is passed for reference.</param>
    /// <param name="expandAll">Read a step for everyone as a line per slot, as a row split apart shows it.</param>
    internal void EditLines(int index, Action<StratStep, List<StepAssignment>> edit, bool expandAll = false)
    {
        if (IsProjecting || _session.Document is not { } document || index < 0 || index >= document.Steps.Count)
        {
            return;
        }

        EndEditBurst();
        StratStep step = document.Steps[index];
        List<PatchOp> ops = StratLinePatches.Edit(step, StepPath(index), lines => edit(step, lines), expandAll);
        if (ops.Count > 0)
        {
            _session.Apply(ops);
        }
    }

    /// <summary>
    ///     A player added to a step: the first slot without a line. On a step for everyone it names the first player,
    ///     who takes the step's place; on a one-player step it makes two lines.
    /// </summary>
    [RelayCommand]
    private void AddLine(StratStepRow? row)
    {
        if (row is null || _session.Document is not { } document)
        {
            return;
        }

        EditLines(document.Steps.FindIndex(s => s.Id == row.Id), (step, lines) =>
        {
            if (StratLinePatches.FreeSlot(lines) is { } slot)
            {
                lines.Add(new StepAssignment { Slot = slot, To = lines.Count == 0 ? step.To : null });
            }
        });
    }

    /// <summary>
    ///     A step's grenade kind. None removes the utility; a new kind keeps the landing and drops the lineup and
    ///     its technique, which belong to the old kind. Part of the combo box's burst.
    /// </summary>
    /// <param name="index">The step's index.</param>
    /// <param name="kind">A utility kind, or <see cref="None" />.</param>
    internal void ChangeUtilityKind(int index, string kind) => ApplyInBurst(index, "utility", (start, path) =>
    {
        path += "/utility";
        JsonObject? utility = UtilityNode(start);
        if (utility is null)
        {
            return kind == None ? [] : [PatchOp.AddOp(path, new JsonObject { ["kind"] = kind })];
        }

        if (kind == None)
        {
            return [PatchOp.RemoveOp(path, null)];
        }

        if (string.Equals(utility["kind"]?.GetValue<string>(), kind, StringComparison.Ordinal))
        {
            return [];
        }

        List<PatchOp> ops = [PatchOp.ReplaceOp(path + "/kind", null, JsonValue.Create(kind))];
        foreach (string owned in (string[])["lineupId", "technique"])
        {
            if (utility.ContainsKey(owned))
            {
                ops.Add(PatchOp.RemoveOp(path + "/" + owned, null));
            }
        }

        return ops;
    });

    /// <summary>A step's lineup; the old lineup's technique goes with it. Part of the combo box's burst.</summary>
    /// <param name="index">The step's index.</param>
    /// <param name="lineupId">The lineup, or null for none.</param>
    internal void ChangeLineup(int index, Guid? lineupId) => ApplyInBurst(index, "lineup", (start, path) =>
    {
        path += "/utility";
        JsonObject? utility = UtilityNode(start);
        if (utility is null || start.Utility?.LineupId == lineupId)
        {
            return [];
        }

        List<PatchOp> ops = [PatchOp.ReplaceOp(path + "/lineupId", null, lineupId is { } id ? JsonValue.Create(id) : null)];
        if (utility.ContainsKey("technique"))
        {
            ops.Add(PatchOp.RemoveOp(path + "/technique", null));
        }

        return ops;
    });

    /// <summary>
    ///     A step's technique. Part of the combo box's burst, compared with what the step effectively had: a stored
    ///     null is the most thrown technique, so wheeling back to it writes nothing.
    /// </summary>
    /// <param name="index">The step's index.</param>
    /// <param name="technique">A technique key.</param>
    /// <param name="mostThrown">The lineup's most thrown technique, what a null technique means.</param>
    internal void ChangeTechnique(int index, string technique, string? mostThrown) => ApplyInBurst(index, "technique", (start, path) =>
        start.Utility is null || string.Equals(start.Utility.Technique ?? mostThrown, technique, StringComparison.Ordinal)
            ? []
            : [PatchOp.ReplaceOp(path + "/utility/technique", null, JsonValue.Create(technique))]);

    /// <summary>
    ///     Ends the combo box burst: the next change starts a new undo entry from the step as it then is. The view
    ///     calls it when a step's combo box loses focus; any other edit ends a burst by itself.
    /// </summary>
    public void EndEditBurst() => _burst = null;

    // A combo box steps through values on the wheel and on Up/Down while closed, and every step lands here. The
    // ops are always computed from the step as it was when the burst began and replace the burst's own undo
    // entry, so passing through "none" or "wait" and back restores everything and leaves one entry.
    private void ApplyInBurst(int index, string field, Func<StratStep, string, List<PatchOp>> build)
    {
        if (IsProjecting || _session.Document is not { } document || index < 0 || index >= document.Steps.Count)
        {
            return;
        }

        StratStep current = document.Steps[index];
        if (_burst is not { } burst || burst.StepId != current.Id || burst.Field != field || burst.Version != _session.Version)
        {
            burst = new EditBurst(current.Id, field,
                JsonSerializer.SerializeToNode(current, StratJsonContext.Default.StratStep)!
                    .Deserialize(StratJsonContext.Default.StratStep)!);
            _burst = burst;
        }

        List<PatchOp> ops = build(burst.Start, StepPath(index));
        if (burst.HasEntry)
        {
            _session.ReplaceLast(ops);
        }
        else if (ops.Count > 0)
        {
            _session.Apply(ops);
        }

        burst.HasEntry = ops.Count > 0;
        burst.Version = _session.Version;
    }

    private static JsonObject? UtilityNode(StratStep step) =>
        JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep)?["utility"] as JsonObject;

    private static string StepPath(int index) => Invariant($"/steps/{index}");

    private sealed class EditBurst(Guid stepId, string field, StratStep start)
    {
        public Guid StepId { get; } = stepId;

        public string Field { get; } = field;

        public StratStep Start { get; } = start;

        public bool HasEntry { get; set; }

        public int Version { get; set; } = -1;
    }

    partial void OnNameChanged(string value) => Replace("/name", JsonValue.Create(value.Trim()));

    partial void OnSideChanged(string value) => Replace("/side", JsonValue.Create(value));

    partial void OnTypeChanged(string value) => Replace("/type", JsonValue.Create(value));

    partial void OnTargetSiteChanged(string value) => Replace("/targetSite", NoneToNull(value));

    partial void OnEconomyChanged(string value) => Replace("/economy", NoneToNull(value));

    partial void OnTempoChanged(string value) => Replace("/tempo", NoneToNull(value));

    partial void OnStatusChanged(string value) => Replace("/status", JsonValue.Create(value));

    partial void OnNotesChanged(string value) => Replace("/notes", string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value));

    partial void OnTagsTextChanged(string value) =>
        Replace("/tags", new JsonArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Select(t => (JsonNode?)JsonValue.Create(t))
            .ToArray()));

    // The trigger's text is the human rule; its kind and time are kept as they are.
    partial void OnTriggerTextChanged(string value)
    {
        if (_session.Document?.Trigger is not null)
        {
            Replace("/trigger/text", JsonValue.Create(value));
        }
        else if (!string.IsNullOrWhiteSpace(value))
        {
            Replace("/trigger", new JsonObject { ["text"] = value });
        }
    }

    /// <summary>How much earlier on the round clock a new step starts than the one it follows.</summary>
    public const double NewStepOffsetSeconds = 5;

    /// <summary>A step added or duplicated here: the view moves focus to its row.</summary>
    public event Action<Guid>? StepFocusRequested;

    /// <summary>
    ///     A new step after <paramref name="after" />, or at the end: every slot, a move, <see cref="NewStepOffsetSeconds" />
    ///     after the step it follows on the round clock, held between its neighbours (never refused), or at the
    ///     round's start for the first. It starts with the tokens where they stand at the step it follows
    ///     (<see cref="StratStepCarry" />) and no strokes. One undo entry.
    /// </summary>
    /// <param name="after">The row it follows; null for the end.</param>
    [RelayCommand]
    private void AddStep(StratStepRow? after)
    {
        if (_session.Document is not { } document)
        {
            return;
        }

        int index = after is null ? -1 : document.Steps.FindIndex(s => s.Id == after.Id);
        if (index < 0)
        {
            index = document.Steps.Count - 1;
        }

        double wanted = index >= 0 ? document.Steps[index].AtSeconds - NewStepOffsetSeconds : document.Clock.RoundSeconds;
        Guid id = Guid.NewGuid();
        Apply([StepAuthoringPatches.AddCarriedStep(document, index, wanted, id, _throwOrigins, _placeCentres)]);
        StepFocusRequested?.Invoke(id);
    }

    /// <summary>
    ///     A copy of the step right after it, with a new id, the same fields, positions and strokes (the strokes
    ///     under new ids), a little later on the clock and held before the next step. One undo entry.
    /// </summary>
    [RelayCommand]
    private void DuplicateStep(StratStepRow? row)
    {
        if (row is null || _session.Document is not { } document)
        {
            return;
        }

        int index = document.Steps.FindIndex(s => s.Id == row.Id);
        if (index < 0)
        {
            return;
        }

        Guid id = Guid.NewGuid();
        Apply([StepAuthoringPatches.DuplicateStep(document, index, id)]);
        StepFocusRequested?.Invoke(id);
    }

    /// <summary>Removes one line; the last one leaves a step for everyone.</summary>
    internal void RemoveLine(StratStepRow row, int line)
    {
        if (_session.Document is { } document)
        {
            EditLines(document.Steps.FindIndex(s => s.Id == row.Id), (_, lines) =>
            {
                if (line < lines.Count)
                {
                    lines.RemoveAt(line);
                }
            }, row.ExpandsAll);
        }
    }

    /// <summary>Removes a step and every branch that names it, as one undo entry: a branch left pointing at nothing is refused.</summary>
    [RelayCommand]
    private void RemoveStep(StratStepRow? row)
    {
        if (row is null || _session.Document is not { } document)
        {
            return;
        }

        int index = document.Steps.FindIndex(s => s.Id == row.Id);
        if (index < 0)
        {
            return;
        }

        List<PatchOp> ops = [];

        // Highest index first, so every pointer still names the element it meant.
        for (int b = document.Branches.Count - 1; b >= 0; b--)
        {
            StratBranch branch = document.Branches[b];
            if (branch.AfterStepId == row.Id || (branch.Target.StratId == document.Id && branch.Target.StepId == row.Id))
            {
                ops.Add(PatchOp.RemoveOp(Invariant($"/branches/{b}"), null));
            }
        }

        ops.Add(PatchOp.RemoveOp(Invariant($"/steps/{index}"), null));
        Apply(ops);
    }

    [RelayCommand]
    private void MoveStepUp(StratStepRow? row) => MoveStep(row, -1);

    [RelayCommand]
    private void MoveStepDown(StratStepRow? row) => MoveStep(row, 1);

    // A move is a remove and an add of the same step, one undo entry. Order is authoring order and Role View's
    // print order; the validator refuses a clock that runs backwards, so a move that breaks it shows as an issue.
    private void MoveStep(StratStepRow? row, int delta)
    {
        if (row is null || _session.Document is not { } document)
        {
            return;
        }

        int index = document.Steps.FindIndex(s => s.Id == row.Id);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= document.Steps.Count)
        {
            return;
        }

        JsonNode node = JsonSerializer.SerializeToNode(document.Steps[index], StratJsonContext.Default.StratStep)!;
        Apply([PatchOp.RemoveOp(Invariant($"/steps/{index}"), null), PatchOp.AddOp(Invariant($"/steps/{target}"), node)]);
    }

    /// <summary>
    ///     A new branch after the last step, continuing in this strat: the step chain form, retargeted by the user.
    ///     Needs a step to hang from.
    /// </summary>
    [RelayCommand]
    private void AddBranch()
    {
        if (_session.Document is not { Steps.Count: > 0 } document)
        {
            return;
        }

        StratBranch branch = new()
        {
            Id = Guid.NewGuid(),
            AfterStepId = document.Steps[^1].Id,
            Target = new BranchTarget { StratId = document.Id }
        };
        Apply([PatchOp.AddOp("/branches/-", JsonSerializer.SerializeToNode(branch, StratJsonContext.Default.StratBranch))]);
    }

    [RelayCommand]
    private void RemoveBranch(StratBranchRow? row)
    {
        if (row is null || _session.Document is not { } document)
        {
            return;
        }

        int index = document.Branches.FindIndex(b => b.Id == row.Id);
        if (index >= 0)
        {
            Apply([PatchOp.RemoveOp(Invariant($"/branches/{index}"), null)]);
        }
    }

    // ── Projection ───────────────────────────────────────────────────────────────────────────────

    private void ProjectCore()
    {
        if (_session.Document is not { } document)
        {
            Map = "";
            ClockLine = "";
            Slots.Clear();
            Steps.Clear();
            Branches.Clear();
            StepOptions.Clear();
            Issues.Clear();
            return;
        }

        Map = document.Map;
        ClockLine = $"round clock, counting down from {StratClock.Format(document.Clock.RoundSeconds)}";
        Name = document.Name;
        Side = document.Side;
        Type = document.Type;
        TargetSite = document.TargetSite ?? None;
        Economy = document.Economy ?? None;
        Tempo = document.Tempo ?? None;
        Status = document.Status;
        Notes = document.Notes ?? "";
        TagsText = string.Join(", ", document.Tags);
        TriggerText = document.Trigger?.Text ?? "";

        if (Slots.Count != document.Slots.Count)
        {
            Slots.Clear();
            for (int i = 0; i < document.Slots.Count; i++)
            {
                Slots.Add(new StratSlotRow(this, i));
            }
        }

        for (int i = 0; i < document.Slots.Count; i++)
        {
            Slots[i].Load(document.Slots[i], PinFor(document.Slots[i].SteamId));
        }

        // Options before rows: a combo box whose items are replaced pushes null into its selection, which the
        // projecting guard swallows, and the row load then sets the real value.
        List<StratStepOption> stepOptions =
            [.. document.Steps.Select((s, i) => new StratStepOption(s.Id, StepLabel(i, s)))];
        if (!StepOptions.SequenceEqual(stepOptions))
        {
            Replace(StepOptions, stepOptions);
        }

        if (!Steps.Select(r => r.Id).SequenceEqual(document.Steps.Select(s => s.Id)))
        {
            Steps.Clear();
            foreach (StratStep step in document.Steps)
            {
                Steps.Add(new StratStepRow(this, step.Id));
            }
        }

        for (int i = 0; i < document.Steps.Count; i++)
        {
            Steps[i].Load(i, document.Steps[i]);
        }

        if (!Branches.Select(r => r.Id).SequenceEqual(document.Branches.Select(b => b.Id)))
        {
            Branches.Clear();
            foreach (StratBranch branch in document.Branches)
            {
                Branches.Add(new StratBranchRow(this, branch.Id));
            }
        }

        for (int i = 0; i < document.Branches.Count; i++)
        {
            StratBranch branch = document.Branches[i];
            Branches[i].Load(i, branch, TargetFor(branch.Target.StratId), TargetStepOptions(document, branch.Target.StratId));
        }

        // One validation per document version, shared by the summary and the rows.
        IReadOnlyList<StratIssue> issues = _session.Issues;
        List<string> lines = [.. issues.Select(issue => $"{issue.Severity.ToString().ToLowerInvariant()}: {issue.Message}"
                                                        + (issue.Field.Length > 0 ? $" ({issue.Field})" : ""))];
        if (!Issues.SequenceEqual(lines))
        {
            Replace(Issues, lines);
        }

        List<StratIssue>[] byStep = new List<StratIssue>[document.Steps.Count];
        foreach (StratIssue issue in issues)
        {
            if (issue.Severity != StratIssueSeverity.Info && StepIndexOf(issue.Field) is { } i && i < byStep.Length)
            {
                (byStep[i] ??= []).Add(issue);
            }
        }

        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].SetIssues(byStep[i] ?? []);
        }
    }

    // "/steps/3/to/place" is step 3.
    private static int? StepIndexOf(string pointer)
    {
        string[] parts = pointer.Split('/');
        return parts.Length >= 3 && parts[1] == "steps"
                                 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? index
            : null;
    }

    private StratPinOption PinFor(string? steamId)
    {
        if (steamId is null)
        {
            return StratPinOption.BookDefault;
        }

        // A pin to someone no longer on the roster still shows, by id, rather than reading as unpinned.
        StratPinOption? known = PinOptions.FirstOrDefault(p => p.SteamId == steamId);
        if (known is null)
        {
            known = new StratPinOption(steamId, steamId);
            PinOptions.Add(known);
        }

        return known;
    }

    private StratTargetOption TargetFor(Guid stratId)
    {
        StratTargetOption? known = TargetStrats.FirstOrDefault(t => t.Id == stratId);
        if (known is null)
        {
            known = new StratTargetOption(stratId, stratId == _session.Document?.Id ? "this strat" : "a strat not in this book");
            TargetStrats.Add(known);
        }

        return known;
    }

    // In this strat a branch may continue at any of its steps; another strat's steps are not loaded here, so
    // a branch to it continues at its start.
    private static List<StratStepOption> TargetStepOptions(StratDocument document, Guid targetStrat)
    {
        List<StratStepOption> options = [StratStepOption.Start];
        if (targetStrat == document.Id)
        {
            options.AddRange(document.Steps.Select((s, i) => new StratStepOption(s.Id, StepLabel(i, s))));
        }

        return options;
    }

    internal static string StepLabel(int index, StratStep step) =>
        Invariant($"{index + 1} · {StratClock.Format(step.AtSeconds)} {StratStepLines.ActorOf(step)} {step.Verb}");

    private static JsonValue? NoneToNull(string? value) =>
        string.IsNullOrEmpty(value) || value == None ? null : JsonValue.Create(value);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A slot pin choice: a SteamID64 and a display name, or the book default (null).</summary>
public sealed record StratPinOption(string? SteamId, string Label)
{
    public static StratPinOption BookDefault { get; } = new(null, "book default");

    public override string ToString() => Label;
}

/// <summary>A step a branch can hang from or continue at; <see cref="Start" /> is "the target's first step".</summary>
public sealed record StratStepOption(Guid? Id, string Label)
{
    public static StratStepOption Start { get; } = new(null, "its start");

    public override string ToString() => Label;
}

/// <summary>A strat a branch can continue in.</summary>
public sealed record StratTargetOption(Guid Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
///     A Utility Book lineup a step's grenade can reference (Lineup On A Strat Step, strat-model.md §3.3.3):
///     <see cref="Id" /> is <c>GrenadeLineup.Id</c>, written to <c>utility.lineupId</c>; <see cref="None" />
///     clears the reference.
/// </summary>
public sealed record StratLineupOption(Guid? Id, string Label)
{
    public static StratLineupOption None { get; } = new(null, StratEditorViewModel.None);

    public override string ToString() => Label;
}

/// <summary>
///     What a step row marks beside one field: the worst severity among the validator's issues there, and every
///     message. A refusal and a warning differ by glyph as well as colour.
/// </summary>
public sealed record StratFieldIssue(bool IsRefusal, string Message)
{
    public string Glyph => IsRefusal ? "!" : "⚠";

    /// <summary>Several fields' marks as one: a refusal if any is, every message once. Null with none.</summary>
    internal static StratFieldIssue? Merge(IReadOnlyList<StratFieldIssue?> issues)
    {
        List<StratFieldIssue> present = [.. issues.OfType<StratFieldIssue>()];
        return present.Count == 0
            ? null
            : new StratFieldIssue(present.Exists(i => i.IsRefusal), string.Join(Environment.NewLine, present.Select(i => i.Message).Distinct()));
    }

    internal static StratFieldIssue? From(Dictionary<string, List<StratIssue>> byField, string field) =>
        byField.TryGetValue(field, out List<StratIssue>? issues)
            ? new StratFieldIssue(issues.Any(i => i.Severity == StratIssueSeverity.Refusal),
                string.Join(Environment.NewLine, issues.Select(i => $"{i.Severity.ToString().ToLowerInvariant()}: {i.Message}")))
            : null;
}

/// <summary>One of the five slots: its role and the optional pin (§3.5).</summary>
public sealed partial class StratSlotRow : ObservableObject
{
    private readonly int _index;
    private readonly StratEditorViewModel _owner;

    [ObservableProperty]
    private StratPinOption? _pin;

    [ObservableProperty]
    private string _role = "";

    internal StratSlotRow(StratEditorViewModel owner, int index)
    {
        _owner = owner;
        _index = index;
    }

    public string Letter { get; private set; } = "";

    public string Label => "Slot " + Letter;

    internal void Load(StratSlot slot, StratPinOption pin)
    {
        Letter = slot.Slot;
        Role = slot.Role ?? "";
        Pin = pin;
        OnPropertyChanged(nameof(Letter));
        OnPropertyChanged(nameof(Label));
    }

    private string Path(string field) => string.Create(CultureInfo.InvariantCulture, $"/slots/{_index}/{field}");

    partial void OnRoleChanged(string value) =>
        _owner.Replace(Path("role"), string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value.Trim()));

    partial void OnPinChanged(StratPinOption? value)
    {
        // A combo box clearing its selection while its items are replaced is not the user unpinning.
        if (value is not null)
        {
            _owner.Replace(Path("steamId"), value.SteamId is null ? null : JsonValue.Create(value.SteamId));
        }
    }
}

/// <summary>One step in the table: time on the round clock, actor, verb, places, utility and note.</summary>
public sealed partial class StratStepRow : ObservableObject
{
    private readonly StratEditorViewModel _owner;

    [ObservableProperty]
    private string _actor = StratVocabulary.ActorAll;

    [ObservableProperty]
    private string _fromText = "";

    // The selected line's slot, kept so rebuilt lines keep the mark.
    private string? _lineSlot;

    private int _index;

    /// <summary>The canvas's active step (<see cref="StratStepSelection" />); display only, never written to the strat.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _landingText = "";

    [ObservableProperty]
    private StratLineupOption? _lineup = StratLineupOption.None;

    [ObservableProperty]
    private StratTechniqueOption? _technique;

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private string _timeText = "";

    [ObservableProperty]
    private string _toText = "";

    [ObservableProperty]
    private string _utilityKind = StratEditorViewModel.None;

    [ObservableProperty]
    private string _verb = "move";

    [ObservableProperty]
    private string _groupPlaceText = "";

    [ObservableProperty]
    private string _groupWatchText = "";

    [ObservableProperty]
    private string _lurkAreasText = "";

    [ObservableProperty]
    private string _rotateAtText = "";

    [ObservableProperty]
    private string _rotateWhenText = "";

    [ObservableProperty]
    private string _rotateToText = "";

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _fromValue = [];

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _toValue = [];

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _landingValue = [];

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _groupPlaceValue = [];

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _groupWatchValue = [];

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _lurkAreasValue = [];

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _rotateToValue = [];

    // Null until the first load; then sticky once the lines differ, so a commit never swaps the view under the caret.
    private bool? _split;

    private bool? _linesImplicit;

    /// <summary>A step for everyone shown apart: its lines are one per slot, and a line's edit writes all five.</summary>
    internal bool ExpandsAll { get; private set; }

    internal StratStepRow(StratEditorViewModel owner, Guid id)
    {
        _owner = owner;
        Id = id;
        WhoOptions = [.. StratVocabulary.Slots.Select(s => new StratWhoOption(s))];
    }

    /// <summary>The owner's words for the location fields' lists.</summary>
    public CalloutResolver Callouts => _owner.Places;

    /// <summary>The level a typed coordinate takes in a field with no point.</summary>
    public double? CurrentLevelMinZ => _owner.DefaultLevelMinZ;

    // What each location field writes, and the map pick it arms. The compact place and watching write every line.
    public StratLocationField FromTarget => new(Id, null, StratLocationKind.From);

    public StratLocationField ToTarget => new(Id, null, StratLocationKind.To);

    public StratLocationField LandingTarget => new(Id, null, StratLocationKind.Landing);

    public StratLocationField GroupPlaceTarget => new(Id, StratLocationField.AllLines, StratLocationKind.To);

    public StratLocationField GroupWatchTarget => new(Id, StratLocationField.AllLines, StratLocationKind.Watch);

    public StratLocationField LurkAreasTarget => new(Id, null, StratLocationKind.LurkArea);

    public StratLocationField RotateToTarget => new(Id, null, StratLocationKind.RotateTo);

    /// <summary>The Who flyout's toggles, A to E. Staged: <see cref="CommitWho" /> writes them as one entry.</summary>
    public IReadOnlyList<StratWhoOption> WhoOptions { get; }

    /// <summary>Who takes the step: <c>all</c>, or its players in slot order.</summary>
    public string WhoText { get; private set; } = StratVocabulary.ActorAll;

    /// <summary>
    ///     The row shows its players as one "who" with one place and one watching: every line agrees and the user has
    ///     not split them. Otherwise one line per player.
    /// </summary>
    public bool ShowCompact { get; private set; } = true;

    public bool ShowApart => !ShowCompact;

    /// <summary>How many players the step names; five for a step for everyone.</summary>
    public int PlayerCount { get; private set; }

    /// <summary>Split is offered on a compact row with more than one player and a place or watching to differ in.</summary>
    public bool CanSplit => ShowCompact && PlayerCount > 1 && (ShowGroupPlace || ShowGroupWatch);

    // Each compact field takes both columns while the other is hidden.
    public int GroupPlaceSpan => ShowGroupWatch ? 1 : 2;

    public int GroupWatchColumn => ShowGroupPlace ? 1 : 0;

    public int GroupWatchSpan => ShowGroupPlace ? 1 : 2;

    /// <summary>Join is offered on a row shown apart once its lines agree again.</summary>
    public bool CanJoin { get; private set; }

    /// <summary>The compact place shows when the verb uses one or the lines hold one.</summary>
    public bool ShowGroupPlace => StratStepFields.Uses(Verb, StratStepField.To) || GroupPlaceText.Length > 0;

    /// <summary>The compact watching shows when the verb uses it or the lines hold one.</summary>
    public bool ShowGroupWatch => StratStepFields.Uses(Verb, StratStepField.Watch) || GroupWatchText.Length > 0;

    /// <summary>The lines share an explicit view angle.</summary>
    public bool HasGroupAngle { get; private set; }

    public string GroupAngleText { get; private set; } = "";

    /// <summary>The lurk's fields show when the verb is a lurk or the step holds one.</summary>
    public bool ShowLurk => StratStepFields.Uses(Verb, StratStepField.Lurk) || _hasLurk;

    private bool _hasLurk;

    /// <summary>The rotate conditions the "or when" field suggests.</summary>
    public static IReadOnlyList<string> RotateConditions => StratVocabulary.RotateConditions;

    public StratFieldIssue? WhoIssue { get; private set; }

    public StratFieldIssue? GroupPlaceIssue { get; private set; }

    public StratFieldIssue? GroupWatchIssue { get; private set; }

    public StratFieldIssue? LurkAreasIssue { get; private set; }

    public StratFieldIssue? RotateAtIssue { get; private set; }

    public StratFieldIssue? RotateToIssue { get; private set; }

    /// <summary>
    ///     The Who flyout opened: its toggles are staged from here, and a reprojection leaves them alone until
    ///     <see cref="CommitWho" />.
    /// </summary>
    public void BeginWho()
    {
        LoadWho();
        _whoOpen = new WhoSnapshot(_owner.DocumentId, [.. WhoOptions.Where(o => o.IsChecked).Select(o => o.Slot)]);
    }

    /// <summary>
    ///     Writes the Who flyout's toggles as one undo entry: the players added and removed since it opened, applied to
    ///     the step as it now is. Nothing when none would be left, or when the step or the strat is no longer the one it
    ///     opened on.
    /// </summary>
    public void CommitWho()
    {
        WhoSnapshot? open = _whoOpen;
        _whoOpen = null;
        HashSet<string> staged = [.. WhoOptions.Where(o => o.IsChecked).Select(o => o.Slot)];
        HashSet<string> before = open?.Checked ?? [.. _players];
        if (open is null || open.StratId == _owner.DocumentId)
        {
            HashSet<string> target = [.. _players];
            target.ExceptWith(before.Except(staged));
            target.UnionWith(staged.Except(before));
            if (!target.SetEquals(_players) && _owner.SetWho(Id, target))
            {
                return;
            }
        }

        LoadWho();
    }

    /// <summary>Checks all five in the flyout; written with the rest on <see cref="CommitWho" />.</summary>
    [RelayCommand]
    private void WhoAll()
    {
        foreach (StratWhoOption option in WhoOptions)
        {
            option.IsChecked = true;
        }
    }

    /// <summary>Shows the players one line each, to give them different places; writes nothing.</summary>
    [RelayCommand]
    private void Split()
    {
        _split = true;
        _owner.Project();
        _owner.RaiseLinesViewChanged();
    }

    /// <summary>Shows lines that agree as one again; writes nothing.</summary>
    [RelayCommand]
    private void Join()
    {
        if (!CanJoin)
        {
            return;
        }

        _split = false;
        _owner.Project();
        _owner.RaiseLinesViewChanged();
    }

    /// <summary>Drops the lines' shared view angle: each faces its first watched place again.</summary>
    [RelayCommand]
    private void ClearGroupAngle() => _owner.EditAllLines(_index, line =>
    {
        if (line.Watch is { } watch)
        {
            watch.YawDegrees = null;
        }
    });

    private bool _agree = true;

    // The open flyout's toggles are the user's until it closes.
    private void LoadWho()
    {
        if (_whoOpen is not null)
        {
            return;
        }

        foreach (StratWhoOption option in WhoOptions)
        {
            option.IsChecked = _players.Contains(option.Slot, StringComparer.Ordinal);
        }
    }

    private List<string> _players = [];

    private WhoSnapshot? _whoOpen;

    private sealed record WhoSnapshot(Guid? StratId, HashSet<string> Checked);

    private void RefreshView()
    {
        ShowCompact = _agree && (_split != true || PlayerCount < 2);
        CanJoin = !ShowCompact && _agree && PlayerCount > 1;
        foreach (string name in (string[])[nameof(ShowCompact), nameof(ShowApart), nameof(CanSplit), nameof(CanJoin)])
        {
            OnPropertyChanged(name);
        }
    }

    public Guid Id { get; }

    public int Number => _index + 1;

    internal int Index => _index;

    internal StratEditorViewModel Owner => _owner;

    public bool HasUtility => UtilityKind != StratEditorViewModel.None;

    /// <summary>
    ///     Who goes where: the stored lines, or the one implicit line of a one-player step. Empty for a step for
    ///     everyone, whose place is the step's own.
    /// </summary>
    public ObservableCollection<StratLineRow> Lines { get; } = [];

    /// <summary>The step stores lines: its actor is their summary, so the actor combo is not the way to change it.</summary>
    public bool HasStoredLines { get; private set; }

    public bool HasLines => Lines.Count > 0;

    public bool CanEditActor => !HasStoredLines;

    public bool CanAddLine => Lines.Count < StratVocabulary.Slots.Count;

    // A member the verb does not use still shows while it holds a value, so nothing an export prints is hidden.
    public bool ShowFrom => StratStepFields.Uses(Verb, StratStepField.From) || FromText.Length > 0;

    // With lines each line holds its place; a step-level one beside stored lines shows only to be cleared.
    public bool ShowTo => HasStoredLines
        ? ToText.Length > 0
        : !HasLines && (StratStepFields.Uses(Verb, StratStepField.To) || ToText.Length > 0);

    public bool ShowUtility => StratStepFields.Uses(Verb, StratStepField.Utility) || HasUtility;

    /// <summary>The picked lineup's techniques, most thrown first; empty without a resolved lineup.</summary>
    public ObservableCollection<StratTechniqueOption> Techniques { get; } = [];

    /// <summary>The technique combo shows when the lineup is thrown more than one way.</summary>
    public bool ShowTechnique => ShowLineup && Techniques.Count > 1;

    /// <summary>A lineup is picked once a kind is.</summary>
    public bool ShowLineup => HasUtility;

    /// <summary>A lineup says where the grenade lands, so "lands at" is offered only without one.</summary>
    public bool ShowLanding => HasUtility && (Lineup?.Id is null || LandingText.Length > 0);

    /// <summary>What this verb calls its <c>to</c> place: to, at or site.</summary>
    public string ToLabel => StratStepFields.ToLabel(Verb);

    /// <summary>This row's lineup choices for its current utility kind: "none" first, then the owner's lookup.</summary>
    public ObservableCollection<StratLineupOption> LineupOptions { get; } = [StratLineupOption.None];

    public StratFieldIssue? TimeIssue { get; private set; }

    public StratFieldIssue? ActorIssue { get; private set; }

    public StratFieldIssue? VerbIssue { get; private set; }

    public StratFieldIssue? FromIssue { get; private set; }

    public StratFieldIssue? ToIssue { get; private set; }

    public StratFieldIssue? UtilityIssue { get; private set; }

    public StratFieldIssue? LineupIssue { get; private set; }

    public StratFieldIssue? TechniqueIssue { get; private set; }

    public StratFieldIssue? LandingIssue { get; private set; }

    /// <summary>Issues about the step as a whole, or about a field the row does not show: marked at the row's start.</summary>
    public StratFieldIssue? RowIssue { get; private set; }

    /// <summary>Every marked issue as a line, for keyboard and screen-reader users; empty without any.</summary>
    public string IssueText { get; private set; } = "";

    public bool HasIssues => IssueText.Length > 0;

    /// <summary>Places the validator's warnings and refusals for this step beside the fields they name.</summary>
    /// <param name="issues">This step's issues, infos excluded.</param>
    internal void SetIssues(IReadOnlyList<StratIssue> issues)
    {
        Dictionary<string, List<StratIssue>> byField = new(StringComparer.Ordinal);
        List<string> lines = [];
        List<(StratIssue Issue, string Member)>[] perLine = [.. Lines.Select(_ => new List<(StratIssue, string)>())];
        foreach (StratIssue issue in issues)
        {
            if (LineOf(issue.Field) is ({ } line, { } member))
            {
                perLine[line].Add((issue, member));
                lines.Add($"{issue.Severity.ToString().ToLowerInvariant()}: {issue.Message} ({Lines[line].Slot} {Lines[line].LabelOf(member)})");
                continue;
            }

            string field = FieldOf(issue.Field);
            if (!byField.TryGetValue(field, out List<StratIssue>? list))
            {
                list = [];
                byField[field] = list;
            }

            list.Add(issue);
            lines.Add($"{issue.Severity.ToString().ToLowerInvariant()}: {issue.Message} ({LabelOf(field)})");
        }

        TimeIssue = StratFieldIssue.From(byField, nameof(TimeIssue));
        ActorIssue = StratFieldIssue.From(byField, nameof(ActorIssue));
        VerbIssue = StratFieldIssue.From(byField, nameof(VerbIssue));
        FromIssue = StratFieldIssue.From(byField, nameof(FromIssue));
        ToIssue = StratFieldIssue.From(byField, nameof(ToIssue));
        UtilityIssue = StratFieldIssue.From(byField, nameof(UtilityIssue));
        LineupIssue = StratFieldIssue.From(byField, nameof(LineupIssue));
        TechniqueIssue = StratFieldIssue.From(byField, nameof(TechniqueIssue));
        LandingIssue = StratFieldIssue.From(byField, nameof(LandingIssue));
        RowIssue = StratFieldIssue.From(byField, nameof(RowIssue));
        LurkAreasIssue = StratFieldIssue.From(byField, nameof(LurkAreasIssue));
        RotateAtIssue = StratFieldIssue.From(byField, nameof(RotateAtIssue));
        RotateToIssue = StratFieldIssue.From(byField, nameof(RotateToIssue));
        IssueText = string.Join(Environment.NewLine, lines);
        for (int j = 0; j < Lines.Count; j++)
        {
            Lines[j].SetIssues(perLine[j]);
        }

        // A compact row shows every line's marks on its one who, place and watching.
        WhoIssue = StratFieldIssue.Merge([ActorIssue, .. Lines.Select(l => l.SlotIssue)]);
        GroupPlaceIssue = StratFieldIssue.Merge(HasLines ? [.. Lines.Select(l => l.PlaceIssue)] : IsStrayTo ? [] : [ToIssue]);
        GroupWatchIssue = StratFieldIssue.Merge([.. Lines.Select(l => l.WatchIssue)]);

        foreach (string name in (string[])
                 [
                     nameof(TimeIssue), nameof(ActorIssue), nameof(VerbIssue), nameof(FromIssue), nameof(ToIssue), nameof(UtilityIssue),
                     nameof(LineupIssue), nameof(TechniqueIssue), nameof(LandingIssue), nameof(RowIssue), nameof(IssueText), nameof(HasIssues),
                     nameof(LurkAreasIssue), nameof(RotateAtIssue), nameof(RotateToIssue), nameof(WhoIssue), nameof(GroupPlaceIssue),
                     nameof(GroupWatchIssue)
                 ])
        {
            OnPropertyChanged(name);
        }
    }

    // The line and member a pointer marks: /steps/i/assignments/j/{slot,to,watch}, or the step's own to while a
    // one-player step shows it on its implicit line.
    private (int? Line, string? Member) LineOf(string pointer)
    {
        string[] parts = pointer.Split('/');
        if (HasStoredLines && parts.Length > 4 && parts[3] == "assignments"
            && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int line) && line < Lines.Count)
        {
            return (line, parts.Length > 5 ? parts[5] : "slot");
        }

        return !HasStoredLines && Lines.Count == 1 && parts.Length > 3 && parts[3] == "to" ? (0, "to") : (null, null);
    }

    // The marker a pointer under /steps/{i} belongs to. A field the row hides marks the row instead.
    private string FieldOf(string pointer)
    {
        string[] parts = pointer.Split('/');
        string member = parts.Length > 3 ? parts[3] : "";
        string sub = parts.Length > 4 ? parts[4] : "";
        return (member, sub) switch
        {
            ("atSeconds", _) => nameof(TimeIssue),
            ("actor", _) => nameof(ActorIssue),
            ("verb", _) => nameof(VerbIssue),
            ("from", _) when ShowFrom => nameof(FromIssue),
            ("to", _) when ShowTo => nameof(ToIssue),
            ("utility", "lineupId") => ShowLineup ? nameof(LineupIssue) : nameof(RowIssue),
            ("utility", "technique") => ShowTechnique ? nameof(TechniqueIssue) : nameof(RowIssue),
            ("utility", "landing") => ShowLanding ? nameof(LandingIssue) : nameof(RowIssue),
            ("utility", _) when ShowUtility => nameof(UtilityIssue),
            ("lurk", "areas") when ShowLurk => nameof(LurkAreasIssue),
            ("lurk", "rotate") when ShowLurk => (parts.Length > 5 ? parts[5] : "") switch
            {
                "atSeconds" => nameof(RotateAtIssue),
                "to" => nameof(RotateToIssue),
                _ => nameof(RowIssue)
            },
            _ => nameof(RowIssue)
        };
    }

    private string LabelOf(string field) => field switch
    {
        nameof(TimeIssue) => "at",
        nameof(ActorIssue) => "actor",
        nameof(VerbIssue) => "verb",
        nameof(FromIssue) => "from",
        nameof(ToIssue) => ToLabel,
        nameof(UtilityIssue) => "utility",
        nameof(LineupIssue) => "lineup",
        nameof(TechniqueIssue) => "thrown",
        nameof(LandingIssue) => "lands at",
        nameof(LurkAreasIssue) => "lurk areas",
        nameof(RotateAtIssue) => "rotate at",
        nameof(RotateToIssue) => "rotate to",
        _ => "step"
    };

    internal void Load(int index, StratStep step)
    {
        _index = index;
        TimeText = StratClock.Format(step.AtSeconds);
        Actor = step.Actor;
        Verb = step.Verb;
        FromValue = StratLocationPatches.Read(step, FromTarget);
        FromText = _owner.DisplayLocations(FromValue);
        ToValue = StratLocationPatches.Read(step, ToTarget);
        ToText = _owner.DisplayLocations(ToValue);
        UtilityKind = step.Utility?.Kind ?? StratEditorViewModel.None;
        LandingValue = StratLocationPatches.Read(step, LandingTarget);
        LandingText = _owner.DisplayLocations(LandingValue);
        OnPropertyChanged(nameof(Callouts));
        Note = step.Note ?? "";
        LoadGroup(step);
        LoadLines(step);
        LoadWho();
        LoadLurk(step);

        // A stored id resolves through the catalog, an alias id to the lineup that absorbed it; the row shows the
        // lineup and never rewrites the stored id. One the catalog cannot resolve (a reindex moved the throw, the
        // map is still being grouped, or there is no catalog) shows as its raw id rather than "none": the file
        // still carries the reference. The selection is one of the items, or the combo box shows nothing.
        List<StratLineupOption> options = [.. _owner.LineupOptionsFor(UtilityKind)];
        Guid? lineupId = step.Utility?.LineupId;
        StratLineupChoice? choice = lineupId is { } stored ? _owner.ResolveLineup(stored) : null;
        Guid? shownId = choice?.Id ?? lineupId;
        if (shownId is { } id && options.All(o => o.Id != id))
        {
            options.Add(new StratLineupOption(id, choice?.Title ?? id.ToString()));
        }

        Sync(LineupOptions, options);
        Lineup = shownId is { } picked ? LineupOptions.First(o => o.Id == picked) : StratLineupOption.None;

        // A null technique is the most thrown one, shown selected without being written.
        Sync(Techniques, choice?.Techniques ?? []);
        Technique = Techniques.FirstOrDefault(t => string.Equals(t.Key, step.Utility?.Technique, StringComparison.Ordinal))
                    ?? Techniques.FirstOrDefault();

        OnPropertyChanged(nameof(Number));
        RaiseShown();
    }

    // Rebuilt only when the number of lines or their kind changes, so a field keeps focus across its own edit.
    private void LoadLines(StratStep step)
    {
        bool stored = StratStepLines.HasLines(step);
        ExpandsAll = !ShowCompact && !stored && string.Equals(step.Actor, StratVocabulary.ActorAll, StringComparison.Ordinal);
        IReadOnlyList<StepAssignment> lines = ExpandsAll ? StratLinePatches.Copy(step, true) : StratStepLines.Of(step);
        // Five lines read from a step for everyone and the five stored lines an edit of one makes are the same rows.
        bool isImplicit = !stored && !ExpandsAll;
        HasStoredLines = stored;
        if (Lines.Count != lines.Count || _linesImplicit != isImplicit)
        {
            _linesImplicit = isImplicit;
            Lines.Clear();
            for (int j = 0; j < lines.Count; j++)
            {
                Lines.Add(new StratLineRow(this));
            }
        }

        for (int j = 0; j < lines.Count; j++)
        {
            string own = lines[j].Slot;
            IReadOnlyList<string> options =
            [
                .. StratVocabulary.Slots.Where(s => s == own || lines.All(l => !string.Equals(l.Slot, s, StringComparison.Ordinal)))
            ];
            Lines[j].Load(j, lines[j], isImplicit, Verb, options);
        }

        SelectLine(_lineSlot);
        OnPropertyChanged(nameof(HasStoredLines));
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(CanEditActor));
        OnPropertyChanged(nameof(CanAddLine));
    }

    // Who, and the one place and watching the lines share while they agree. A step for everyone reads as five lines.
    private void LoadGroup(StratStep step)
    {
        List<StepAssignment> expanded = StratLinePatches.Copy(step, true);
        _players = [.. expanded.Select(l => l.Slot)];
        PlayerCount = expanded.Count;
        _agree = StratLinePatches.Agree(expanded);
        _split = _split is null ? !_agree : _split.Value || !_agree;
        WhoText = PlayerCount == StratVocabulary.Slots.Count || PlayerCount == 0
            ? StratVocabulary.ActorAll
            : string.Join(", ", StratVocabulary.Slots.Where(s => expanded.Exists(l => l.Slot == s)));

        StepAssignment? shared = _agree ? expanded.FirstOrDefault() : null;
        GroupPlaceValue = StratLocations.IsSet(shared?.To) ? [StratLocations.Clone(shared!.To!)] : [];
        GroupPlaceText = _owner.DisplayLocations(GroupPlaceValue);
        GroupWatchValue = StratLocations.Watched(shared?.Watch);
        GroupWatchText = _owner.DisplayLocations(GroupWatchValue);
        HasGroupAngle = shared?.Watch?.YawDegrees is not null;
        GroupAngleText = shared?.Watch?.YawDegrees is { } yaw ? yaw.ToString("0", CultureInfo.InvariantCulture) + "°" : "";
        RefreshView();
        foreach (string name in (string[])
                 [nameof(WhoText), nameof(PlayerCount), nameof(HasGroupAngle), nameof(GroupAngleText), nameof(ShowGroupPlace), nameof(ShowGroupWatch)])
        {
            OnPropertyChanged(name);
        }
    }

    private void LoadLurk(StratStep step)
    {
        StepLurk? lurk = step.Lurk;
        _hasLurk = lurk is not null;
        LurkAreasValue = StratLocations.LurkAreas(lurk);
        LurkAreasText = _owner.DisplayLocations(LurkAreasValue);
        RotateAtText = lurk?.Rotate?.AtSeconds is { } at ? StratClock.Format(at) : "";
        RotateWhenText = lurk?.Rotate?.When ?? "";
        RotateToValue = StratLocations.IsSet(lurk?.Rotate?.To) ? [StratLocations.Clone(lurk!.Rotate!.To!)] : [];
        RotateToText = _owner.DisplayLocations(RotateToValue);
        OnPropertyChanged(nameof(ShowLurk));
    }

    partial void OnGroupPlaceTextChanged(string value)
    {
        RaiseShown();
        if (_owner.IsProjecting)
        {
            return;
        }

        _owner.WriteLocation(GroupPlaceTarget, _owner.ParseLocations(value, GroupPlaceValue, false));
    }

    partial void OnGroupPlaceValueChanged(IReadOnlyList<PlaceRef> value) => _owner.WriteLocation(GroupPlaceTarget, value);

    partial void OnGroupWatchValueChanged(IReadOnlyList<PlaceRef> value) => _owner.WriteLocation(GroupWatchTarget, value);

    partial void OnFromValueChanged(IReadOnlyList<PlaceRef> value) => _owner.WriteLocation(FromTarget, value);

    partial void OnLurkAreasValueChanged(IReadOnlyList<PlaceRef> value) => _owner.WriteLocation(LurkAreasTarget, value);

    partial void OnRotateToValueChanged(IReadOnlyList<PlaceRef> value) => _owner.WriteLocation(RotateToTarget, value);

    partial void OnLandingValueChanged(IReadOnlyList<PlaceRef> value) => WriteLanding(value);

    partial void OnGroupWatchTextChanged(string value)
    {
        RaiseShown();
        if (_owner.IsProjecting)
        {
            return;
        }

        _owner.WriteLocation(GroupWatchTarget, _owner.ParseLocations(value, GroupWatchValue, true));
    }

    partial void OnLurkAreasTextChanged(string value)
    {
        if (_owner.IsProjecting)
        {
            return;
        }

        _owner.WriteLocation(LurkAreasTarget, _owner.ParseLocations(value, LurkAreasValue, true));
    }

    partial void OnRotateAtTextChanged(string value)
    {
        if (_owner.IsProjecting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            _owner.EditLurk(_index, lurk => (lurk.Rotate ??= new LurkRotate()).AtSeconds = null);
        }
        else if (StratClock.TryParse(value, out double at))
        {
            _owner.EditLurk(_index, lurk => (lurk.Rotate ??= new LurkRotate()).AtSeconds = at);
        }
        else
        {
            // Not a time: show the stored one again.
            _owner.Project();
        }
    }

    partial void OnRotateWhenTextChanged(string value)
    {
        if (!_owner.IsProjecting)
        {
            _owner.EditLurk(_index, lurk => (lurk.Rotate ??= new LurkRotate()).When = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        }
    }

    partial void OnRotateToTextChanged(string value)
    {
        if (_owner.IsProjecting)
        {
            return;
        }

        _owner.WriteLocation(RotateToTarget, _owner.ParseLocations(value, RotateToValue, false));
    }

    /// <summary>Marks the selected line; null or a slot with no line marks none.</summary>
    /// <param name="slot">The selected line's slot.</param>
    internal void SelectLine(string? slot)
    {
        _lineSlot = slot;
        foreach (StratLineRow line in Lines)
        {
            line.IsSelected = IsSelected && slot is not null && string.Equals(line.Slot, slot, StringComparison.Ordinal);
        }
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items))
        {
            return;
        }

        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }

    private void RaiseShown()
    {
        OnPropertyChanged(nameof(ShowTechnique));
        OnPropertyChanged(nameof(ShowFrom));
        OnPropertyChanged(nameof(ShowTo));
        OnPropertyChanged(nameof(IsStrayTo));
        OnPropertyChanged(nameof(ShowUtility));
        OnPropertyChanged(nameof(ShowLineup));
        OnPropertyChanged(nameof(ShowLanding));
        OnPropertyChanged(nameof(ToLabel));
        OnPropertyChanged(nameof(ShowGroupPlace));
        OnPropertyChanged(nameof(ShowGroupWatch));
        OnPropertyChanged(nameof(GroupPlaceSpan));
        OnPropertyChanged(nameof(GroupWatchColumn));
        OnPropertyChanged(nameof(GroupWatchSpan));
        OnPropertyChanged(nameof(CanSplit));
        OnPropertyChanged(nameof(ShowLurk));
    }

    private string Path(string field) => string.Create(CultureInfo.InvariantCulture, $"/steps/{_index}/{field}");

    partial void OnTimeTextChanged(string value)
    {
        if (_owner.IsProjecting)
        {
            return;
        }

        if (StratClock.TryParse(value, out double atSeconds))
        {
            _owner.Replace(Path("atSeconds"), JsonValue.Create(atSeconds));
        }
        else
        {
            // Not a time: the table shows the stored one again rather than keeping text that means nothing.
            _owner.Project();
        }
    }

    partial void OnActorChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _owner.Replace(Path("actor"), JsonValue.Create(value));
        }
    }

    partial void OnVerbChanged(string value)
    {
        RaiseShown();
        if (!string.IsNullOrEmpty(value))
        {
            _owner.ChangeVerb(_index, value);
        }
    }

    partial void OnFromTextChanged(string value)
    {
        RaiseShown();
        _owner.WriteLocation(FromTarget, _owner.ParseLocations(value, FromValue, false));
    }

    // Beside stored lines the step's own to is not used: the field only shows it for clearing.
    partial void OnToTextChanged(string value)
    {
        if (HasStoredLines && !_owner.IsProjecting)
        {
            _owner.Project();
            return;
        }

        RaiseShown();
        _owner.WriteLocation(ToTarget, _owner.ParseLocations(value, ToValue, false));
    }

    /// <summary>A step-level to beside stored lines: shown read-only, with a clear that writes the lines' shape.</summary>
    public bool IsStrayTo => HasStoredLines && ToText.Length > 0;

    /// <summary>Removes a step-level to that stored lines make unused, through the lines writer.</summary>
    [RelayCommand]
    private void ClearStrayTo() => _owner.EditLines(_index, (_, _) => { });

    partial void OnUtilityKindChanged(string value)
    {
        OnPropertyChanged(nameof(HasUtility));
        RaiseShown();
        if (!string.IsNullOrEmpty(value))
        {
            _owner.ChangeUtilityKind(_index, value);
        }
    }

    partial void OnLandingTextChanged(string value)
    {
        RaiseShown();
        WriteLanding(_owner.ParseLocations(value, LandingValue, false));
    }

    // A landing keeps its captured point when the place is retyped or cleared; clearing a landing that is only a
    // point removes it. Other location fields drop a point under another place.
    private void WriteLanding(IReadOnlyList<PlaceRef> value)
    {
        if (_owner.IsProjecting || UtilityKind == StratEditorViewModel.None)
        {
            return;
        }

        PlaceRef? stored = LandingValue.Count > 0 ? LandingValue[0] : null;
        PlaceRef? next = value.Count > 0 ? StratLocations.Clone(value[0]) : null;
        if (stored is not null && StratLocations.HasPoint(stored) && (next is not null || StratLocations.HasPlace(stored)))
        {
            next ??= new PlaceRef { Extra = stored.Extra };
            if (!StratLocations.HasPoint(next))
            {
                next.X = stored.X;
                next.Y = stored.Y;
                next.LevelMinZ = stored.LevelMinZ;
            }
        }

        _owner.WriteLocation(LandingTarget, next is null ? [] : [next]);
    }

    // Guarded the way the pin combo is (OnPinChanged above): replacing LineupOptions while a row is
    // selected can push the combo's own selection to null as its items reset, which is not the user
    // clearing the lineup, so a null value writes nothing rather than the row's own StratLineupOption.None.
    partial void OnLineupChanged(StratLineupOption? value)
    {
        RaiseShown();
        if (value is not null && UtilityKind != StratEditorViewModel.None)
        {
            _owner.ChangeLineup(_index, value.Id);
        }
    }

    // Guarded like the lineup combo: resetting the items pushes null, which is not the user's choice.
    partial void OnTechniqueChanged(StratTechniqueOption? value)
    {
        if (value is not null && UtilityKind != StratEditorViewModel.None)
        {
            _owner.ChangeTechnique(_index, value.Key, Techniques.FirstOrDefault()?.Key);
        }
    }

    partial void OnNoteChanged(string value) =>
        _owner.Replace(Path("note"), string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value));
}

/// <summary>
///     One line of a step row: a slot, its place and what it watches. The implicit line of a one-player step edits
///     the step's actor and to; a stored line edits <c>assignments</c>. Either way through
///     <see cref="StratLinePatches" />, which keeps the stored shape.
/// </summary>
public sealed partial class StratLineRow : ObservableObject
{
    private readonly StratStepRow _row;
    private bool _loading;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _placeText = "";

    [ObservableProperty]
    private string? _slot;

    [ObservableProperty]
    private string _watchText = "";

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _placeValue = [];

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _watchValue = [];

    internal StratLineRow(StratStepRow row) => _row = row;

    /// <summary>The owner's words for the location fields' lists.</summary>
    public CalloutResolver Callouts => _row.Owner.Places;

    /// <summary>The level a typed coordinate takes in a field with no point.</summary>
    public double? CurrentLevelMinZ => _row.Owner.DefaultLevelMinZ;

    /// <summary>What the line's place field writes and picks: this slot's <c>to</c>.</summary>
    public StratLocationField PlaceTarget => new(_row.Id, Slot, StratLocationKind.To);

    /// <summary>What the line's watching field writes and picks.</summary>
    public StratLocationField WatchTarget => new(_row.Id, Slot, StratLocationKind.Watch);

    /// <summary>The step row this line is on.</summary>
    public StratStepRow Row => _row;

    /// <summary>The line's index in the step's lines.</summary>
    public int Index { get; private set; }

    /// <summary>A one-player step's line: its slot is the step's actor, changed with the actor combo.</summary>
    public bool IsImplicit { get; private set; }

    public bool IsExplicit => !IsImplicit;

    /// <summary>The slots this line may take: its own and those with no line on the step.</summary>
    public ObservableCollection<string> SlotOptions { get; } = [];

    /// <summary>What the verb calls the place: to, at or site.</summary>
    public string PlaceLabel { get; private set; } = "to";

    /// <summary>The verb uses a place, or this line holds one.</summary>
    public bool ShowPlace { get; private set; }

    /// <summary>The verb uses watching, or this line holds a watch.</summary>
    public bool ShowWatch { get; private set; }

    /// <summary>An explicit view angle is set: the cone points there, not at the first watched place.</summary>
    public bool HasAngle { get; private set; }

    public string AngleText { get; private set; } = "";

    public StratFieldIssue? SlotIssue { get; private set; }

    public StratFieldIssue? PlaceIssue { get; private set; }

    public StratFieldIssue? WatchIssue { get; private set; }

    internal void Load(int index, StepAssignment line, bool isImplicit, string verb, IReadOnlyList<string> slotOptions)
    {
        _loading = true;
        try
        {
            Index = index;
            IsImplicit = isImplicit;
            if (!SlotOptions.SequenceEqual(slotOptions))
            {
                SlotOptions.Clear();
                foreach (string option in slotOptions)
                {
                    SlotOptions.Add(option);
                }
            }

            Slot = line.Slot;
            PlaceValue = StratLocations.IsSet(line.To) ? [StratLocations.Clone(line.To!)] : [];
            PlaceText = _row.Owner.DisplayLocations(PlaceValue);
            WatchValue = StratLocations.Watched(line.Watch);
            WatchText = _row.Owner.DisplayLocations(WatchValue);
            PlaceLabel = StratStepFields.ToLabel(verb);
            ShowPlace = StratStepFields.Uses(verb, StratStepField.To) || PlaceText.Length > 0;
            ShowWatch = StratStepFields.Uses(verb, StratStepField.Watch) || WatchText.Length > 0 || line.Watch?.YawDegrees is not null;
            HasAngle = line.Watch?.YawDegrees is not null;
            AngleText = line.Watch?.YawDegrees is { } yaw ? yaw.ToString("0", CultureInfo.InvariantCulture) + "°" : "";
        }
        finally
        {
            _loading = false;
        }

        foreach (string name in (string[])
                 [
                     nameof(Index), nameof(IsImplicit), nameof(IsExplicit), nameof(PlaceLabel), nameof(ShowPlace), nameof(ShowWatch), nameof(HasAngle),
                     nameof(AngleText), nameof(Callouts), nameof(PlaceTarget), nameof(WatchTarget)
                 ])
        {
            OnPropertyChanged(name);
        }
    }

    internal void SetIssues(IReadOnlyList<(StratIssue Issue, string Member)> issues)
    {
        Dictionary<string, List<StratIssue>> byField = new(StringComparer.Ordinal);
        foreach ((StratIssue issue, string member) in issues)
        {
            string field = member switch
            {
                "to" => nameof(PlaceIssue),
                "watch" => nameof(WatchIssue),
                _ => nameof(SlotIssue)
            };
            if (!byField.TryGetValue(field, out List<StratIssue>? list))
            {
                byField[field] = list = [];
            }

            list.Add(issue);
        }

        SlotIssue = StratFieldIssue.From(byField, nameof(SlotIssue));
        PlaceIssue = StratFieldIssue.From(byField, nameof(PlaceIssue));
        WatchIssue = StratFieldIssue.From(byField, nameof(WatchIssue));
        OnPropertyChanged(nameof(SlotIssue));
        OnPropertyChanged(nameof(PlaceIssue));
        OnPropertyChanged(nameof(WatchIssue));
    }

    internal string LabelOf(string member) => member switch
    {
        "to" => PlaceLabel,
        "watch" => "watching",
        _ => "slot"
    };

    [RelayCommand]
    private void Remove() => _row.Owner.RemoveLine(_row, Index);

    /// <summary>Drops the explicit angle: the token faces its first watched place again.</summary>
    [RelayCommand]
    private void ClearAngle() => _row.Owner.EditLines(_row.Index, (_, lines) =>
    {
        if (Index < lines.Count && lines[Index].Watch is { } watch)
        {
            watch.YawDegrees = null;
        }
    }, _row.ExpandsAll);

    // Guarded like the pin combo: replacing SlotOptions pushes null, which is not the user's choice.
    partial void OnSlotChanged(string? value)
    {
        if (!_loading && value is not null)
        {
            _row.Owner.ChangeLineSlot(_row.Index, Index, value, _row.ExpandsAll);
        }
    }

    partial void OnPlaceTextChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        WritePlace(_row.Owner.ParseLocations(value, PlaceValue, false));
    }

    partial void OnPlaceValueChanged(IReadOnlyList<PlaceRef> value)
    {
        if (!_loading)
        {
            WritePlace(value);
        }
    }

    partial void OnWatchValueChanged(IReadOnlyList<PlaceRef> value)
    {
        if (!_loading)
        {
            WriteWatch(value);
        }
    }

    private void WritePlace(IReadOnlyList<PlaceRef> value)
    {
        if (Slot is not null)
        {
            _row.Owner.WriteLocation(PlaceTarget, value);
        }
    }

    private void WriteWatch(IReadOnlyList<PlaceRef> value)
    {
        if (Slot is not null)
        {
            _row.Owner.WriteLocation(WatchTarget, value);
        }
    }

    partial void OnWatchTextChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        WriteWatch(_row.Owner.ParseLocations(value, WatchValue, true));
    }

}

/// <summary>One slot's toggle in a step row's Who flyout.</summary>
public sealed partial class StratWhoOption(string slot) : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public string Slot { get; } = slot;
}

/// <summary>One branch: after which step, on what condition, continuing where (§3.3.4).</summary>
public sealed partial class StratBranchRow : ObservableObject
{
    private readonly StratEditorViewModel _owner;

    [ObservableProperty]
    private StratStepOption? _afterStep;

    [ObservableProperty]
    private string _conditionText = "";

    private int _index;

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private StratTargetOption? _targetStrat;

    [ObservableProperty]
    private StratStepOption? _targetStep;

    internal StratBranchRow(StratEditorViewModel owner, Guid id)
    {
        _owner = owner;
        Id = id;
    }

    public Guid Id { get; }

    /// <summary>Where the branch can continue: the target's start, or any step when the target is this strat.</summary>
    public ObservableCollection<StratStepOption> TargetStepOptions { get; } = [];

    internal void Load(int index, StratBranch branch, StratTargetOption target, IReadOnlyList<StratStepOption> targetSteps)
    {
        _index = index;
        if (!TargetStepOptions.SequenceEqual(targetSteps))
        {
            TargetStepOptions.Clear();
            foreach (StratStepOption option in targetSteps)
            {
                TargetStepOptions.Add(option);
            }
        }

        AfterStep = _owner.StepOptions.FirstOrDefault(o => o.Id == branch.AfterStepId);
        ConditionText = branch.Condition.Text;
        TargetStrat = target;
        TargetStep = TargetStepOptions.FirstOrDefault(o => o.Id == branch.Target.StepId) ?? StratStepOption.Start;
        Note = branch.Note ?? "";
    }

    private string Path(string field) => string.Create(CultureInfo.InvariantCulture, $"/branches/{_index}/{field}");

    partial void OnAfterStepChanged(StratStepOption? value)
    {
        if (value?.Id is { } id)
        {
            _owner.Replace(Path("afterStepId"), JsonValue.Create(id));
        }
    }

    partial void OnConditionTextChanged(string value) => _owner.Replace(Path("condition/text"), JsonValue.Create(value));

    // A new target strat starts at its beginning: a step id from the old target means nothing in the new one.
    partial void OnTargetStratChanged(StratTargetOption? value)
    {
        if (value is not null)
        {
            _owner.Apply([
                PatchOp.ReplaceOp(Path("target/stratId"), null, JsonValue.Create(value.Id)),
                PatchOp.ReplaceOp(Path("target/stepId"), null, null)
            ]);
        }
    }

    partial void OnTargetStepChanged(StratStepOption? value)
    {
        if (value is not null)
        {
            _owner.Replace(Path("target/stepId"), value.Id is { } id ? JsonValue.Create(id) : null);
        }
    }

    partial void OnNoteChanged(string value) =>
        _owner.Replace(Path("note"), string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value));
}
