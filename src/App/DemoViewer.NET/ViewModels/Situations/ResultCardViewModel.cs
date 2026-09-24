#region

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.ViewModels.Situations;

/// <summary>
///     One Result Card: a matching (demo, round) with its mini-radar thumbnail of the matched state,
///     the match, the round number, the score before the round, both buy types, the end reason and the
///     clock at the matched tick. The facts come from the demo's Round Facts rows when the record
///     carries them and read <see cref="NoData" /> when it does not (the stats-board rule: say so,
///     never render a zero that reads as a score). A click seeks 2D playback to
///     <see cref="SeekTick" />, ten seconds before the matched tick.
///     <para>
///         Built on the UI thread from the hit and the index row (the match label needs no sidecar
///         read); the facts and the thumbnail arrive from the result set's worker through
///         <see cref="ApplyFacts" /> and <see cref="ApplyThumbnail" />.
///     </para>
/// </summary>
public sealed partial class ResultCardViewModel : ViewModelBase
{
    /// <summary>What a fact reads when the demo has no Round Facts rows.</summary>
    public const string NoData = "no data";

    /// <summary>How far before the matched tick a click lands: enough to see the situation form.</summary>
    public const int SeekOffsetSeconds = 10;

    private readonly ResultCardsViewModel _owner;

    /// <summary>The CT buy type, lower-cased, or <see cref="NoData" />.</summary>
    [ObservableProperty]
    private string _ctBuyText = NoData;

    /// <summary>The end reason's baked icon key (<c>ui/…</c>), or null when there is none for it.</summary>
    [ObservableProperty]
    private string? _endReasonIconKey;

    /// <summary>The end reason in words, or <see cref="NoData" />.</summary>
    [ObservableProperty]
    private string _endReasonText = NoData;

    /// <summary>True once the worker has answered, with rows or without.</summary>
    [ObservableProperty]
    private bool _hasFacts;

    /// <summary>True while this card is the walk's current one.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The round clock at the matched tick: seconds left when the rows say the round time, else <see cref="NoData" />.</summary>
    [ObservableProperty]
    private string _roundClockText = NoData;

    /// <summary>"CT 7 : 5 T" before the round, or <see cref="NoData" />.</summary>
    [ObservableProperty]
    private string _scoreText = NoData;

    /// <summary>The T buy type, lower-cased, or <see cref="NoData" />.</summary>
    [ObservableProperty]
    private string _tBuyText = NoData;

    /// <summary>The decoded thumbnail, or null while it renders or when there is none.</summary>
    [ObservableProperty]
    private Bitmap? _thumbnail;

    /// <summary>What the placeholder tile says instead of a picture; empty while rendering or with one.</summary>
    [ObservableProperty]
    private string _thumbnailNote = "";

    /// <summary>The rendered PNG, kept beside the bitmap so a test can read it without a platform.</summary>
    [ObservableProperty]
    private byte[]? _thumbnailPng;

    /// <summary>The demo's tick rate, 64 until the record says otherwise; the seek offset is measured in it.</summary>
    [ObservableProperty]
    private int _tickRate = 64;

    /// <param name="owner">The result set this card belongs to.</param>
    /// <param name="hit">The matching (demo, round).</param>
    /// <param name="entry">The demo's index row, for the match label; null when the library lost it.</param>
    public ResultCardViewModel(ResultCardsViewModel owner, SituationHit hit, DemoCacheIndexEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(hit);
        _owner = owner;
        Hit = hit;
        MatchLabel = MatchLabelFor(hit, entry);
    }

    /// <summary>The hit this card shows.</summary>
    public SituationHit Hit { get; }

    /// <summary>"Clan vs Clan" when the record names both, else the demo's file name.</summary>
    public string MatchLabel { get; }

    /// <summary>"Round 7".</summary>
    public string RoundLabel => $"Round {Hit.RoundNumber}";

    /// <summary>The map, for the tile's caption.</summary>
    public string Map => Hit.Map;

    /// <summary>Seconds into the round at the matched tick, from the hit alone: "0:47 in".</summary>
    public string ElapsedText => $"{Clock(Math.Max(0, (Hit.FirstMatchTick - Hit.FreezeEndTick) / (double)TickRate))} in";

    /// <summary>The frame-clock tick a click seeks to: the matched tick minus ten seconds, never below zero.</summary>
    public int SeekTick => SeekTickFor(Hit.FirstMatchTick, TickRate);

    /// <summary>A picture is on the tile.</summary>
    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>The tile has a note instead of a picture.</summary>
    public bool HasThumbnailNote => ThumbnailNote.Length > 0;

