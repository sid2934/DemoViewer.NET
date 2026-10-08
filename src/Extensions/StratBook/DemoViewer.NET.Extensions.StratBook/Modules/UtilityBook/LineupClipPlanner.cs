#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Parser;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Frames;
using DemoViewer.NET.Services.Export;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>
///     One planned Lineup Clip: the representative throw of a Lineup Card, the tick range its GIF covers,
///     and the two files the render leaves side by side, the GIF and the <c>setpos</c>/<c>setang</c>
///     line it was thrown from.
/// </summary>
/// <param name="Key">The representative throw's <see cref="IndexedGrenade.Key" />. Changes when an older demo is indexed.</param>
/// <param name="LineupId">The lineup's <see cref="GrenadeLineup.Id" />: what the pair's file name and the queue entry are keyed by.</param>
/// <param name="DemoPath">The demo the throw is in.</param>
/// <param name="Sha256">The demo's content hash, or null.</param>
/// <param name="Map">The map, as the demo header spells it.</param>
/// <param name="Title">"Smoke into CTSpawn", or the landing point when no zone placed it.</param>
/// <param name="FromTick">First tick of the clip, frame clock.</param>
/// <param name="ToTick">Last tick, frame clock, inclusive.</param>
/// <param name="TickRate">The demo's tick rate.</param>
/// <param name="ThrowerSteamId">The thrower the camera follows, or null to frame the whole map.</param>
/// <param name="ConsoleText">The <see cref="GrenadeConsole.Format" /> line the sidecar holds.</param>
/// <param name="GifPath">Where the GIF goes.</param>
/// <param name="SetposPath">Where the console line goes, beside the GIF.</param>
/// <param name="AliasIds">The lineup's <see cref="GrenadeLineup.AliasIds" />.</param>
/// <param name="FormerGifPaths">Where an earlier build or an absorbed neighbour may have left this lineup's GIF.</param>
public sealed record LineupClipJob(
    string Key,
    Guid LineupId,
    string DemoPath,
    string? Sha256,
    string Map,
    string Title,
    int FromTick,
    int ToTick,
    int TickRate,
    ulong? ThrowerSteamId,
    string ConsoleText,
    string GifPath,
    string SetposPath,
    IReadOnlyList<Guid> AliasIds,
    IEnumerable<string> FormerGifPaths)
{
    /// <summary>The pair's shared file name, without extension.</summary>
    public string Stem => Path.GetFileNameWithoutExtension(GifPath);

    /// <summary>How often the lineup was thrown: its rank for render order and for the byte cap.</summary>
    public int Throws { get; init; }

    /// <summary>The <see cref="LineupTechnique.Key" /> the clip shows, or null for a lineup without techniques.</summary>
    public string? TechniqueKey { get; init; }
}

/// <summary>
///     Lineup Clip Render's planning: which throw positions get a clip, what range and
///     camera it renders with, and where the pair lands. One clip per lineup and technique: every throw of
///     one technique from one spot looks the same, so one representative stands for them. Pure: no parse,
///     no render, no file written; <see cref="LineupClipService" /> runs what this plans.
/// </summary>
public static class LineupClipPlanner
{
    /// <summary>The GIF's rate: one of <see cref="SceneExportSession.SupportedFps" />'s GIF rates, the Strat Export default.</summary>
    public const int Fps = 20;

    /// <summary>The GIF's side, square like a Lineup Card's radar.</summary>
    public const int Side = 480;

    /// <summary>Seconds shown before the release, so the throw is seen from its start.</summary>
    public const double LeadSeconds = 1.0;

    /// <summary>Seconds shown after the grenade goes off.</summary>
    public const double TailSeconds = 1.5;

    /// <summary>The longest clip, so a flight the walk never saw end still renders short.</summary>
    public const double MaxSeconds = 12.0;

    /// <summary>
    ///     A lineup has to be thrown this often to get a clip, and a technique other than the lineup's most
    ///     thrown one needs this many throws of its own. A throw seen once is a moment rather than a lineup.
    /// </summary>
    public const int MinThrows = 2;

    /// <summary>The section title earlier builds queued lineup clips under, with the map after it.</summary>
    public const string SectionPrefix = "Lineup clips";

    /// <summary>The sidecar's extension, after the GIF's stem.</summary>
    public const string SetposExtension = ".setpos.txt";

    /// <summary>The GIF's extension.</summary>
    public const string GifExtension = ".gif";

