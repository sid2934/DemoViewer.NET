#region

using System.Security.Cryptography;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Services.Dependencies;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.Services.Export.Pack;
using DemoViewer.NET.Services.Review;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Lineup and pack clips parse with <see cref="DecodePlan.EntityReplay" />. The clips must come out the same
///     as from the full parse: lineup GIFs byte for byte, pack clip frames pixel for pixel. Three lineups per
///     demo from the grenade walk, the three smallest demos in <c>DEMO_PATH</c>, read in place.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ClipReplayPlanRealDemoTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ClipsFromTheReplayParse_MatchClipsFromTheFullParse(int which)
    {
        if (!FfmpegDependency.Locate().Found)
        {
            throw new SkipTestException("no ffmpeg to encode GIFs");
        }

        string path = BackgroundPlanRealDemoTests.SmallestDemo(which);
        string root = Path.Combine(Path.GetTempPath(), $"dv-clip-ab-{Guid.NewGuid():N}");
        try
        {
            ParsedDemo full = BackgroundPlanRealDemoTests.ParseMapped(path, DecodePlan.Everything);
            List<GrenadeRow> rows = [.. GrenadeWalker.Walk(full).Rows.Where(r => r.ThrowerSteamId is not null)];
            if (rows.Count < 3)
            {
                throw new SkipTestException($"{Path.GetFileName(path)} has fewer than three attributed throws");
            }

            GrenadeRow[] picked = [rows[0], rows[rows.Count / 2], rows[^1]];
            int rate = full.TickRate;
            string map = full.MapName;

            ParsedDemo replay = ParseForReplay(path);
            using (Assert.Multiple())
            {
                await Assert.That(replay.Plan).IsEqualTo(DecodePlan.EntityReplay);
                await Assert.That(replay.TickRate).IsEqualTo(rate);
                await Assert.That(replay.MapName).IsEqualTo(map);
                await Assert.That(FrameClock.IdentityFor(replay)).IsEqualTo(FrameClock.IdentityFor(full))
                    .Because("pack ink is matched to the demo by this identity");
            }

            replay = null!;

            List<string> fullGifs = await RenderLineups(path, picked, map, rate, Path.Combine(root, "full"), _ => full);
            List<string> replayGifs = await RenderLineups(path, picked, map, rate, Path.Combine(root, "replay"), null);
            await Assert.That(fullGifs.Count).IsEqualTo(3);
            await Assert.That(replayGifs.Count).IsEqualTo(3);
            for (int i = 0; i < fullGifs.Count; i++)
            {
                await Assert.That(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(replayGifs[i]))))
                    .IsEqualTo(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullGifs[i]))))
                    .Because($"lineup GIF {i} of {Path.GetFileName(path)}");
            }

            (int from, int to) = LineupClipPlanner.Range(picked[1], rate);
            List<string> fullFrames = await RenderPackClip(path, from, to, rate, _ => full);
            full = null!;
            List<string> replayFrames = await RenderPackClip(path, from, to, rate, null);
            await Assert.That(fullFrames.Distinct().Count()).IsGreaterThan(10).Because("a still clip proves nothing");
            await Assert.That(replayFrames).IsEquivalentTo(fullFrames, TUnit.Assertions.Enums.CollectionOrdering.Matching);

            Console.WriteLine($"{Path.GetFileName(path)}: {fullGifs.Count} GIFs identical, pack clip {fullFrames.Count} frames identical");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    // An entity-replay parse, the smallest plan a clip render reads.
    private static ParsedDemo ParseForReplay(string path) =>
        BackgroundPlanRealDemoTests.ParseMapped(path, DecodePlan.EntityReplay);

    private static async Task<List<string>> RenderLineups(string path, GrenadeRow[] picked, string map, int rate,
        string directory, Func<string, ParsedDemo>? parse)
    {
        Directory.CreateDirectory(directory);
        List<LineupClipJob> jobs = [];
        for (int i = 0; i < picked.Length; i++)
        {
            (int from, int to) = LineupClipPlanner.Range(picked[i], rate);
            string gif = Path.Combine(directory, $"clip{i}{LineupClipPlanner.GifExtension}");
            jobs.Add(new LineupClipJob($"k{i}", Guid.NewGuid(), path, null, map, $"clip {i}", from, to, rate,
                picked[i].ThrowerSteamId, "", gif, LineupClipPlanner.SetposPathFor(gif), [], []));
        }

        IReadOnlyList<LineupClipJob> rendered =
            await new LineupClipRenderer().RenderAsync(path, (parse ?? ParseForReplay)(path), jobs, CancellationToken.None);
        return [.. rendered.Select(j => j.GifPath)];
    }

    private static async Task<List<string>> RenderPackClip(string path, int from, int to, int rate,
        Func<string, ParsedDemo>? parse)
    {
        using PackClipRenderer renderer = new(null, null, parse ?? ParseForReplay);
        PackClip clip = new(ReviewEntry.Clip(path, from, to, "", ReviewSources.Manual, rate), 1, null, 0, 120, null);
        PackSettings settings = PackSettings.For("unused.gif", ExportFormats.Gif) with { Side = 160 };
        HashingSink sink = new();
        await renderer.RenderAsync(clip, settings, sink, CancellationToken.None);
        return sink.Hashes;
    }

    private sealed class HashingSink : IFrameSink
    {
        public List<string> Hashes { get; } = [];

        public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
        {
            Hashes.Add($"{width}x{height}:{Convert.ToHexString(SHA256.HashData(rgba.Span))}");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
