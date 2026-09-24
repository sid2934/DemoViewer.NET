#region

using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.ViewModels.Situations;

/// <summary>
///     The result set below the Query Canvas: one <see cref="ResultCardViewModel" /> per hit in the
///     index's order (newest demo first, then round), the walk over them, and the worker that fills
///     the cards' facts and thumbnails in one batch per search.
///     <para>
///         <b>The budget.</b> Forty results walk in under two minutes of user time because a card
///         never opens a demo: its picture is a scene built from the positions file's tuples (a read
///         of 1 to 2 ms per demo, amortised over the hits that share it) rendered in half a
///         millisecond, and the only demo open is the one the user clicked, on the interactive path.
///         The batch runs on one worker off the UI thread and posts each card back as it lands, so
///         walking with <c>J</c> / <c>K</c> never waits on a render. A card whose demo has no positions
///         file (indexed before positions existed, or under a stale fingerprint) shows a note and the
///         strip's stale count says how many; "Rebuild index" is the remedy.
///     </para>
///     <para>
///         Delegate-injected (the Highlights precedent): the playback seam, the thumbnail renderer and
///         the bitmap decoder are all seams a test replaces, and the cache store, the sidecar store and
///         the place sources are the same singletons the strip and the canvas read.
///     </para>
/// </summary>
public sealed partial class ResultCardsViewModel : ViewModelBase
{
    /// <summary>The note on a card whose demo has no current positions file.</summary>
    public const string NoPositionsNote = "no positions for this demo; rebuild the index";

    /// <summary>The note on a card whose matched step the walk did not sample.</summary>
    public const string NoSampleNote = "no sample at this tick";

    private readonly SituationThumbnailCache _cache;
    private readonly Func<byte[], Bitmap?> _decode;
    private readonly DemoCacheStore _demoCache;
    private readonly Func<ISituationPlayback?> _playback;
    private readonly Action<Action> _post;
    private readonly Func<SituationThumbnailRenderer> _renderer;
    private readonly RoundIndexPlaceSources _sources;
    private readonly RoundIndexStore _store;

    // Bumped per Load; a worker whose generation is behind posts nothing, so a search issued while the
    // last batch is still rendering cannot land old pictures on new cards.
    private int _generation;

    /// <summary>The walk's current card, or null with no result set.</summary>
    [ObservableProperty]
    private ResultCardViewModel? _selectedCard;

    /// <summary>"3 of 40", or what the last seek could not do.</summary>
    [ObservableProperty]
    private string _walkLine = "";

