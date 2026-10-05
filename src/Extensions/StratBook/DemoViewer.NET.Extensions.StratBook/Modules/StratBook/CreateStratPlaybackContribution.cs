#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.Sdk.Playback;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using DemoViewer.NET.Extensions.StratBook.Services.RoundFactsPass;
using DemoViewer.NET.Modules;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.StratBook;

/// <summary>
///     Create Strat From Round in 2D Playback as one playback contribution: the
///     round band's "Create strat from this round" entry, and the review pane its action opens. The entry
///     shows only while <see cref="IStratCapture" /> resolves with a parsed demo, which is how the pack's gate
///     reaches it: off, the service resolves null and the band offers nothing.
/// </summary>
/// <param name="post">Marshals the walk's result onto the UI thread; synchronous when omitted (tests).</param>
/// <param name="jobs">The queue the round's walk runs on; the pool when null (tests).</param>
public sealed class CreateStratPlaybackContribution(Action<Action>? post = null, IExtensionJobs? jobs = null) : Sdk.Playback.IPlaybackContribution
{
    /// <summary>The round band's menu entry.</summary>
    public const string Label = "Create strat from this round";

    private readonly Action<Action> _post = post ?? (action => action());
    private IModuleContext? _context;
    private IDisposable? _menu;
    private Func<CreateStratDialogViewModel>? _next;
    private IPaneHandle? _pane;
    private Sdk.Playback.IPlaybackSurface? _surface;

    /// <inheritdoc />
    public void Attach(Sdk.Playback.IPlaybackSurface surface, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(context);
        Detach();
        _surface = surface;
        _context = context;
        _menu = surface.AddBandMenu(MenuFor);
        _pane = surface.AddPane(PanePlacement.Side, 0, BuildPane);
    }

    /// <inheritdoc />
    public void Detach()
    {
        _menu?.Dispose();
        _pane?.Dispose();
        _menu = null;
        _pane = null;
        _next = null;
        _surface = null;
        _context = null;
    }

    /// <summary>The entry for a round band when a capture with a demo is there; nothing otherwise.</summary>
    internal IEnumerable<MenuEntry> MenuFor(PlaybackBand band)
    {
        if (!band.IsRound || Capture() is null)
        {
            return [];
        }

        return [new MenuEntry(Label, () => Open(band.StartFrameIndex, band.EndFrameIndex))];
    }

    // IStratCapture only promises the parse; the store, Team Identity and the way into the Strat Book are
    // the pack's own host, so a registration of another shape offers nothing rather than half a review.
    private StratCaptureHost? Capture() =>
        _context is { HasDemo: true } context && context.GetService<IStratCapture>() is StratCaptureHost host
                                              && host.Demo() is not null
            ? host
            : null;

    /// <summary>
    ///     Opens the review for the round a band covers: the round from <c>ClipRounds.Derive</c> that opens
    ///     inside the band, so <c>origin.round</c> is <c>ClipRound.Number</c>; our side and the book from Team
    ///     Identity; the round's Round Facts row when the demo has rows.
    /// </summary>
    /// <param name="startFrame">The band's first frame.</param>
    /// <param name="endFrame">The band's last frame.</param>
    internal void Open(int startFrame, int endFrame)
    {
        if (_context is not { } context || _surface is not { } surface || Capture() is not { } host
            || host.Demo() is not { Frames: { Count: > 0 } } demo)
        {
            return;
        }

        int rate = context.TickRate > 0 ? context.TickRate : 64;
        int startTick = demo.Frames[Math.Clamp(startFrame, 0, demo.Frames.Count - 1)].ServerTick;
        int endTick = demo.Frames[Math.Clamp(endFrame, 0, demo.Frames.Count - 1)].ServerTick;
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(demo);
        int at = -1;
        for (int i = 0; i < rounds.Count; i++)
        {
            // An event's tick and its frame's can differ by a tick, so the band's first frame is not an exact key.
            if (rounds[i].StartTickFrameClock >= startTick - rate && rounds[i].StartTickFrameClock <= endTick)
            {
                at = i;
                break;
            }
        }

        if (at < 0)
        {
            return;
        }

        ClipRound round = rounds[at];
        int? windowEnd = at + 1 < rounds.Count ? rounds[at + 1].StartTickFrameClock : null;
        string? path = context.DemoPath;
        RoundFacts? facts = path is null ? null : context.GetService<IRoundFactsSource>()?.RoundAt(path, round.StartTickFrameClock);
        if (facts is not null && facts.Number != round.Number)
        {
            facts = null;
        }

        StratCaptureRequest request = BuildRequest(host, context, round, windowEnd, facts, demo.MapName, Levels(surface));
        OpenWith(request,
            (progress, ct) => RoundCaptureWalker.Walk(demo, round.Number, round.StartTickFrameClock, windowEnd, facts?.EndTick,
                progress, ct),
            host);
    }

