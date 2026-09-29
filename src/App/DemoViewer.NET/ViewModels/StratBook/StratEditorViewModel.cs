#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly StratSession _session;
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
    public StratEditorViewModel(StratSession session, IStratLineupCatalog? lineups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _lineups = lineups;
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

        List<PatchOp> ops = [PatchOp.ReplaceOp(path + "/verb", null, JsonValue.Create(verb))];
        if (start.From is not null && !StratStepFields.Uses(verb, StratStepField.From))
        {
            ops.Add(PatchOp.RemoveOp(path + "/from", null));
        }

        if (start.To is not null && !StratStepFields.Uses(verb, StratStepField.To))
        {
            ops.Add(PatchOp.RemoveOp(path + "/to", null));
        }

        if (start.Utility is not null && !StratStepFields.Uses(verb, StratStepField.Utility))
        {
            ops.Add(PatchOp.RemoveOp(path + "/utility", null));
        }

        return ops;
    });

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

    /// <summary>
    ///     A new step at the end, at the last step's time so the clock still counts down, or at the round's start
    ///     for the first; every slot, a move.
    /// </summary>
    [RelayCommand]
    private void AddStep()
    {
        if (_session.Document is not { } document)
        {
            return;
        }

        StratStep step = new()
        {
            Id = Guid.NewGuid(),
            AtSeconds = document.Steps.Count > 0 ? document.Steps[^1].AtSeconds : document.Clock.RoundSeconds,
            Actor = StratVocabulary.ActorAll,
            Verb = "move"
        };
        Apply([PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep))]);
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

        Issues.Clear();
        foreach (StratIssue issue in _session.Issues)
        {
            Issues.Add($"{issue.Severity.ToString().ToLowerInvariant()}: {issue.Message}"
                       + (issue.Field.Length > 0 ? $" ({issue.Field})" : ""));
        }
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
        Invariant($"{index + 1} · {StratClock.Format(step.AtSeconds)} {step.Actor} {step.Verb}");

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

    private bool _hasLanding;

    private bool _landingHasPoint;

    private int _index;

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

    internal StratStepRow(StratEditorViewModel owner, Guid id)
    {
        _owner = owner;
        Id = id;
    }

    public Guid Id { get; }

    public int Number => _index + 1;

    public bool HasUtility => UtilityKind != StratEditorViewModel.None;

    // A member the verb does not use still shows while it holds a value, so nothing an export prints is hidden.
    public bool ShowFrom => StratStepFields.Uses(Verb, StratStepField.From) || FromText.Length > 0;

    public bool ShowTo => StratStepFields.Uses(Verb, StratStepField.To) || ToText.Length > 0;

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

    internal void Load(int index, StratStep step)
    {
        _index = index;
        TimeText = StratClock.Format(step.AtSeconds);
        Actor = step.Actor;
        Verb = step.Verb;
        FromText = _owner.DisplayPlace(step.From?.Place);
        ToText = _owner.DisplayPlace(step.To?.Place);
        UtilityKind = step.Utility?.Kind ?? StratEditorViewModel.None;
        _hasLanding = step.Utility?.Landing is not null;
        _landingHasPoint = step.Utility?.Landing is { } landing
                           && (landing.X is not null || landing.Y is not null || landing.LevelMinZ is not null || landing.Extra is { Count: > 0 });
        LandingText = _owner.DisplayPlace(step.Utility?.Landing?.Place);
        Note = step.Note ?? "";

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
        OnPropertyChanged(nameof(ShowUtility));
        OnPropertyChanged(nameof(ShowLineup));
        OnPropertyChanged(nameof(ShowLanding));
        OnPropertyChanged(nameof(ToLabel));
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

    partial void OnFromTextChanged(string value) => ReplacePlace("from", value);

    partial void OnToTextChanged(string value) => ReplacePlace("to", value);

    // A cleared place drops the reference, which the file then omits; what does not resolve is stored as typed.
    private void ReplacePlace(string field, string text)
    {
        RaiseShown();
        string? place = _owner.ResolvePlace(text);
        _owner.Replace(Path(field), place is null ? null : new JsonObject { ["place"] = place });
    }

    partial void OnUtilityKindChanged(string value)
    {
        OnPropertyChanged(nameof(HasUtility));
        RaiseShown();
        if (!string.IsNullOrEmpty(value))
        {
            _owner.ChangeUtilityKind(_index, value);
        }
    }

    // Only the place: a captured landing's X, Y and level stay when the place is retyped or cleared.
    partial void OnLandingTextChanged(string value)
    {
        RaiseShown();
        if (UtilityKind == StratEditorViewModel.None)
        {
            return;
        }

        string? place = _owner.ResolvePlace(value);
        if (_hasLanding && place is null && !_landingHasPoint)
        {
            _owner.Replace(Path("utility") + "/landing", null);
        }
        else if (_hasLanding)
        {
            _owner.Replace(Path("utility") + "/landing/place", place is null ? null : JsonValue.Create(place));
        }
        else if (place is not null)
        {
            _owner.Replace(Path("utility") + "/landing", new JsonObject { ["place"] = place });
        }
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
