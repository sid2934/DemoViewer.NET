#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.ViewModels.Dossier;

/// <summary>
///     One generated Dossier line before the user's edits: its stable key, its section, the generated
///     text, and, for a Setup Heatmap, where to read its rendered picture at export time.
/// </summary>
/// <param name="Key">Section slug plus what the line counts; never an index.</param>
/// <param name="Section">The section heading it prints under.</param>
/// <param name="Generated">The generated text.</param>
/// <param name="Image">Reads the line's PNG when it is exported; null for a text line.</param>
public sealed record DossierFindingSource(string Key, string Section, string Generated, Func<byte[]?>? Image = null);

/// <summary>
///     Dossier Editing And Export (plan.md §3, Phase 5): every built section's numbers as findings, the
///     long form being the working document and the starred findings assembling the one-pager. Every
///     line is editable (rewritten, reset, left out), the user adds notes and a summary beside them, and
///     the export writes either form as self-contained HTML or Markdown, the short form by default.
///     <para>
///         <b>No verdicts.</b> A finding is a count over its stated sample (<see cref="CountText" />); the
///         section adds no rating, no grade and no win probability, and neither does the export.
///     </para>
///     <para>
///         <b>Edits live in <see cref="DossierNotesStore" /> by key.</b> <see cref="Load" /> is called
///         whenever a section lands, rebuilding the rows from the sources and the stored stars and edits,
///         so a star on a line survives the section rebuilding under it. Row actions update the row in
///         place and write the store; this VM is the store's only writer.
///     </para>
/// </summary>
public sealed partial class DossierEditorViewModel : ObservableObject
{
    public const string MapPoolSection = "Map Pool Record";
    public const string HeatmapSection = "Setup Heatmaps By Buy";
    public const string OpeningsSection = "Opening Tendencies";
    public const string PostPlantSection = "Post-Plant And Retake";
    public const string SituationalSection = "Situational Behaviour";
    public const string PeriodDiffSection = "Period Diff";
    public const string VetoSection = "Veto history";
    public const string NotesSection = "Notes";

    private readonly Func<string, string, string, string?> _export;
    private readonly List<DossierFindingViewModel> _rows = [];
    private readonly DossierNotesStore _store;
    private bool _loading;
    private string _sampleLine = "";
    private IReadOnlyList<DossierFindingSource> _sources = [];
    private Guid? _teamId;
    private string _teamName = "";

    /// <summary>"one-pager written: falcons-one-pager.html", or why not; empty until an export.</summary>
    [ObservableProperty]
    private string _exportLine = "";

    /// <summary>The form Export writes. The short form, the one-pager, is the default.</summary>
    [ObservableProperty]
    private DossierForm _exportForm = DossierForm.OnePager;

    /// <summary>The file Export writes: HTML by default, the LAN Print route.</summary>
    [ObservableProperty]
    private DossierFormat _exportFormat = DossierFormat.Html;

    [ObservableProperty]
    private string _newNoteText = "";

    /// <summary>Shows only the starred lines: the one-pager as it will print.</summary>
    [ObservableProperty]
    private bool _showStarredOnly;

    /// <summary>The user's paragraph at the top of both forms.</summary>
    [ObservableProperty]
    private string _summary = "";

    /// <param name="store">Where stars, edits, notes and the summary persist.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; Export has no file to write there.</param>
    /// <param name="export">Writes (text, stem, extension) and opens it, returning the path or null; the temp-file writer when null.</param>
    public DossierEditorViewModel(DossierNotesStore store, bool isBrowser = false, Func<string, string, string, string?>? export = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        IsBrowser = isBrowser;
        _export = export ?? DossierExportWriter.WriteAndOpen;
    }

    public bool IsBrowser { get; }

    /// <summary>The rows shown: every line not left out, or only the starred ones.</summary>
    public ObservableCollection<DossierFindingViewModel> Findings { get; } = [];