    /// <param name="demoCache">The records: Round Facts rows, tick rate and hash per demo.</param>
    /// <param name="store">The positions files.</param>
    /// <param name="sources">The fingerprint in force per map: a positions file under another is stale.</param>
    /// <param name="playback">The seek seam, resolved per click; null on a host with no 2D tab.</param>
    /// <param name="cache">The thumbnail cache; a fresh one when null.</param>
    /// <param name="renderer">Builds the batch's renderer; the pipeline's bundle loader when null.</param>
    /// <param name="post">UI-thread marshal for the worker's results; the dispatcher when null.</param>
    /// <param name="decode">PNG bytes to a bitmap; Avalonia's decoder when null, a stub in a test without a platform.</param>
    public ResultCardsViewModel(
        DemoCacheStore demoCache,
        RoundIndexStore store,
        RoundIndexPlaceSources sources,
        Func<ISituationPlayback?> playback,
        SituationThumbnailCache? cache = null,
        Func<SituationThumbnailRenderer>? renderer = null,
        Action<Action>? post = null,
        Func<byte[], Bitmap?>? decode = null)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(playback);
        _demoCache = demoCache;
        _store = store;
        _sources = sources;
        _playback = playback;
        _cache = cache ?? new SituationThumbnailCache();
        _renderer = renderer ?? (() => new SituationThumbnailRenderer());
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _decode = decode ?? DecodePng;
    }

    /// <summary>The cards, in the index's order.</summary>
    public ObservableCollection<ResultCardViewModel> Cards { get; } = [];

    /// <summary>How many cards there are.</summary>
    public int Count => Cards.Count;

    /// <summary>There is a result set to show.</summary>
    public bool HasCards => Cards.Count > 0;

    /// <summary>The current card's position in the set, one-based; 0 with none selected.</summary>
    public int SelectedIndex => SelectedCard is { } card ? Cards.IndexOf(card) + 1 : 0;

    /// <summary>"40 rounds · click a card to open it ten seconds before the match; J / K walk them in playback".</summary>
    public string HeaderLine => Cards.Count == 0
        ? ""
        : $"{(Cards.Count == 1 ? "1 round" : $"{Cards.Count} rounds")} · click a card to open it "
          + $"{ResultCardViewModel.SeekOffsetSeconds} s before the match; J / K walk them in playback";

    /// <summary>The thumbnail cache, so a rebuild can drop every picture of the old rows.</summary>
    public SituationThumbnailCache Cache => _cache;

    /// <summary>
    ///     Replaces the result set with the hits of a search and starts the batch that fills the cards.
    ///     The cards exist at once, with the match label and the round, so the list is never blank
    ///     while the worker reads records and renders.
    /// </summary>
    /// <param name="hits">The index's hits, in its order.</param>
    public void Load(IReadOnlyList<SituationHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        int generation = Interlocked.Increment(ref _generation);
        Cards.Clear();
        foreach (SituationHit hit in hits)
        {
            Cards.Add(new ResultCardViewModel(this, hit, _demoCache.TryGetIndex(hit.DemoPath)));
        }

        SelectedCard = null;
        WalkLine = "";
        NotifySetChanged();

        if (Cards.Count == 0)
        {
            return;
        }

        List<ResultCardViewModel> snapshot = [.. Cards];
        BatchTask = Task.Run(() => Fill(generation, snapshot));
    }

    /// <summary>The last batch's worker, so a test can await the fill instead of polling the cards.</summary>
    internal Task BatchTask { get; private set; } = Task.CompletedTask;

    /// <summary>Empties the result set: a new query on the canvas, or a map change.</summary>
    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        Cards.Clear();
        SelectedCard = null;
        WalkLine = "";
        NotifySetChanged();
    }

    /// <summary>
    ///     <c>J</c> / <c>K</c>: moves the selection and seeks playback to the new card. False with no
    ///     result set or no card in that direction, so the key stays unhandled; with no selection yet,
    ///     the walk starts at the first card either way.
    /// </summary>
    /// <param name="direction">+1 next, -1 previous.</param>
    public bool Walk(int direction)
    {
        if (Cards.Count == 0)
        {
            return false;
        }

        int next = SelectedCard is null ? 0 : Cards.IndexOf(SelectedCard) + Math.Sign(direction);
        if (next < 0 || next >= Cards.Count)
        {
            return false;
        }

        _ = OpenAsync(Cards[next]);
        return true;
    }

    /// <summary>Makes a card the current one and seeks playback to its <see cref="ResultCardViewModel.SeekTick" />.</summary>
    /// <param name="card">The card.</param>
    public async Task OpenAsync(ResultCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);

        Select(card);
        if (_playback() is not { } playback)
        {
            WalkLine = $"{SelectedIndex} of {Cards.Count} · no 2D playback on this host";
            return;
        }

        bool shown;
        try
        {
            shown = await playback.SeekAsync(card.Hit.DemoPath, card.SeekTick);
        }
        catch (Exception ex)
        {
            shown = false;
            WalkLine = $"{SelectedIndex} of {Cards.Count} · could not open {Path.GetFileName(card.Hit.DemoPath)}: {ex.Message}";
            return;
        }

        if (!shown)
        {
            WalkLine = $"{SelectedIndex} of {Cards.Count} · could not open {Path.GetFileName(card.Hit.DemoPath)}";
        }
    }

    private void Select(ResultCardViewModel card)
    {
        foreach (ResultCardViewModel other in Cards)
        {
            other.IsSelected = ReferenceEquals(other, card);
        }

        SelectedCard = card;
        WalkLine = $"{SelectedIndex} of {Cards.Count}";
        OnPropertyChanged(nameof(SelectedIndex));
    }

    private void NotifySetChanged()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(HeaderLine));
        OnPropertyChanged(nameof(SelectedIndex));
    }

    // The worker: one record read and one positions read per demo, then a render per hit that the
    // cache does not already hold. Hits cluster by demo and arrive in demo order, so the per-demo
    // reads are done once and the renderer's bundle cache is warm after the first card of each map.
    private void Fill(int generation, IReadOnlyList<ResultCardViewModel> cards)
    {
        using SituationThumbnailRenderer renderer = _renderer();
        string? currentPath = null;
        DemoCacheRecord? record = null;
        RoundPositionsDocument? positions = null;
        string fingerprint = "";

        foreach (ResultCardViewModel card in cards)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            SituationHit hit = card.Hit;
            if (!string.Equals(currentPath, hit.DemoPath, StringComparison.Ordinal))
            {
                currentPath = hit.DemoPath;
                fingerprint = _sources.FingerprintFor(hit.Map);
                record = TryLoadRecord(hit.DemoPath);
                positions = _store.TryReadPositions(hit.DemoPath, fingerprint, record?.Sha256);
            }

            RoundFacts? round = record?.RoundFacts?.Rounds.FirstOrDefault(r => r.Number == hit.RoundNumber);
            int tickRate = record?.TickRate ?? 0;

            byte[]? png = null;
            string note = NoPositionsNote;
            if (positions is not null)
            {
                SituationThumbnailKey key = new(hit.DemoStableKey, hit.RoundNumber, hit.FirstMatchTick, fingerprint);
                png = _cache.TryGet(key);
                if (png is null)
                {
                    png = TryRender(renderer, hit, positions);
                    if (png is not null)
                    {
                        _cache.Put(key, png);
                    }
                }

                note = NoSampleNote;
            }

            Bitmap? bitmap = png is null ? null : _decode(png);
            _post(() =>
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                card.ApplyFacts(round, tickRate);
                card.ApplyThumbnail(png, bitmap, note);
            });
        }
    }

    private DemoCacheRecord? TryLoadRecord(string path)
    {
        try
        {
            return _demoCache.TryLoadRecord(path);
        }
        catch (Exception)
        {
            return null; // an unreadable record is "no data", which the card already says
        }
    }

    private static byte[]? TryRender(SituationThumbnailRenderer renderer, SituationHit hit, RoundPositionsDocument positions)
    {
        try
        {
            return renderer.Render(hit.Map, positions, hit.RoundNumber, hit.FirstMatchTick);
        }
        catch (Exception)
        {
            return null; // a render that throws is a placeholder, never a dead result set
        }
    }

    private static Bitmap? DecodePng(byte[] png)
    {
        try
        {
            using MemoryStream stream = new(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
