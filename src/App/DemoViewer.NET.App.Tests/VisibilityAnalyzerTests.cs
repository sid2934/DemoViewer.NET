#region

using System.Numerics;
using Cs2DemoKit.Analysis.Plugins;
using Cs2DemoKit.Analysis.Visibility;
using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.EntityTracking;
using Cs2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>Phase-2 validation</b> of the "time enemy was visible" stat (<see cref="VisibilityAnalyzer" />),
///     run on BOTH a single-plane map (dust2) and a <b>multi-level</b> map (nuke) — the latter is the case
///     3D exists to get right (a 2D top-down would wrongly call stacked players visible). Load-bearing check
///     is the <b>kill-tick oracle</b>: at a direct kill the killer's crosshair is on the victim, so a correct
///     eye/angle/anchor pipeline yields could-see≈true. Reporting <b>exposed% and could-see% separately</b>
///     localizes bugs (exposed-high + could-see-low ⇒ angle/FOV; both-low ⇒ eye-position/timing). Plus the
///     hard invariant could-see ≤ exposed, and the cross-floor occlusion differentiator. Skips without assets.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class VisibilityAnalyzerTests
{
    // Sample a few frames BEFORE the death event: the death FrameNumber latches post-kill (ragdoll / stale
    // cell coords), whereas ~0.12s earlier the killer is mid-aim and the victim is cleanly positioned.
    private const int KillLookbackFrames = 8;

    // Per-map cache (speedup P2): the class runs 9 test executions, each of which used to
    // re-read a 373-459 MB demo, re-parse it, and re-bake the BVH. Engine and ParsedDemo are
    // consumed read-only (every test builds its own EntityTracker), so one shared instance per
    // map is safe.
    private static readonly Dictionary<string, (VisibilityEngine Engine, ParsedDemo Demo)?> _loadCache = [];

    private static Func<EntityState, Vector3?> PositionResolver => PositionUtil.CellToWorldVector;

    private static (VisibilityEngine Engine, ParsedDemo Demo)? Load(string map)
    {
        lock (_loadCache)
        {
            if (_loadCache.TryGetValue(map, out (VisibilityEngine, ParsedDemo)? cached))
            {
                return cached;
            }

            (VisibilityEngine, ParsedDemo)? loaded = LoadCore(map);
            _loadCache[map] = loaded;
            return loaded;
        }
    }

    private static (VisibilityEngine Engine, ParsedDemo Demo)? LoadCore(string map)
    {
        string? tris = FindBaked(map, "collision.tris");
        if (tris is null)
        {
            return null;
        }

        string[] candidates = map switch
        {
            "de_nuke" =>
            [
                "vitality-vs-fut-m3-nuke.dem", "furia-vs-vitality-m3-nuke.dem",
                "003816306022075596881_1029495947.dem", "match730_003826256877184877003_0981591541_410.dem"
            ],
            _ => ["vitality-vs-fut-m2-dust2.dem"]
        };

        string? demoPath = candidates.Select(DemoTestHelper.FindDemoPath).FirstOrDefault(p => p is not null);
        if (demoPath is null)
        {
            return null;
        }

        VisibilityEngine engine = VisibilityEngine.Load(tris);
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());
        return (engine, demo);
    }

    /// <summary>Kill-tick oracle — the decisive angle-math check — on single-plane AND multi-level maps.</summary>
    [Test]
    [Arguments("de_dust2")]
    [Arguments("de_nuke")]
    public async Task KillTick_CouldSee_IsHigh_ForDirectKills(string map)
    {
        if (Load(map) is not var (engine, demo))
        {
            throw new SkipTestException($"no {map} demo + baked collision");
        }

        IReadOnlyList<DemoFrame> frames = demo.Frames;
        // Pair each fire with its payload: the filter needs Penetrated/Attacker off the payload
        // and FrameNumber off the envelope.
        List<(GameEvent Fire, PlayerDeathEvent Death)> kills = demo.AllGameEvents
            .Where(e => e.Payload is PlayerDeathEvent)
            .Select(e => (Fire: e, Death: (PlayerDeathEvent)e.Payload!))
            .Where(x => x.Death.Penetrated == 0 && x.Death.Attacker >= 0
                        && x.Death.Attacker != x.Death.UserId
                        && x.Fire.FrameNumber >= KillLookbackFrames
                        && x.Fire.FrameNumber < frames.Count)
            .OrderBy(x => x.Fire.FrameNumber)
            .Take(150)
            .ToList();

        if (kills.Count < 20)
        {
            throw new SkipTestException($"{map}: too few direct kills ({kills.Count})");
        }

        EntityTracker tracker = new();
        int cursor = -1; // kills are frame-ordered ⇒ advance ONE tracker forward (not re-seek from 0 per kill)
        int considered = 0, exposedHits = 0, couldSeeHits = 0, shown = 0;
        foreach ((GameEvent fire, PlayerDeathEvent death) in kills)
        {
            int lookIdx = fire.FrameNumber - KillLookbackFrames;
            if (cursor < 0)
            {
                tracker.AdvanceToIndex(lookIdx, frames);
            }
            else
            {
                for (int f = cursor + 1; f <= lookIdx; f++)
                {
                    tracker.AdvanceOneFrame(frames[f]);
                }
            }

            cursor = Math.Max(cursor, lookIdx);
            EntityState? killer = PawnLookup.ResolvePawn(tracker, death.Attacker);
            EntityState? victim = PawnLookup.ResolvePawn(tracker, death.UserId);
            if (killer is null || victim is null ||
                VisibilityAnalyzer.TryVantage(death.Attacker, killer, PositionResolver) is not { } kv ||
                VisibilityAnalyzer.TryVantage(death.UserId, victim, PositionResolver) is not { } vv ||
                !VisibilityAnalyzer.AreEnemies(kv, vv))
            {
                continue;
            }

            (bool exposed, bool couldSee) = VisibilityAnalyzer.EvaluatePair(engine, kv, vv, 53f, 37f);
            considered++;
            exposedHits += exposed ? 1 : 0;
            couldSeeHits += couldSee ? 1 : 0;
            if (!couldSee && shown++ < 6)
            {
                Console.WriteLine($"[killoracle:{map}] miss killer={death.Attacker} victim={death.UserId} " +
                                  $"exposed={exposed} weapon={death.Weapon} smoke={death.ThruSmoke} dist={death.Distance:F0}");
            }
        }

        double exposedPct = (double)exposedHits / considered;
        double couldSeePct = (double)couldSeeHits / considered;
        Console.WriteLine($"[killoracle:{map}] considered={considered}  exposed={exposedPct:P0}  couldSee={couldSeePct:P0}");

        await Assert.That(considered).IsGreaterThan(15);
        await Assert.That(exposedPct).IsGreaterThan(0.85);
        await Assert.That(couldSeePct).IsGreaterThan(0.75);
    }

    /// <summary>
    ///     <b>Smoke occlusion — the core validation.</b> Smoke must block <b>could-see</b> (vision) yet leave
    ///     <b>exposed</b> (line of fire) untouched. Two complementary checks at real kill ticks:
    ///     <list type="number">
    ///         <item>
    ///             <b>Structural:</b> for every kill, exposed is bit-identical with and without smoke — smoke
    ///             can only ever change could-see (a hard regression guard).
    ///         </item>
    ///         <item>
    ///             <b>Synthetic (decisive, sample-robust):</b> on the geometry-clear + in-FOV kills, injecting
    ///             a smoke on the sightline midpoint flips could-see true→false for ~all of them — proving the smoke
    ///             span actually gates could-see through the full eye/anchor/frustum pipeline on real geometry.
    ///         </item>
    ///     </list>
    ///     Plus an <b>ecological cross-check</b>: the demo's real <c>ThruSmoke</c> kills (killer shot the
    ///     victim through a real cloud) should have could-see collapse under the actually-active smokes, while
    ///     ordinary direct kills are unaffected. That real subset can be tiny, so it's reported per-kill and only
    ///     soft-gated (the synthetic check carries the proof).
    /// </summary>
    [Test]
    [Arguments("de_dust2")]
    [Arguments("de_nuke")]
    public async Task KillTick_Smoke_OccludesCouldSee_ButNotExposed(string map)
    {
        if (Load(map) is not var (engine, demo))
        {
            throw new SkipTestException($"no {map} demo + baked collision");
        }

        IReadOnlyList<DemoFrame> frames = demo.Frames;
        // Pair each fire with its payload: the filter needs Penetrated/Attacker off the payload
        // and FrameNumber off the envelope.
        List<(GameEvent Fire, PlayerDeathEvent Death)> kills = demo.AllGameEvents
            .Where(e => e.Payload is PlayerDeathEvent)
            .Select(e => (Fire: e, Death: (PlayerDeathEvent)e.Payload!))
            .Where(x => x.Death.Penetrated == 0 && x.Death.Attacker >= 0
                        && x.Death.Attacker != x.Death.UserId
                        && x.Fire.FrameNumber >= KillLookbackFrames
                        && x.Fire.FrameNumber < frames.Count)
            .OrderBy(x => x.Fire.FrameNumber)
            .Take(300)
            .ToList();

        if (kills.Count < 20)
        {
            throw new SkipTestException($"{map}: too few direct kills ({kills.Count})");
        }

        EntityTracker tracker = new();
        List<Vector4> smokeBuf = new(4);
        int cursor = -1; // kills are frame-ordered ⇒ advance ONE tracker forward (not re-seek from 0 per kill)
        int considered = 0, exposedIdentical = 0;
        int synthCandidates = 0, synthBlocked = 0; // couldSee(no-smoke)=true → midpoint smoke blocks?
        int realSmokeKills = 0, realSmokeSubset = 0, realSmokeFlipped = 0;
        int directKills = 0, directFlipped = 0, shownReal = 0;

        foreach ((GameEvent fire, PlayerDeathEvent death) in kills)
        {
            int lookIdx = fire.FrameNumber - KillLookbackFrames;
            if (cursor < 0)
            {
                tracker.AdvanceToIndex(lookIdx, frames);
            }
            else
            {
                for (int f = cursor + 1; f <= lookIdx; f++)
                {
                    tracker.AdvanceOneFrame(frames[f]);
                }
            }

            cursor = Math.Max(cursor, lookIdx);
            EntityState? killer = PawnLookup.ResolvePawn(tracker, death.Attacker);
            EntityState? victim = PawnLookup.ResolvePawn(tracker, death.UserId);
            if (killer is null || victim is null ||
                VisibilityAnalyzer.TryVantage(death.Attacker, killer, PositionResolver) is not { } kv ||
                VisibilityAnalyzer.TryVantage(death.UserId, victim, PositionResolver) is not { } vv ||
                !VisibilityAnalyzer.AreEnemies(kv, vv))
            {
                continue;
            }

            considered++;
            (bool exposedNo, bool couldSeeNo) = VisibilityAnalyzer.EvaluatePair(engine, kv, vv, 53f, 37f);

            // Real active smokes at this tick.
            VisibilityAnalyzer.CollectActiveSmokes(tracker, smokeBuf);
            (bool exposedReal, bool couldSeeReal) =
                VisibilityAnalyzer.EvaluatePair(engine, kv, vv, 53f, 37f, smokeBuf.ToArray());

            if (exposedNo == exposedReal)
            {
                exposedIdentical++;
            }

            if (death.ThruSmoke)
            {
                realSmokeKills++;
                if (couldSeeNo)
                {
                    realSmokeSubset++;
                    if (!couldSeeReal)
                    {
                        realSmokeFlipped++;
                    }
                }

                if (shownReal++ < 8)
                {
                    Console.WriteLine($"[smoke:{map}] REAL through-smoke kill killer={death.Attacker} " +
                                      $"victim={death.UserId} exposed={exposedReal} couldSee: noSmoke={couldSeeNo} " +
                                      $"withSmoke={couldSeeReal} activeSmokes={smokeBuf.Count} weapon={death.Weapon}");
                }
            }
            else
            {
                directKills++;
                if (couldSeeNo && !couldSeeReal)
                {
                    directFlipped++;
                }
            }

            // Synthetic injection: on kills we'd otherwise call "could see", drop a smoke on the sightline
            // midpoint (eye → victim chest) and confirm could-see collapses — decisive and sample-robust.
            if (couldSeeNo)
            {
                synthCandidates++;
                Vector3 chest = new(vv.Feet.X, vv.Feet.Y, vv.Feet.Z + 48f);
                Vector3 mid = (kv.Eye + chest) * 0.5f;
                Vector4[] inject = new[]
                {
                    new Vector4(mid, SmokeVolumes.DefaultRadius)
                };
                (_, bool couldSeeInj) = VisibilityAnalyzer.EvaluatePair(engine, kv, vv, 53f, 37f, inject);
                if (!couldSeeInj)
                {
                    synthBlocked++;
                }
            }
        }

        double synthRate = synthCandidates > 0 ? (double)synthBlocked / synthCandidates : 0;
        Console.WriteLine($"[smoke:{map}] considered={considered} exposedIdentical={exposedIdentical} " +
                          $"synth={synthBlocked}/{synthCandidates} ({synthRate:P0}) " +
                          $"realSmokeKills={realSmokeKills} realSubset={realSmokeSubset} realFlipped={realSmokeFlipped} " +
                          $"directKills={directKills} directFlipped={directFlipped}");

        await Assert.That(considered).IsGreaterThan(15);

        // (1) Structural: smoke NEVER changes exposed.
        await Assert.That(exposedIdentical).IsEqualTo(considered);

        // (2) Synthetic: a midpoint smoke blocks vision on ~all real geometry-clear + in-FOV kills.
        await Assert.That(synthCandidates).IsGreaterThan(15);
        await Assert.That(synthRate).IsGreaterThan(0.95);

        // Ordinary direct kills (bullet did NOT pass through smoke) are essentially unaffected by real smokes.
        await Assert.That(directFlipped).IsLessThanOrEqualTo(Math.Max(1, directKills / 20));

        // (3) Ecological: real through-smoke kills — hard-gate only when the subset is big enough to be stable.
        if (realSmokeSubset >= 5)
        {
            await Assert.That((double)realSmokeFlipped / realSmokeSubset).IsGreaterThan(0.5);
        }
    }

    /// <summary>
    ///     <b>The silent-failure gate:</b> confirms active smokes have a bounded lifetime, so they
    ///     can't occlude vision for the rest of the round. Enumerates raw active <c>CSmokeGrenadeProjectile</c>
    ///     clouds across a windowed replay — if the entity lingered after fade, the concurrent count and the
    ///     observed age-spread would blow up toward the whole window. Also A/Bs <see cref="VisibilityAnalyzer.Analyze" />
    ///     with smoke on vs off: smoke must reduce could-see, never exposed, and must not collapse vision to near-zero.
    /// </summary>
    [Test]
    [Arguments("de_dust2")]
    [Arguments("de_nuke")]
    public async Task SmokeWindow_HasBoundedLifetime_AndDoesNotCollapseVision(string map)
    {
        if (Load(map) is not var (engine, demo))
        {
            throw new SkipTestException($"no {map} demo + baked collision");
        }

        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int start = frames.Count / 4;
        int end = Math.Min(frames.Count - 1, start + 16000);

        // Raw smoke-lifetime scan (deliberately NO age cap — we want to SEE a never-expiring entity if it exists).
        EntityTracker tracker = new();
        tracker.AdvanceToIndex(start, frames);
        int maxConcurrent = 0, ticksWithSmoke = 0, minAge = int.MaxValue, maxAge = int.MinValue;
        for (int i = start; i <= end; i++)
        {
            if (i > start)
            {
                tracker.AdvanceOneFrame(frames[i]);
            }

            if ((i - start) % 8 != 0)
            {
                continue;
            }

            int tick = frames[i].ServerTick;
            int active = 0;
            foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
            {
                if (ent.ClassName != "CSmokeGrenadeProjectile")
                {
                    continue;
                }

                int begin = AsInt(ent["m_nSmokeEffectTickBegin"]);
                if (begin <= 0)
                {
                    continue;
                }

                if (ent.TryGet<Vector3>("m_vSmokeDetonationPos") is { } pos && (pos.X != 0 || pos.Y != 0))
                {
                    active++;
                    int age = tick - begin;
                    minAge = Math.Min(minAge, age);
                    maxAge = Math.Max(maxAge, age);
                }
            }

            if (active > 0)
            {
                ticksWithSmoke++;
                maxConcurrent = Math.Max(maxConcurrent, active);
            }
        }

        // NOTE: m_nSmokeEffectTickBegin lives on a DIFFERENT tick origin than DemoFrame.ServerTick (offset by a
        // per-demo constant — pre-recording ticks), so the ABSOLUTE age (tick − begin) is large-negative and
        // meaningless. The age SPREAD (maxAge − minAge) is base-independent, though, and equals the real span of
        // smoke ages observed ≈ one smoke's lifetime. That's what proves the window ends: a lingering entity
        // would push the spread toward the whole window (and pile up concurrency).
        int ageSpread = maxAge - minAge;
        Console.WriteLine($"[smokelife:{map}] ticksWithSmoke={ticksWithSmoke} maxConcurrent={maxConcurrent} " +
                          $"ageSpreadTicks={ageSpread} (~{ageSpread / 64.0:F0}s; raw ages cross-base, ignore sign)");

        if (ticksWithSmoke == 0)
        {
            throw new SkipTestException($"{map}: no active smoke in the sampled window");
        }

        // A never-expiring cloud would accumulate across rounds; a real instant tops out at a handful.
        await Assert.That(maxConcurrent).IsLessThanOrEqualTo(15);
        // Observed ages span at most one smoke lifetime (~18 s + slack). A lingering entity would blow this up.
        await Assert.That(ageSpread).IsLessThanOrEqualTo(30 * 64);

        // A/B: smoke on vs off over the same window.
        VisibilityAnalyzer.Options optOn = new(StartFrame: start, EndFrame: end, IncludeSmoke: true);
        VisibilityAnalyzer.Options optOff = new(StartFrame: start, EndFrame: end, IncludeSmoke: false);
        VisibilityAnalyzer.Report on = VisibilityAnalyzer.Analyze(frames, engine, PositionResolver, optOn);
        VisibilityAnalyzer.Report off = VisibilityAnalyzer.Analyze(frames, engine, PositionResolver, optOff);

        double csOn = on.Pairs.Sum(p => p.CouldSeeSeconds), csOff = off.Pairs.Sum(p => p.CouldSeeSeconds);
        double expOn = on.Pairs.Sum(p => p.ExposedSeconds), expOff = off.Pairs.Sum(p => p.ExposedSeconds);
        Console.WriteLine($"[smokelife:{map}] couldSee on={csOn:F1}s off={csOff:F1}s  exposed on={expOn:F1}s off={expOff:F1}s");

        const double Slack = 1e-3;
        // Exposed is smoke-independent → identical accumulation across the two deterministic replays.
        await Assert.That(Math.Abs(expOn - expOff)).IsLessThanOrEqualTo(Slack + expOff * 1e-6);
        // Smoke only removes vision…
        await Assert.That(csOn).IsLessThanOrEqualTo(csOff + Slack);
        // …but doesn't collapse it: players still see each other most of the time across a whole window.
        await Assert.That(csOn).IsGreaterThan(csOff * 0.5);
    }

    private static int AsInt(object? o) => o switch
    {
        int i => i, uint u => (int)u, short s => s, ushort us => us, byte b => b, long l => (int)l, _ => 0
    };

    /// <summary>Windowed accumulation: hard invariant (could-see ≤ exposed) + a plausibility summary, both maps.</summary>
    [Test]
    [Arguments("de_dust2")]
    [Arguments("de_nuke")]
    public async Task Analyze_Window_Invariants_And_Summary(string map)
    {
        if (Load(map) is not var (engine, demo))
        {
            throw new SkipTestException($"no {map} demo + baked collision");
        }

        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int start = frames.Count / 4;
        VisibilityAnalyzer.Options opt = new(StartFrame: start, EndFrame: start + 20000);
        VisibilityAnalyzer.Report report = VisibilityAnalyzer.Analyze(frames, engine, PositionResolver, opt);

        Console.WriteLine($"[vstat:{map}] sampledTicks={report.SampledTicks} sampledSeconds={report.SampledSeconds:F1} " +
                          $"pairs={report.Pairs.Count}");
        foreach (VisibilityAnalyzer.PairStat p in report.Pairs.OrderByDescending(p => p.CouldSeeSeconds).Take(4))
        {
            Console.WriteLine($"[vstat:{map}]   A{p.ViewerSlot}→E{p.TargetSlot}  exposed={p.ExposedSeconds:F1}s couldSee={p.CouldSeeSeconds:F1}s");
        }

        await Assert.That(report.SampledTicks).IsGreaterThan(0);
        await Assert.That(report.Pairs.Count).IsGreaterThan(0);

        const double Slack = 1e-6;
        foreach (VisibilityAnalyzer.PairStat p in report.Pairs)
        {
            await Assert.That(p.CouldSeeSeconds).IsLessThanOrEqualTo(p.ExposedSeconds + Slack);
            await Assert.That(p.ExposedSeconds).IsGreaterThanOrEqualTo(0);
            await Assert.That(p.ExposedSeconds).IsLessThanOrEqualTo(report.SampledSeconds + Slack);
        }
    }

    /// <summary>
    ///     <b>The multi-level differentiator.</b> On nuke, enemy pairs at similar XY but separated in Z across
    ///     the floor band (a player stacked above another) must be <b>mostly occluded</b> — the solid floor
    ///     slab blocks the sightline. A 2D top-down projection would call every one of these visible (same
    ///     spot on the map), so a high occlusion rate here is exactly the 3D correctness dust2 cannot test.
    /// </summary>
    [Test]
    public async Task MultiLevel_CrossFloor_NearXY_IsMostlyOccluded()
    {
        if (Load("de_nuke") is not var (engine, demo))
        {
            throw new SkipTestException("no nuke demo + baked collision");
        }

        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int start = frames.Count / 4;
        int end = Math.Min(frames.Count - 1, start + 20000);

        EntityTracker tracker = new();
        tracker.AdvanceToIndex(start, frames);
        List<VisibilityAnalyzer.Vantage> samples = new(12);

        int crossFloorPairs = 0, occluded = 0;
        for (int i = start; i <= end; i++)
        {
            if (i > start)
            {
                tracker.AdvanceOneFrame(frames[i]);
            }

            if ((i - start) % 16 != 0) // sample density tuned to collect enough stacked pairs (spatial check)
            {
                continue;
            }

            samples.Clear();
            PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            {
                if (VisibilityAnalyzer.TryVantage(slot, pawn, PositionResolver) is { } v)
                {
                    samples.Add(v);
                }
            });

            foreach (VisibilityAnalyzer.Vantage a in samples)
            {
                foreach (VisibilityAnalyzer.Vantage b in samples)
                {
                    if (a.Slot >= b.Slot || !VisibilityAnalyzer.AreEnemies(a, b))
                    {
                        continue;
                    }

                    float dx = a.Feet.X - b.Feet.X, dy = a.Feet.Y - b.Feet.Y;
                    float horiz = MathF.Sqrt(dx * dx + dy * dy);
                    float dz = MathF.Abs(a.Feet.Z - b.Feet.Z);
                    if (horiz > 250f || dz < 190f) // near in XY, separated across the floor band
                    {
                        continue;
                    }

                    // Eye of the higher player → chest of the lower (a straight stacked sightline).
                    (VisibilityAnalyzer.Vantage upper, VisibilityAnalyzer.Vantage lower) = a.Feet.Z > b.Feet.Z ? (a, b) : (b, a);
                    Vector3 lowerChest = new(lower.Feet.X, lower.Feet.Y, lower.Feet.Z + 48f);
                    crossFloorPairs++;
                    if (!engine.IsVisible(upper.Eye, lowerChest))
                    {
                        occluded++;
                    }
                }
            }
        }

        double rate = crossFloorPairs > 0 ? (double)occluded / crossFloorPairs : 0;
        Console.WriteLine($"[crossfloor] near-XY cross-floor pairs={crossFloorPairs} occluded={occluded} ({rate:P0}) " +
                          "— a 2D projection would call ALL of these visible");

        // Need a meaningful sample of stacked pairs, and the floor must block the majority (openings —
        // ramp/hole/vents — are the visible minority). A floor-occlusion bug would drive this toward 0%.
        await Assert.That(crossFloorPairs).IsGreaterThan(30);
        await Assert.That(rate).IsGreaterThan(0.6);
    }

    private static string? FindBaked(string mapName, string file)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "cs2-assets", "baked", mapName, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
