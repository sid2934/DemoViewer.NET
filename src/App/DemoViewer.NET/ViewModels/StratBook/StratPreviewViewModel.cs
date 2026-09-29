#region

using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Strats.Mining;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>Where a Detected preview is: building from cached files, shown, or unbuildable.</summary>
public enum StratPreviewState
{
    Loading,
    Ready,
    Missing
}

/// <summary>
///     The strat a detected pattern would become, shown before it is added: the metadata, slots and steps as text,
///     and a read-only canvas playing it. The canvas runs over a session on a private in-memory store, so nothing
///     it does reaches the book, its history or its working copies.
/// </summary>
public sealed partial class StratPreviewViewModel : ObservableObject, IDisposable
{
    private readonly LineupOriginSource? _lineupOrigins;
    private readonly Func<string?, LoadedMapAsset?>? _mapLoader;
    private readonly Action<Action> _post;
    private StratSession? _session;
    private StratStore? _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(IsMissing))]
    private StratPreviewState _state = StratPreviewState.Loading;

    [ObservableProperty]
    private StratCanvasViewModel? _canvas;

    /// <param name="pattern">The pattern as it was when the preview was asked for.</param>
    /// <param name="title">The pattern's title, shown while it builds.</param>
    /// <param name="mapLoader">The canvas's map loader; the baked assets when null.</param>
    /// <param name="post">UI-thread marshal for the canvas session.</param>
    /// <param name="lineupOrigins">Puts a throw's actor at its lineup's throw origin; null projects none.</param>
    public StratPreviewViewModel(MinedPattern pattern, string title, Func<string?, LoadedMapAsset?>? mapLoader, Action<Action> post,
        LineupOriginSource? lineupOrigins = null)
    {
        _lineupOrigins = lineupOrigins;
        Pattern = pattern;
        Key = pattern.Key;
        Title = title;
        _mapLoader = mapLoader;
        _post = post;
    }

    public string Key { get; }

    /// <summary>What the preview was built from; Add to book refuses when the pattern no longer matches it.</summary>
    public MinedPattern Pattern { get; }

    /// <summary>A line above the preview, such as why it was rebuilt; null for none.</summary>
    public string? Notice { get; init; }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public string Title { get; private set; }

    public bool IsLoading => State == StratPreviewState.Loading;

    public bool IsReady => State == StratPreviewState.Ready;

    public bool IsMissing => State == StratPreviewState.Missing;

    public static string MissingLine => "Could not build this strat: the round's cached files are gone. Find strats again.";

    /// <summary>The built document exactly as Build returned it; null until ready.</summary>
    public StratDocument? Document { get; private set; }

    public IReadOnlyList<StratPreviewField> Fields { get; private set; } = [];

    public IReadOnlyList<StratPreviewField> Slots { get; private set; } = [];

    public IReadOnlyList<StratPreviewStepRow> Steps { get; private set; } = [];

    public string? Notes { get; private set; }

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    public void Dispose()
    {
        if (Canvas is { } canvas)
        {
            canvas.PropertyChanged -= OnCanvasChanged;
            canvas.Transport.Pause();
            canvas.Dispose();
        }

        _session?.Dispose();
        Canvas = null;
        _session = null;
        _store = null;
    }

    /// <summary>Shows the built strat, or the missing state when it is null.</summary>
    public void Show(StratDocument? built)
    {
        if (built is null)
        {
            State = StratPreviewState.Missing;
            return;
        }

        Document = built;
        Title = built.Name;
        Fields = FieldsOf(built);
        Slots =
        [
            .. built.Slots.Select(s => new StratPreviewField("Slot " + s.Slot,
                (string.IsNullOrWhiteSpace(s.Role) ? "no role" : s.Role)
                + (s.SteamId is { } id ? " · pinned to " + id : "")))
        ];
        Steps = [.. built.Steps.Select((s, i) => new StratPreviewStepRow(s.Id, i + 1, StratClock.Format(s.AtSeconds), StratStepPhrasing.PhraseWithLines(s, null), s.Note))];
        Notes = built.Notes;

        // A copy into a private store: Save stamps the instance it is given, and the session plays only what a store holds.
        _store = new StratStore(null, _post);
        StratDocument copy = built.Clone();
        if (_store.Save(copy, [], "preview").Saved)
        {
            _session = new StratSession(_store, _post);
            _session.Open(copy.Id);
            Canvas = new StratCanvasViewModel(_session, _mapLoader, readOnly: true, lineupOrigins: _lineupOrigins);
            Canvas.PropertyChanged += OnCanvasChanged;
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Fields));
        OnPropertyChanged(nameof(Slots));
        OnPropertyChanged(nameof(Steps));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(HasNotes));
        State = StratPreviewState.Ready;
        MarkActive();
    }

    private void OnCanvasChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StratCanvasViewModel.ActiveStep))
        {
            MarkActive();
        }
    }

    private void MarkActive()
    {
        Guid? active = Canvas?.ActiveStep?.Id;
        foreach (StratPreviewStepRow row in Steps)
        {
            row.IsActive = row.Id == active;
        }
    }

    private static List<StratPreviewField> FieldsOf(StratDocument d)
    {
        List<StratPreviewField> fields =
        [
            new("Map", d.Map),
            new("Side", d.Side),
            new("Type", d.Type + (d.TargetSite is { } site ? " " + site : "")),
            new("Status", d.Status)
        ];
        if (d.Economy is { } economy)
        {
            fields.Add(new StratPreviewField("Economy", economy));
        }

        if (d.Tempo is { } tempo)
        {
            fields.Add(new StratPreviewField("Tempo", tempo));
        }

        if (d.Trigger is { Text.Length: > 0 } trigger)
        {
            fields.Add(new StratPreviewField("Trigger", trigger.Text));
        }

        if (d.Tags.Count > 0)
        {
            fields.Add(new StratPreviewField("Tags", string.Join(", ", d.Tags)));
        }

        if (d.Origin is { } origin)
        {
            fields.Add(new StratPreviewField("From", string.Create(CultureInfo.InvariantCulture,
                $"round {origin.Round}{(origin.FileName is { } file ? " of " + file : "")}")));
        }

        return fields;
    }
}

/// <summary>A label and its value, read-only.</summary>
public sealed record StratPreviewField(string Label, string Value);

/// <summary>One step of a previewed strat; the canvas's active step is marked by <see cref="Marker" />.</summary>
public sealed partial class StratPreviewStepRow(Guid id, int number, string atText, string text, string? note) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marker))]
    private bool _isActive;

    public Guid Id { get; } = id;

    public string NumberText { get; } = number.ToString(CultureInfo.InvariantCulture);

    public string AtText { get; } = atText;

    public string Text { get; } = text;

    public string? Note { get; } = note;

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    /// <summary>Words, not tint: the playing step reads "now".</summary>
    public string Marker => IsActive ? "now" : "";
}