    /// <summary>
    ///     The job for one lineup, or null when it gets none: fewer than <see cref="MinThrows" /> throws,
    ///     or a representative whose release state was not read (no console line, so no pair).
    /// </summary>
    /// <param name="lineup">The throw position.</param>
    /// <param name="title">The card's cluster title.</param>
    /// <param name="directory">Where clips are written.</param>
    public static LineupClipJob? Plan(GrenadeLineup lineup, string title, string directory) =>
        Plan(lineup, lineup.Techniques.Count > 0 ? lineup.Techniques[0] : null, title, directory);

    /// <summary>
    ///     The job for one technique of a lineup, or null when it gets none: a lineup under
    ///     <see cref="MinThrows" />, a technique other than the first under it, or a representative whose
    ///     release state was not read.
    /// </summary>
    /// <param name="lineup">The throw position.</param>
    /// <param name="technique">One of its techniques, or null for a lineup without techniques.</param>
    /// <param name="title">The card's cluster title.</param>
    /// <param name="directory">Where clips are written.</param>
    public static LineupClipJob? Plan(GrenadeLineup lineup, LineupTechnique? technique, string title, string directory) =>
        Plan(lineup, technique, title, directory, null);

    /// <summary>
    ///     The job for one technique of a lineup, shown from a throw in <paramref name="sourceDemo" /> when it has
    ///     one with a console line, else from the representative. The job keeps the representative's key either way.
    /// </summary>
    /// <param name="lineup">The throw position.</param>
    /// <param name="technique">One of its techniques, or null for a lineup without techniques.</param>
    /// <param name="title">The card's cluster title.</param>
    /// <param name="directory">Where clips are written.</param>
    /// <param name="sourceDemo">The demo to take the throw from, or null for the representative's.</param>
    public static LineupClipJob? Plan(GrenadeLineup lineup, LineupTechnique? technique, string title, string directory,
        string? sourceDemo)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        ArgumentNullException.ThrowIfNull(directory);

        bool first = technique is null || (lineup.Techniques.Count > 0 && ReferenceEquals(lineup.Techniques[0], technique));
        if (lineup.Throws.Count < MinThrows || (!first && technique!.Throws.Count < MinThrows))
        {
            return null;
        }

        IndexedGrenade representative = technique?.Representative ?? lineup.Representative;
        IndexedGrenade shown = (sourceDemo is null
            ? null
            : (technique?.Throws ?? lineup.Throws).FirstOrDefault(t =>
                string.Equals(t.Demo.Path, sourceDemo, StringComparison.OrdinalIgnoreCase) && GrenadeConsole.Format(t.Row) is not null))
            ?? representative;
        GrenadeRow row = shown.Row;
        if (GrenadeConsole.Format(row) is not { } console)
        {
            return null;
        }

        int rate = shown.TickRate > 0 ? shown.TickRate : 64;
        (int from, int to) = Range(row, rate);
        string map = representative.Map;
        GrenadeKind kind = representative.Kind;
        string? key = technique?.Key;
        string stem = FileStem(map, kind, lineup.Id, key);
        // The first technique's clip is the one earlier builds rendered per lineup (their representative
        // was Techniques[0]'s), so it adopts their pairs. Deferred: a hash per throw only without a pair.
        IEnumerable<string> former = lineup.AliasIds.Where(a => a != lineup.Id)
            .Select(a => FileStem(map, kind, a, key))
            .Concat(first && key is not null
                ? lineup.AliasIds.Append(lineup.Id).Select(a => FileStem(map, kind, a))
                : [])
            .Concat(first ? lineup.Throws.Select(LegacyFileStem) : [])
            .Distinct(StringComparer.Ordinal)
            .Where(s => !string.Equals(s, stem, StringComparison.Ordinal))
            .Select(s => Path.Combine(directory, s + GifExtension));
        ulong? steamId = ulong.TryParse(row.ThrowerSteamId64, NumberStyles.None, CultureInfo.InvariantCulture,
            out ulong id) && id != 0
            ? id
            : null;