    /// <summary>The two forms, worded for the picker; the one-pager first, as the default.</summary>
    public static IReadOnlyList<DossierExportOption<DossierForm>> FormOptions { get; } =
    [
        new(DossierForm.OnePager, "one-pager"),
        new(DossierForm.LongForm, "long form")
    ];

    /// <summary>The two files, worded for the picker; HTML first, as the default.</summary>
    public static IReadOnlyList<DossierExportOption<DossierFormat>> FormatOptions { get; } =
    [
        new(DossierFormat.Html, "HTML"),
        new(DossierFormat.Markdown, "Markdown")
    ];

    /// <summary>The picker's row for <see cref="ExportForm" />.</summary>
    public DossierExportOption<DossierForm> SelectedFormOption
    {
        get => FormOptions.First(o => o.Value == ExportForm);
        set => ExportForm = value?.Value ?? DossierForm.OnePager;
    }

    /// <summary>The picker's row for <see cref="ExportFormat" />.</summary>
    public DossierExportOption<DossierFormat> SelectedFormatOption
    {
        get => FormatOptions.First(o => o.Value == ExportFormat);
        set => ExportFormat = value?.Value ?? DossierFormat.Html;
    }

    public bool HasTeam => _teamId is not null;

    public bool HasFindings => Findings.Count > 0;

    public int StarredCount => _rows.Count(r => r.IsStarred && !r.IsHidden);

    public int HiddenCount => _rows.Count(r => r.IsHidden);

    public bool HasHidden => HiddenCount > 0;