    /// <summary>Opens the pane on a request and its walk. <see cref="Open" /> after the round is resolved; tests call it directly.</summary>
    internal void OpenWith(StratCaptureRequest request, Func<IProgress<double>, CancellationToken, RoundCapture> walk,
        StratCaptureHost host)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(walk);
        ArgumentNullException.ThrowIfNull(host);
        if (_pane is not { } pane)
        {
            return;
        }

        _next = () =>
        {
            CreateStratDialogViewModel dialog = new(request, walk, host.Store, _post, jobs: jobs);
            dialog.Closed += () => pane.Close();
            dialog.StratCreated += id => host.OpenStrat?.Invoke(id);
            return dialog;
        };
        pane.Open();
    }

    // The SDK's floors as the capture's level keys read them.
    private static List<MapLevel> Levels(Sdk.Playback.IPlaybackSurface surface) =>
    [
        .. surface.Levels.Select(l => new MapLevel { Id = MapSpace.IdForZMin(l.ZMin), Name = l.Name, ZMin = l.ZMin, ZMax = l.ZMax })
    ];

    private object BuildPane() =>
        _next?.Invoke() ?? throw new InvalidOperationException("The Create Strat pane opens from a round band's menu entry.");

    // Our side's key and book from Team Identity: the side key of our end-of-demo side when the demo has one,
    // else the me accounts; the team's book when that side is a known team, else the user's own.
    internal static StratCaptureRequest BuildRequest(StratCaptureHost host, IModuleContext context, ClipRound round,
        int? windowEnd, RoundFacts? facts, string? demoMap, IReadOnlyList<MapLevel> levels)
    {
        TeamAssignment? assignment = context.DemoPath is { } path ? host.Teams?.GetAssignment(path) : null;
        SideAssignment? ours = assignment?.OurSide is { } side ? assignment.Side(side) : null;
        IReadOnlyCollection<string> key = ours is { Key.Count: > 0 } ? ours.Key : host.Teams?.MyAccounts ?? [];

        StratOwner owner = StratOwner.Me();
        string ownerLabel = "me";
        string? epoch = StratOwner.MeKind;
        if (ours?.TeamId is { } teamId && host.Teams?.AllTeams.FirstOrDefault(t => t.Id == teamId) is { } team)
        {
            owner = StratOwner.Team(team.Id);
            ownerLabel = DisplayText.Sanitize(team.Name) + (team.IsUs ? " (us)" : "");
            epoch = ours.RosterId;
        }

        Func<double, double> levelKeys = StratFromRound.LevelKeys(levels.Count > 0 ? levels : null);
        string? fileName = context.DemoPath is { } demoPath ? Path.GetFileName(demoPath) : null;
        return new StratCaptureRequest((context.MapName ?? demoMap ?? "").ToLowerInvariant(), round.Number, round.StartTickFrameClock,
            windowEnd, context.DemoSha256, fileName, facts, key, owner, ownerLabel, epoch, levelKeys);
    }
}