        return new LineupClipJob(representative.Key, lineup.Id, shown.Demo.Path, shown.Demo.Sha256,
            representative.Map, title, from, to, rate, steamId, console,
            Path.Combine(directory, stem + GifExtension), Path.Combine(directory, stem + SetposExtension),
            lineup.AliasIds, former) { Throws = technique?.Throws.Count ?? lineup.Throws.Count, TechniqueKey = key };
    }

    /// <summary>The job for every lineup and technique that gets one, whatever is on disk, in cluster order.</summary>
    /// <param name="clusters">The index's clusters.</param>
    /// <param name="directory">Where clips are written.</param>
    public static IReadOnlyList<LineupClipJob> PlanEvery(IEnumerable<GrenadeCluster> clusters, string directory) =>
        PlanEvery(clusters, directory, null);

    /// <summary>
    ///     The job for every lineup and technique that gets one, in cluster order, each shown from a throw in
    ///     <paramref name="sourceDemo" /> where it has one.
    /// </summary>
    /// <param name="clusters">The index's clusters.</param>
    /// <param name="directory">Where clips are written.</param>
    /// <param name="sourceDemo">The demo to take throws from, or null for the representatives.</param>
    public static IReadOnlyList<LineupClipJob> PlanEvery(IEnumerable<GrenadeCluster> clusters, string directory,
        string? sourceDemo)
    {
        ArgumentNullException.ThrowIfNull(clusters);

        List<LineupClipJob> jobs = [];
        HashSet<Guid> ids = [];
        foreach (GrenadeCluster cluster in clusters)
        {
            string title = Title(cluster);
            foreach (GrenadeLineup lineup in cluster.Lineups)
            {
                if (!ids.Add(lineup.Id))
                {
                    continue;
                }

                if (lineup.Techniques.Count == 0)
                {
                    if (Plan(lineup, null, title, directory, sourceDemo) is { } only)
                    {
                        jobs.Add(only);
                    }

                    continue;
                }

                foreach (LineupTechnique technique in lineup.Techniques)
                {
                    if (Plan(lineup, technique, title, directory, sourceDemo) is { } job)
                    {
                        jobs.Add(job);
                    }
                }
            }
        }

        return jobs;
    }

    /// <summary>The setpos sidecar beside a GIF.</summary>
    /// <param name="gifPath">The GIF.</param>
    public static string SetposPathFor(string gifPath)
    {
        ArgumentNullException.ThrowIfNull(gifPath);
        return gifPath[..^GifExtension.Length] + SetposExtension;
    }

    /// <summary>The pair stem a file in the clip directory belongs to, or null when it is neither half of a pair.</summary>
    /// <param name="fileName">A file name or path.</param>
    public static string? StemOf(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        string name = Path.GetFileName(fileName);
        if (name.EndsWith(SetposExtension, StringComparison.OrdinalIgnoreCase))
        {
            return name[..^SetposExtension.Length];
        }

        return name.EndsWith(GifExtension, StringComparison.OrdinalIgnoreCase) ? name[..^GifExtension.Length] : null;
    }

    /// <summary>
    ///     Every clip the clusters call for whose pair is not already on disk, in cluster order. A lineup
    ///     whose GIF exists without its sidecar is planned again: the pair is the unit.
    /// </summary>
    /// <param name="clusters">The index's clusters.</param>
    /// <param name="directory">Where clips are written.</param>
    /// <param name="fileExists">The existence probe; <see cref="File.Exists" /> in the app.</param>
    public static IReadOnlyList<LineupClipJob> PlanAll(IEnumerable<GrenadeCluster> clusters, string directory,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        return [.. PlanEvery(clusters, directory).Where(j => !fileExists(j.GifPath) || !fileExists(j.SetposPath))];
    }

    /// <summary>
    ///     The clip's tick range: <see cref="LeadSeconds" /> before the release to <see cref="TailSeconds" />
    ///     after the grenade went off (its detonation, else its end, else release plus air time), never
    ///     longer than <see cref="MaxSeconds" />.
    /// </summary>
    /// <param name="row">The throw.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    public static (int FromTick, int ToTick) Range(GrenadeRow row, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(row);
        int rate = tickRate > 0 ? tickRate : 64;
        int release = row.ReleaseTick;
        int landed = row.DetonationTick
                     ?? (row.EndTick > release ? row.EndTick : release + Math.Max(0, row.AirTimeTicks));
        int from = Math.Max(0, release - (int)Math.Round(LeadSeconds * rate));
        int to = Math.Max(release, landed) + (int)Math.Round(TailSeconds * rate);
        return (from, Math.Min(to, from + (int)Math.Round(MaxSeconds * rate)));
    }

    /// <summary>The section title earlier builds queued a map's clips under.</summary>
    /// <param name="map">The map.</param>
    public static string SectionTitle(string map) => $"{SectionPrefix}, {map}";

    /// <summary>The pair a lineup's technique has on disk, or null while either half is missing.</summary>
    /// <param name="directory">Where clips are written.</param>
    /// <param name="map">The map.</param>
    /// <param name="kind">What is thrown.</param>
    /// <param name="lineupId">The lineup's id.</param>
    /// <param name="techniqueKey">The technique, or null for a lineup without techniques.</param>
    public static string? FinishedGif(string directory, string map, GrenadeKind kind, Guid lineupId, string? techniqueKey)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string gif = Path.Combine(directory, FileStem(map, kind, lineupId, techniqueKey) + GifExtension);
        return File.Exists(gif) && File.Exists(SetposPathFor(gif)) ? gif : null;
    }

    /// <summary>
    ///     The export request for a job over its demo's parsed frames: a GIF at <see cref="Fps" />, square,
    ///     the scene's default layers, the camera on the thrower. The tick range is resolved to demo frames
    ///     and clamped to the demo; null when the range lies outside it.
    /// </summary>
    /// <param name="job">The planned clip.</param>
    /// <param name="frames">The demo's frames.</param>
    /// <param name="tickRate">The parsed demo's tick rate.</param>
    public static Scene2DExportRequest? BuildRequest(LineupClipJob job, IReadOnlyList<DemoFrame> frames,
        int tickRate)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0 || job.ToTick < frames[0].ServerTick || job.FromTick > frames[^1].ServerTick)
        {
            return null;
        }

        int start = Math.Max(0, TrackerFrameSource.FrameIndexForTick(frames, Math.Max(job.FromTick, frames[0].ServerTick)));
        int end = TrackerFrameSource.FrameIndexForTick(frames, Math.Min(job.ToTick, frames[^1].ServerTick));
        if (end < start)
        {
            return null;
        }

        int count = TrackerFrameSource.OutputFrameCount(frames, start, end, Fps, 1.0, tickRate);
        CameraScript camera = job.ThrowerSteamId is { } steamId
            ? new CameraScript.FollowPlayer(steamId)
            : new CameraScript.Fixed(new Dictionary<MapLevelId, ViewportTransform>());
        ExportRequest core = new(0, Math.Max(0, count - 1), Fps, new SKSizeI(Side, Side), 1.0, ExportFormats.Gif,
            new HashSet<string>(StringComparer.Ordinal), camera);
        return new Scene2DExportRequest(core, job.GifPath, job.DemoPath, start, end, Palette: ScenePalette.Dark);
    }

    /// <summary>The cluster's title as the card prints it.</summary>
    /// <param name="cluster">The landing cluster.</param>
    public static string Title(GrenadeCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return cluster.LandingPlace is { } place
            ? $"{cluster.Kind} into {place}"
            : string.Create(CultureInfo.InvariantCulture,
                $"{cluster.Kind} at ({cluster.Landing.X:0}, {cluster.Landing.Y:0}, {cluster.Landing.Z:0})");
    }

    /// <summary>
    ///     "de_mirage-smoke-&lt;lineup id&gt;": keyed by the lineup, so a new representative throw keeps the
    ///     file it already has.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="kind">What is thrown.</param>
    /// <param name="lineupId"><see cref="GrenadeLineup.Id" /> or one of its aliases.</param>
    /// <param name="techniqueKey">The technique, appended when given: "...-&lt;id&gt;-stand-jump-left".</param>
    public static string FileStem(string map, GrenadeKind kind, Guid lineupId, string? techniqueKey = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        string stem = string.Create(CultureInfo.InvariantCulture,
            $"{SafeMap(map)}-{kind.ToString().ToLowerInvariant()}-{lineupId:N}");
        return techniqueKey is null ? stem : stem + "-" + SafeMap(techniqueKey);
    }

    // The stem before lineup ids: twelve hex of a hash of the representative throw's key. Read only, to adopt
    // a pair an earlier build rendered.
    internal static string LegacyFileStem(IndexedGrenade grenade)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(grenade.Key));
        return string.Create(CultureInfo.InvariantCulture,
            $"{SafeMap(grenade.Map)}-{grenade.Kind.ToString().ToLowerInvariant()}-{Convert.ToHexStringLower(hash.AsSpan(0, 6))}");
    }

    private static string SafeMap(string map) =>
        string.Concat(map.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}
