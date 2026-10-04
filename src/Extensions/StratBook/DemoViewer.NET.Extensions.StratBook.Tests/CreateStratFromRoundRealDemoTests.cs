#region

using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Create Strat From Round on the two build-10896 replays step-authoring.md §3.9 measured, read in place from the
///     <c>DEMO_PATH</c> folder; the tour sample is never used. The plan's criterion is that a strat made this way
///     needs no manual re-entry to be playable: it validates, saves, projects to ten token tracks and utility, and
///     every step's time maps back to the tick it was captured at. A third variant checks that the buy type and
///     round length come from the engine's Round Facts row instead of the walk.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class CreateStratFromRoundRealDemoTests
{
    /// <summary>The design's two rounds and the detonations it counted in each (§3.9's table).</summary>
    internal static readonly (string Map, string Demo, int Round, int Detonations)[] Measured =
    [
        ("de_mirage", "match730_003842233788306292960_0260929275_408.dem", 7, 16),
        ("de_nuke", "match730_003842182368957825245_0056633905_389.dem", 5, 15)
    ];

    [Test]
    [MethodDataSource(nameof(Rounds))]
    public async Task ACapturedRound_IsPlayable_WithNoManualReEntry(int index)
    {
        (string map, string demo, int round, int detonations) = Measured[index];
        (ParsedDemo parsed, ClipRound clip, RoundCapture capture) = Capture(demo, round);
        int throwSteps = 0;

        foreach (int side in new[] { 2, 3 })
        {
            StratCaptureOptions options = OptionsFor(capture, side, arrows: true);
            StratDocument document = StratFromRound.Document(capture, options, StratOwner.Me(), map, $"Round {round}",
                new StratOrigin { DemoSha256 = "0", Round = clip.Number, FileName = demo }, null, DateTime.UtcNow);

            IReadOnlyList<StratIssue> issues = StratValidator.Validate(document);
            StratStore store = new(null);
            StratSaveResult saved = store.Save(document, [], "created");
            StratSceneProjection projection = StratSceneProjection.Build(document, StratPath.MainLine(document));
            List<StratStep> throws = [.. document.Steps.Where(s => s.Verb == "throw")];

            using (Assert.Multiple())
            {
                await Assert.That(issues.Where(i => i.Severity == StratIssueSeverity.Refusal)).IsEmpty();
                await Assert.That(saved.Saved).IsTrue();
                await Assert.That(document.Steps[0].Verb).IsEqualTo("hold");
                await Assert.That(document.Steps[0].AtSeconds).IsEqualTo(StratClock.DefaultRoundSeconds);
                await Assert.That(projection.ClockClamped).IsFalse();
                await Assert.That(projection.Tracks.Count).IsEqualTo(10).Because("five of ours and five opponents at the freeze-end");
                await Assert.That(throws.All(s => s.Utility?.Landing is { X: not null, Y: not null })).IsTrue()
                    .Because("every detonation carries its world point");
                await Assert.That(projection.Utility.Count).IsEqualTo(throws.Count);
                await Assert.That(throws.Count(s => s.Strokes.Count == 1)).IsEqualTo(throws.Count(s => s.Actor != StratVocabulary.ActorAll
                    && s.Positions.Any(p => p.Slot == s.Actor))).Because("an arrow per throw whose thrower is standing");
                await Assert.That(projection.Elements.Count).IsEqualTo(throws.Sum(s => s.Strokes.Count));

                // Every token samples at every step's tick: nothing has to be dragged into place to play.
                foreach (int tick in projection.Ticks)
                {
                    await Assert.That(projection.Tracks.All(t => t.TrySample(tick, out _))).IsTrue();
                }
            }

            throwSteps += throws.Count;
        }

        // Each side's strat throws its own utility; a fire whose owner the inferno does not name is in both.
        int sideless = capture.Moments.Count(m => m.Trigger == CaptureTrigger.Utility && m.ActorTeam == 0);
        await Assert.That(throwSteps - sideless).IsEqualTo(detonations);

        GC.KeepAlive(parsed);
    }

    [Test]
    [MethodDataSource(nameof(Rounds))]
    public async Task TheCapture_MatchesTheDesignsMeasurement(int index)
    {
        (_, string demo, int round, int detonations) = Measured[index];
        (ParsedDemo parsed, _, RoundCapture capture) = Capture(demo, round);
        List<CaptureMoment> utility = [.. capture.Moments.Where(m => m.Trigger == CaptureTrigger.Utility)];

        // No orphan reaches a keyframe: the freeze-end holds ten live pawns in ten distinct slots, five a side.
        CaptureMoment freeze = capture.FreezeEnd;
        using (Assert.Multiple())
        {
            await Assert.That(utility.Count).IsEqualTo(detonations).Because("§3.9 counted this many detonations in the round");
            await Assert.That(freeze.Pawns.Count).IsEqualTo(10);
            await Assert.That(freeze.Pawns.Select(p => p.PlayerSlot).Distinct().Count()).IsEqualTo(10);
            await Assert.That(freeze.Pawns.Count(p => p.Team == 2)).IsEqualTo(5);
            await Assert.That(freeze.Pawns.All(p => p.Place is not null)).IsTrue().Because("the pawn carries its place");
            await Assert.That(capture.Moments.Select(m => m.Tick)).IsOrderedBy(t => t);
            await Assert.That(capture.LiveEndTick).IsGreaterThan(capture.FreezeEndTick);

            // Throwers are named for every kind but the fire, whose owner the inferno entity carries.
            await Assert.That(utility.Where(m => m.UtilityKind is "smoke" or "flash" or "he").All(m => m.ActorSlot >= 0)).IsTrue();
        }

        // Every captured step's time maps back to its own tick through the strat clock.
        StratCaptureOptions options = OptionsFor(capture, 2, arrows: false);
        List<StratStep> steps = StratFromRound.Steps(capture, options);
        HashSet<int> ticks = [.. capture.Moments.Select(m => m.Tick)];
        foreach (StratStep step in steps)
        {
            int tick = StratClock.TickFor(step.AtSeconds, capture.FreezeEndTick, capture.TickRate, options.RoundSeconds);
            await Assert.That(ticks.Contains(tick)).IsTrue();
        }

        GC.KeepAlive(parsed);
    }

    [Test]
    [MethodDataSource(nameof(Rounds))]
    public async Task WithRoundFactsRows_TheEconomyTargetAndRoundLengthComeFromTheRow(int index)
    {
        (string map, string demo, int round, _) = Measured[index];
        (ParsedDemo parsed, ClipRound clip, RoundCapture capture) = Capture(demo, round);
        RoundFactsRows rows = RoundFactsProjection.Project(ClipRounds.Derive(parsed), new EngineRoundFactsRowSource().Rows(parsed));
        RoundFacts row = rows.Rounds.Single(r => r.Number == clip.Number);

        StratCaptureOptions options = OptionsFor(capture, 2, arrows: true) with
        {
            RoundSeconds = StratClock.RoundSecondsFor(row.RoundTimeSeconds, null)
        };
        StratDocument document = StratFromRound.Document(capture, options, StratOwner.Me(), map, "rows",
            new StratOrigin { Round = clip.Number }, row, DateTime.UtcNow);

        using (Assert.Multiple())
        {
            await Assert.That(document.Economy).IsEqualTo(row.T.BuyType.ToString().ToLowerInvariant());
            await Assert.That(document.Clock.RoundSeconds).IsEqualTo((double)row.RoundTimeSeconds!.Value);
        }
    }

    public static IEnumerable<int> Rounds() => Enumerable.Range(0, Measured.Length);

    internal static StratCaptureOptions OptionsFor(RoundCapture capture, int side, bool arrows)
    {
        IReadOnlyDictionary<char, ulong> map = StratFromRound.SlotMap(capture.FreezeEnd.Pawns.Where(p => p.Team == side), null, null);
        return new StratCaptureOptions(side, StratFromRound.Tokens(capture.FreezeEnd.Pawns, side, map), StratClock.DefaultRoundSeconds,
            arrows, StratFromRound.QuantizedLevel);
    }

    private static (ParsedDemo Parsed, ClipRound Round, RoundCapture Capture) Capture(string demo, int round)
    {
        string path = Path.Combine(ReplaysFolder(), demo);
        if (!File.Exists(path))
        {
            throw new SkipTestException($"{demo} is not in the replays folder");
        }

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(parsed);
        int at = rounds.ToList().FindIndex(r => r.Number == round);
        ClipRound clip = rounds[at];
        int? windowEnd = at + 1 < rounds.Count ? rounds[at + 1].StartTickFrameClock : null;
        return (parsed, clip, RoundCaptureWalker.Walk(parsed, clip.Number, clip.StartTickFrameClock, windowEnd));
    }

    // The replays folder DEMO_PATH names, or the folder of the file it names.
    private static string ReplaysFolder()
    {
        string? env = Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (Directory.Exists(env))
            {
                return env;
            }

            if (File.Exists(env))
            {
                return Path.GetDirectoryName(env)!;
            }
        }

        throw new SkipTestException($"{DemoTestHelper.DemoPathEnvVar} does not name the replays folder the design measured");
    }
}