    /// <summary>The seek target for a matched tick at a tick rate: ten seconds earlier, floored at zero.</summary>
    /// <param name="matchTick">The hit's first matched tick, frame clock.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    public static int SeekTickFor(int matchTick, int tickRate) =>
        Math.Max(0, matchTick - SeekOffsetSeconds * (tickRate > 0 ? tickRate : 64));

    /// <summary>The baked icon for an end reason, or null when the catalogue has nothing that means it.</summary>
    /// <param name="reason">The round's end reason.</param>
    public static string? EndReasonIcon(RoundEndReason reason) => reason switch
    {
        RoundEndReason.TargetBombed => "ui/bomb",
        RoundEndReason.BombDefused => "ui/defuse",
        RoundEndReason.CTWin or RoundEndReason.TerroristsWin => "ui/elimination",
        RoundEndReason.TargetSaved => "ui/timer",
        RoundEndReason.TerroristsSurrender or RoundEndReason.CTSurrender => "ui/alert",
        _ => null
    };

    /// <summary>The end reason in the words a player uses.</summary>
    /// <param name="reason">The round's end reason.</param>
    public static string EndReasonLabel(RoundEndReason reason) => reason switch
    {
        RoundEndReason.TargetBombed => "bomb exploded",
        RoundEndReason.BombDefused => "bomb defused",
        RoundEndReason.CTWin => "CT eliminated T",
        RoundEndReason.TerroristsWin => "T eliminated CT",
        RoundEndReason.TargetSaved => "time ran out",
        RoundEndReason.TerroristsSurrender => "T surrendered",
        RoundEndReason.CTSurrender => "CT surrendered",
        RoundEndReason.Draw => "draw",
        RoundEndReason.Unknown => NoData,
        _ => RoundFactsValues.LowerCamel(reason)
    };

    /// <summary>Fills the facts from the demo's rows, or marks them absent. Called on the UI thread.</summary>
    /// <param name="round">The round's Round Facts row, or null when the demo has none.</param>
    /// <param name="tickRate">The record's tick rate, or 0 when unknown.</param>
    public void ApplyFacts(RoundFacts? round, int tickRate)
    {
        if (tickRate > 0)
        {
            TickRate = tickRate;
        }

        HasFacts = true;
        if (round is null)
        {
            ScoreText = NoData;
            CtBuyText = NoData;
            TBuyText = NoData;
            EndReasonText = NoData;
            EndReasonIconKey = null;
            RoundClockText = NoData;
        }
        else
        {
            ScoreText = $"CT {round.Ct.ScoreBefore} : {round.T.ScoreBefore} T";
            CtBuyText = RoundFactsValues.LowerCamel(round.Ct.BuyType);
            TBuyText = RoundFactsValues.LowerCamel(round.T.BuyType);
            EndReasonText = EndReasonLabel(round.EndReason);
            EndReasonIconKey = EndReasonIcon(round.EndReason);
            double elapsed = Math.Max(0, (Hit.FirstMatchTick - round.FreezeEndTick) / (double)TickRate);
            RoundClockText = round.RoundTimeSeconds is int roundTime
                ? $"{Clock(Math.Max(0, roundTime - elapsed))} left"
                : NoData;
        }

        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(SeekTick));
    }

    /// <summary>Puts the rendered picture on the tile, or the note that stands in for it. Called on the UI thread.</summary>
    /// <param name="png">The rendered PNG, or null.</param>
    /// <param name="bitmap">The decoded picture, or null.</param>
    /// <param name="note">What to say instead when there is no picture.</param>
    public void ApplyThumbnail(byte[]? png, Bitmap? bitmap, string note)
    {
        ArgumentNullException.ThrowIfNull(note);
        ThumbnailPng = png;
        Thumbnail = bitmap;
        // The note stands in for a missing picture, never for a missing decoder: a PNG that would not
        // decode on this host is a blank tile, not a claim that the step was never sampled.
        ThumbnailNote = png is null ? note : "";
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(HasThumbnailNote));
    }

    /// <summary>Seeks 2D playback to <see cref="SeekTick" /> and makes this the walk's current card.</summary>
    [RelayCommand]
    private Task Open() => _owner.OpenAsync(this);

    partial void OnTickRateChanged(int value)
    {
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(SeekTick));
    }

    private static string MatchLabelFor(SituationHit hit, DemoCacheIndexEntry? entry)
    {
        if (entry is { CtClan.Length: > 0, TClan.Length: > 0 })
        {
            return $"{entry.CtClan} vs {entry.TClan}";
        }

        return Path.GetFileNameWithoutExtension(hit.DemoPath);
    }

    private static string Clock(double seconds)
    {
        int whole = (int)Math.Floor(seconds);
        return $"{whole / 60}:{whole % 60:D2}";
    }
}