    /// <summary>"42 findings · 5 starred · 2 left out".</summary>
    public string CountsLine
    {
        get
        {
            int shown = _rows.Count(r => !r.IsHidden);
            List<string> parts = [Plural(shown, "finding"), $"{StarredCount} starred"];
            if (HiddenCount > 0)
            {
                parts.Add($"{HiddenCount} left out");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>What the Findings list says when it is empty.</summary>
    public string EmptyLine => ShowStarredOnly
        ? DossierExporter.NothingStarredLine
        : "no findings yet: the sections are still building, or have nothing to count";

    public bool CanExport => HasTeam && !IsBrowser;

    /// <summary>What the Export slot says when there is no file to write.</summary>
    public string ExportUnavailableNote => IsBrowser ? "Export dossier: unavailable in the browser; copy the markdown instead" : "";

    public bool HasExportUnavailableNote => ExportUnavailableNote.Length > 0;

    public bool NotesAreSessionOnly => _store.IsSessionOnly;

    /// <summary>The words on the Export button for the chosen form.</summary>
    public string ExportLabel => ExportForm == DossierForm.OnePager ? "Export one-pager" : "Export long form";

    /// <summary>
    ///     Rebuilds the rows for a team from the sections' current lines and the stored edits, or clears
    ///     them when <paramref name="teamId" /> is null.
    /// </summary>
    /// <param name="teamId">The team, or null.</param>
    /// <param name="teamName">Its display name, the export's title.</param>
    /// <param name="sampleLine">The Map Pool Record's sample line, printed under the title.</param>
    /// <param name="sources">Every section's generated lines, in print order.</param>
    public void Load(Guid? teamId, string teamName, string sampleLine, IReadOnlyList<DossierFindingSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        bool teamChanged = teamId != _teamId;
        _teamId = teamId;
        _teamName = teamName;
        _sampleLine = sampleLine;
        _sources = sources;
        if (teamChanged)
        {
            ExportLine = "";
            NewNoteText = "";
        }

        Rebuild();
        ExportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasTeam));
        OnPropertyChanged(nameof(CanExport));
    }

    /// <summary>The document as it will print, with the user's edits applied and left-out lines dropped.</summary>
    public DossierDocument Document() => new()
    {
        TeamName = _teamName,
        SampleLine = _sampleLine,
        Summary = Summary.Trim(),
        Findings =
        [
            .. _rows.Where(r => !r.IsHidden).Select(r => new DossierFinding
            {
                Key = r.Key,
                Section = r.Section,
                Text = r.Text,
                IsStarred = r.IsStarred,
                ImagePng = r.Image?.Invoke()
            })
        ]
    };

    /// <summary>The chosen form as Markdown, for the Copy button.</summary>
    public string MarkdownText() => DossierExporter.Markdown(Document(), ExportForm);

    /// <summary>The line the Copy button leaves once the clipboard took the text.</summary>
    public void ReportCopied() => ExportLine = ExportForm == DossierForm.OnePager ? "one-pager copied as markdown" : "long form copied as markdown";

    /// <summary>The Copy button's fallback when the host refused the clipboard.</summary>
    public void ReportCopyFailed() => ExportLine = "could not reach the clipboard";

    partial void OnSummaryChanged(string value)
    {
        if (!_loading && _teamId is { } id)
        {
            _store.SetSummary(id, value);
        }
    }

    partial void OnShowStarredOnlyChanged(bool value) => Project();

    partial void OnExportFormChanged(DossierForm value)
    {
        OnPropertyChanged(nameof(ExportLabel));
        OnPropertyChanged(nameof(SelectedFormOption));
    }

    partial void OnExportFormatChanged(DossierFormat value) => OnPropertyChanged(nameof(SelectedFormatOption));

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        if (!CanExport)
        {
            return;
        }

        DossierDocument doc = Document();
        (string text, string extension) = ExportFormat == DossierFormat.Html
            ? (DossierExporter.Html(doc, ExportForm), ".html")
            : (DossierExporter.Markdown(doc, ExportForm), ".md");
        string stem = StratBookTabViewModel.ExportFileStem(_teamName) + (ExportForm == DossierForm.OnePager ? "-one-pager" : "-dossier");
        string? path;
        try
        {
            path = _export(text, stem, extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportLine = $"could not write the export: {ex.Message}";
            return;
        }

        ExportLine = path is null
            ? "could not open the export"
            : $"{(ExportForm == DossierForm.OnePager ? "one-pager" : "long form")} written: {Path.GetFileName(path)}";
    }

    [RelayCommand]
    private void ToggleStar(DossierFindingViewModel? row)
    {
        if (row is null || _teamId is not { } id)
        {
            return;
        }

        row.IsStarred = !row.IsStarred;
        _store.SetStarred(id, row.Key, row.IsStarred);
        if (ShowStarredOnly)
        {
            Project();
        }
        else
        {
            RaiseCounts();
        }
    }

    [RelayCommand]
    private void CommitEdit(DossierFindingViewModel? row)
    {
        if (row is null || _teamId is not { } id)
        {
            return;
        }

        if (row.IsNote)
        {
            _store.EditNote(id, row.Key, row.EditText);
            row.Text = _store.For(id).Notes.FirstOrDefault(n => DossierNotesStore.NoteKey(n.Id) == row.Key)?.Text ?? row.Text;
        }
        else
        {
            _store.SetEdit(id, row.Key, row.EditText, row.Generated);
            row.Text = _store.For(id).Edits.TryGetValue(row.Key, out string? edit) ? edit : row.Generated;
        }

        row.IsEditing = false;
    }

    /// <summary>Puts a rewritten line back to its generated text.</summary>
    [RelayCommand]
    private void ResetEdit(DossierFindingViewModel? row)
    {
        if (row is null || row.IsNote || _teamId is not { } id)
        {
            return;
        }

        _store.SetEdit(id, row.Key, null, row.Generated);
        row.Text = row.Generated;
        row.IsEditing = false;
    }

    /// <summary>Leaves a generated line out of both forms; removes a note.</summary>
    [RelayCommand]
    private void Remove(DossierFindingViewModel? row)
    {
        if (row is null || _teamId is not { } id)
        {
            return;
        }

        if (row.IsNote)
        {
            _store.RemoveNote(id, row.Key);
            _rows.Remove(row);
        }
        else
        {
            _store.SetHidden(id, row.Key, true);
            row.IsHidden = true;
        }

        Project();
    }

    [RelayCommand]
    private void RestoreHidden()
    {
        if (_teamId is not { } id)
        {
            return;
        }

        _store.ClearHidden(id);
        foreach (DossierFindingViewModel row in _rows)
        {
            row.IsHidden = false;
        }

        Project();
    }

    [RelayCommand]
    private void AddNote()
    {
        if (_teamId is not { } id || _store.AddNote(id, NewNoteText) is null)
        {
            return;
        }

        NewNoteText = "";
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Clear();
        _loading = true;
        try
        {
            if (_teamId is not { } id)
            {
                Summary = "";
            }
            else
            {
                DossierTeamNotes notes = _store.For(id);
                HashSet<string> starred = new(notes.Starred, StringComparer.Ordinal);
                HashSet<string> hidden = new(notes.Hidden, StringComparer.Ordinal);
                foreach (DossierFindingSource source in _sources)
                {
                    string text = notes.Edits.TryGetValue(source.Key, out string? edit) ? edit : source.Generated;
                    _rows.Add(new DossierFindingViewModel(source.Key, source.Section, source.Generated, text, false, source.Image)
                    {
                        IsStarred = starred.Contains(source.Key),
                        IsHidden = hidden.Contains(source.Key)
                    });
                }

                foreach (DossierNote note in notes.Notes)
                {
                    string key = DossierNotesStore.NoteKey(note.Id);
                    _rows.Add(new DossierFindingViewModel(key, NotesSection, note.Text, note.Text, true, null)
                    {
                        IsStarred = starred.Contains(key)
                    });
                }

                Summary = notes.Summary;
            }
        }
        finally
        {
            _loading = false;
        }

        Project();
    }

    // The shown rows from the full set, with a section heading on the first row of each section.
    private void Project()
    {
        Findings.Clear();
        string? section = null;
        foreach (DossierFindingViewModel row in _rows)
        {
            if (row.IsHidden || (ShowStarredOnly && !row.IsStarred))
            {
                continue;
            }

            row.ShowsSectionHeader = !string.Equals(section, row.Section, StringComparison.Ordinal);
            section = row.Section;
            Findings.Add(row);
        }

        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(EmptyLine));
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(StarredCount));
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(CountsLine));
    }

    /// <summary>"6 of 20 rounds", or "6 rounds" when the denominator is unknown.</summary>
    /// <param name="count">The rounds counted.</param>
    /// <param name="of">The sample they are counted in; 0 when unknown.</param>
    public static string CountText(int count, int of) =>
        of > 0 ? $"{count.ToString(CultureInfo.InvariantCulture)} of {Plural(of, "round")}" : Plural(count, "round");

    private static string Plural(int count, string noun) => OpeningTendenciesSectionViewModel.Plural(count, noun);

    // ---- The sections' lines. Each builder is pure over a section's display rows; zero counts are skipped. ----

    /// <summary>One line per map played, and the decider line when there is one.</summary>
    /// <param name="maps">The Map Pool Record's rows.</param>
    /// <param name="deciderLine">The decider line, or empty.</param>
    public static IEnumerable<DossierFindingSource> FromMapPool(IEnumerable<MapPoolRowViewModel> maps, string deciderLine)
    {
        foreach (MapPoolRowViewModel map in maps)
        {
            yield return new DossierFindingSource($"map|{map.Map}", MapPoolSection,
                $"{map.Map}: {map.PlayedLabel}, {map.RecordLabel}, win rate {map.WinRateLabel}, {map.SideLabel}");
        }

        if (deciderLine.Length > 0)
        {
            yield return new DossierFindingSource("map|deciders", MapPoolSection, deciderLine);
        }
    }

    /// <summary>One line per heatmap, carrying its picture for the HTML form.</summary>
    /// <param name="heatmaps">The Setup Heatmaps.</param>
    public static IEnumerable<DossierFindingSource> FromHeatmaps(IEnumerable<SetupHeatmapViewModel> heatmaps) =>
        heatmaps.Select(h => new DossierFindingSource($"heatmap|{h.Map}|{h.BuyLabel}", HeatmapSection,
            $"{h.Map} CT {h.BuyLabel}: {h.RoundsLabel}; {h.FixedLabel}; {h.RotatingLabel}", () => h.ImagePng));

    /// <summary>Per map and side: the rounds, then every non-zero number over its own sample.</summary>
    /// <param name="blocks">The Opening Tendencies blocks.</param>
    public static IEnumerable<DossierFindingSource> FromOpenings(IEnumerable<OpeningBlockViewModel> blocks)
    {
        foreach (OpeningBlockViewModel b in blocks)
        {
            int rounds = b.RoundsLink.Count;
            foreach (DossierFindingSource s in Sample("openings", OpeningsSection, b.RoundsLink, b.Map, b.SideLabel))
            {
                yield return s;
            }

            foreach (DossierFindingSource s in Links("openings", OpeningsSection, b.Block.UtilityRounds, b.UtilityClock, b.UtilityPlaces)
                         .Concat(Links("openings", OpeningsSection, rounds, b.ContactClock, b.SiteSplit, b.Entries))
                         .Concat(Links("openings", OpeningsSection, b.Block.LurkRounds, b.LurkClock, b.Lurkers)))
            {
                yield return s;
            }
        }
    }

    /// <summary>Per map and side: the plants over the rounds, then every non-zero number over the plants.</summary>
    /// <param name="blocks">The Post-Plant And Retake blocks.</param>
    public static IEnumerable<DossierFindingSource> FromPostPlant(IEnumerable<PostPlantBlockViewModel> blocks)
    {
        foreach (PostPlantBlockViewModel b in blocks)
        {
            int planted = b.PlantedLink.Count;
            foreach (DossierFindingSource s in Links("postplant", PostPlantSection, b.RoundsLink.Count, [b.PlantedLink])
                         .Concat(Links("postplant", PostPlantSection, planted, b.ManCount, b.Outcomes, b.PlantClusters,
                             b.HoldShapes, b.HoldPlaces, b.RetakeGroups)))
            {
                yield return s;
            }
        }
    }

    /// <summary>Per map and side: each situation over the rounds, then its breakdown over the situation.</summary>
    /// <param name="blocks">The Situational Behaviour blocks.</param>
    public static IEnumerable<DossierFindingSource> FromSituational(IEnumerable<SituationalBlockViewModel> blocks)
    {
        foreach (SituationalBlockViewModel b in blocks)
        {
            int rounds = b.RoundsLink.Count;
            foreach (DossierFindingSource s in Links("situational", SituationalSection, rounds, [b.PistolLink])
                         .Concat(Links("situational", SituationalSection, b.PistolLink.Count, b.PistolOutcomes, b.PistolFollowUp))
                         .Concat(Links("situational", SituationalSection, rounds, [b.AntiEcoLink]))
                         .Concat(Links("situational", SituationalSection, b.AntiEcoLink.Count, b.AntiEcoBuyTypes, b.AntiEcoOutcomes,
                             b.AntiEcoContactClock))
                         .Concat(Links("situational", SituationalSection, rounds, [b.ManAdvantageLink]))
                         .Concat(Links("situational", SituationalSection, b.ManAdvantageLink.Count, b.ManAdvantage, b.ManAdvantageOutcomes))
                         .Concat(Links("situational", SituationalSection, rounds, [b.RoundsLostLink]))
                         .Concat(Links("situational", SituationalSection, b.RoundsLostLink.Count, b.SaveDiscipline)))
            {
                yield return s;
            }
        }
    }

    /// <summary>The note, then one line per period.</summary>
    /// <param name="recent">The last period, or null with no diff.</param>
    /// <param name="previous">The period before it, or null.</param>
    /// <param name="note">The section's note.</param>
    public static IEnumerable<DossierFindingSource> FromPeriodDiff(PeriodDiffRowViewModel? recent, PeriodDiffRowViewModel? previous, string note)
    {
        if (recent is null)
        {
            yield break;
        }

        if (note.Length > 0)
        {
            yield return new DossierFindingSource("period|note", PeriodDiffSection, note);
        }

        PeriodDiffRowViewModel[] periods = previous is null ? [recent] : [recent, previous];
        foreach (PeriodDiffRowViewModel p in periods)
        {
            List<string> parts = [p.CountLabel, p.RecordLabel, $"win rate {p.WinRateLabel}", p.SideLabel, p.RosterLabel];
            if (p.HasStandIn)
            {
                parts.Add(p.StandInLabel);
            }

            yield return new DossierFindingSource($"period|{p.Label}", PeriodDiffSection, $"{p.Label}: {string.Join(", ", parts)}");
        }
    }

    /// <summary>One line per veto step the user entered.</summary>
    /// <param name="vetoes">The veto rows.</param>
    public static IEnumerable<DossierFindingSource> FromVetoes(IEnumerable<VetoRowViewModel> vetoes) =>
        vetoes.Select(v => new DossierFindingSource($"veto|{v.Id:N}", VetoSection,
            $"{v.Order}. {v.ActionLabel} {v.Map} by {v.ByLabel}" + (v.Note.Length > 0 ? $" ({v.Note})" : "")));

    private static IEnumerable<DossierFindingSource> Sample(string slug, string section, TendencyLinkViewModel link, string map, string side)
    {
        if (link.Count > 0)
        {
            yield return new DossierFindingSource($"{slug}|{link.Title}", section, $"{map} {side}: {Plural(link.Count, "round")}");
        }
    }

    private static IEnumerable<DossierFindingSource> Links(string slug, string section, int of, params IReadOnlyList<TendencyLinkViewModel>[] lists) =>
        lists.SelectMany(l => l)
            .Where(l => l.Count > 0)
            .Select(l => new DossierFindingSource($"{slug}|{l.Title}", section, $"{l.Title}: {CountText(l.Count, of)}"));
}

/// <summary>One choice in an export picker: the value and the words shown for it.</summary>
/// <param name="Value">The value.</param>
/// <param name="Label">The words.</param>
public sealed record DossierExportOption<T>(T Value, string Label)
    where T : struct, Enum
{
    public override string ToString() => Label;
}

/// <summary>One line of the long form: the star, the text (the user's rewrite when there is one) and its edit state.</summary>
public sealed partial class DossierFindingViewModel : ObservableObject
{
    [ObservableProperty]
    private string _editText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarGlyph), nameof(StarToolTip))]
    private bool _isStarred;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isHidden;

    [ObservableProperty]
    private bool _showsSectionHeader;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    private string _text;

    /// <param name="key">The finding's key.</param>
    /// <param name="section">Its section heading.</param>
    /// <param name="generated">The generated text (a note's own text).</param>
    /// <param name="text">The text shown.</param>
    /// <param name="isNote">True for a line the user wrote.</param>
    /// <param name="image">Reads the line's PNG at export time, or null.</param>
    public DossierFindingViewModel(string key, string section, string generated, string text, bool isNote, Func<byte[]?>? image)
    {
        Key = key;
        Section = section;
        Generated = generated;
        _text = text;
        IsNote = isNote;
        Image = image;
    }

    public string Key { get; }

    public string Section { get; }

    public string Generated { get; }

    public bool IsNote { get; }

    public Func<byte[]?>? Image { get; }

    /// <summary>True when a generated line has been rewritten.</summary>
    public bool IsEdited => !IsNote && !string.Equals(Text, Generated, StringComparison.Ordinal);

    /// <summary>"Remove" on a note, "Leave out" on a generated line.</summary>
    public string RemoveLabel => IsNote ? "Remove" : "Leave out";

    public string StarGlyph => IsStarred ? "★" : "☆";

    public string StarToolTip => IsStarred ? "On the one-pager: click to take it off" : "Star for the one-pager";

    /// <summary>Opens the line for editing, starting from the text shown.</summary>
    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Text;
        IsEditing = true;
    }

    /// <summary>Closes the editor without keeping what was typed.</summary>
    [RelayCommand]
    private void CancelEdit() => IsEditing = false;
}
