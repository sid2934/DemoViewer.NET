#region

using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The step 9 parity measurement: the round index folded from <see cref="PositionSampler.Walk" /> against
///     the same fold over samples an <see cref="IFrameObserver" /> reads off the rules run at the same stride.
///     Prints per-field mismatch counts and the first differing bytes, and asserts frame alignment.
///     <c>hostAlive</c> adds probe-only liveness and pawn-index columns, which the engine does not ship; with
///     them the fold in entity-index order must equal the walk's byte for byte. Without them it is printed only.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundIndexObserverParityProbe
{
    private static readonly string[] Declared =
        ["entity.pawn.pos_x", "entity.pawn.pos_y", "entity.pawn.pos_z", "entity.pawn.place", "entity.pawn.health"];

    // Probe only: PawnLookup.IsAlive emitted for every bound pawn, to size the engine ask.
    private static bool _hostAlive;

    private sealed class AliveProvider : IPerPlayerEntityValueProvider, IPawnStateReader
    {
        public string EntityClass => "CCSPlayerPawn";
        public string FieldName => "m_iHealth";
        public string Name => "dvprobe.pawn.alive";
        public Type ValueType => typeof(int);

        public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit) =>
            PawnLookup.ForEachLivePawn(layer.Tracker, (slot, pawn) => emit(slot, PawnLookup.IsAlive(pawn) ? 1 : 0));

        public object? ReadForPawnState(EntityTracker tracker, EntityState pawn) => PawnLookup.IsAlive(pawn) ? 1 : 0;

        public object? ReadForPawn(EntityTracker tracker, CS2OpenDev.Sdk.Entities.CSPlayerPawn pawn) => throw new NotSupportedException();

        public object? Read(EntityStateLayer layer, int playerSlot) =>
            PawnLookup.ResolvePawn(layer.Tracker, playerSlot) is { } pawn ? PawnLookup.IsAlive(pawn) ? 1 : 0 : null;
    }

    // Probe only: the pawn's entity index, the order PawnLookup.ForEachLivePawn visits.
    private sealed class IndexProvider : IPerPlayerEntityValueProvider, IPawnStateReader
    {
        public string EntityClass => "CCSPlayerPawn";
        public string FieldName => "m_iHealth";
        public string Name => "dvprobe.pawn.index";
        public Type ValueType => typeof(int);

        public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit) =>
            PawnLookup.ForEachLivePawn(layer.Tracker, (slot, pawn) =>
            {
                if (ReadForPawnState(layer.Tracker, pawn) is { } index)
                {
                    emit(slot, index);
                }
            });

        public object? ReadForPawnState(EntityTracker tracker, EntityState pawn)
        {
            if (!PawnLookup.TryReadHandle(pawn, "m_hController", out uint controller)
                || tracker.CurrentEntities[PawnLookup.IndexOf(controller)] is not { } ctrl
                || !PawnLookup.TryReadHandle(ctrl, "m_hPlayerPawn", out uint handle))
            {
                return null;
            }

            int index = PawnLookup.IndexOf(handle);
            return index >= 0 && ReferenceEquals(tracker.CurrentEntities[index], pawn) ? index : null;
        }

        public object? ReadForPawn(EntityTracker tracker, CS2OpenDev.Sdk.Entities.CSPlayerPawn pawn) => throw new NotSupportedException();

        public object? Read(EntityStateLayer layer, int playerSlot) =>
            PawnLookup.ResolvePawn(layer.Tracker, playerSlot) is { } pawn ? ReadForPawnState(layer.Tracker, pawn) : null;
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    [Arguments(4, false)]
    [Arguments(4, true)]
    public async Task Probe(int which, bool hostAlive)
    {
        _hostAlive = hostAlive;
        // The benchmarks are the oracle set; a folder DEMO_PATH stands in only where they are absent.
        string? root = DemoTestHelper.FindRepoRoot();
        string folder = root is not null && Directory.Exists(Path.Combine(root, "demos", "benchmarks"))
            ? Path.Combine(root, "demos", "benchmarks")
            : Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar) is { } env && Directory.Exists(env)
                ? env
                : throw new SkipTestException("no benchmark demos");
        string[] demos = Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*.dem").Order(StringComparer.Ordinal)] : [];
        string path = which < demos.Length ? demos[which] : throw new SkipTestException($"fewer than {which + 1} benchmark demos");
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        int stride = RoundIndexOptions.Default.FrameStride;

        List<PositionSample> sampler = [.. PositionSampler.Walk(parsed, stride)];
        Recorder observer = new(parsed, stride);
        PerPlayerEntityValueProviderRegistry registry = PerPlayerEntityValueProviderRegistry.CreateDefault();
        registry.Register(new AliveProvider());
        registry.Register(new IndexProvider());
        DemoAnalysis.Run(parsed, [], new AnalysisOptions
        {
            CaptureSnapshots = false,
            ObservedProviders = hostAlive ? [.. Declared, "dvprobe.pawn.alive", "dvprobe.pawn.index"] : Declared,
            PerPlayerEntityProviders = registry,
            FrameObserver = observer
        });

        Console.WriteLine($"frames {parsed.Frames.Count}, observed {observer.Frames}, misaligned {observer.Misaligned}");
        Dictionary<(int Frame, int Slot), PositionSample> bySampler = sampler.ToDictionary(s => (s.FrameIndex, s.PlayerSlot));
        Dictionary<(int Frame, int Slot), PositionSample> byObserver = observer.Samples.ToDictionary(s => (s.FrameIndex, s.PlayerSlot));
        int onlySampler = 0, onlyObserver = 0, onlySamplerAlive = 0, onlyObserverAlive = 0, alive = 0, team = 0, teamAlive = 0, place = 0, pos = 0, tick = 0;
        StringBuilder first = new();
        foreach ((var key, PositionSample s) in bySampler)
        {
            if (!byObserver.TryGetValue(key, out PositionSample o))
            {
                onlySampler++;
                if (s.IsAlive && s.Team is 2 or 3)
                {
                    onlySamplerAlive++;
                    if (first.Length < 2000)
                    {
                        first.AppendLine(CultureInfo.InvariantCulture, $"only sampler {s}");
                    }
                }

                continue;
            }

            if (s.IsAlive != o.IsAlive)
            {
                alive++;
                if (first.Length < 2000)
                {
                    first.AppendLine(CultureInfo.InvariantCulture, $"alive sampler {s} observer {o}");
                }
            }

            if (s.Team != o.Team)
            {
                team++;
                if (s.IsAlive)
                {
                    teamAlive++;
                }
            }

            place += string.Equals(s.Place, o.Place, StringComparison.Ordinal) ? 0 : 1;
            pos += s.Position == o.Position ? 0 : 1;
            tick += s.Tick == o.Tick ? 0 : 1;
        }

        foreach ((var key, PositionSample o) in byObserver)
        {
            if (!bySampler.ContainsKey(key))
            {
                onlyObserver++;
                if (o.IsAlive && o.Team is 2 or 3)
                {
                    onlyObserverAlive++;
                    if (first.Length < 4000)
                    {
                        first.AppendLine(CultureInfo.InvariantCulture, $"only observer {o}");
                    }
                }
            }
        }

        Console.WriteLine($"sampler {sampler.Count}, observer {observer.Samples.Count}; only sampler {onlySampler} (alive {onlySamplerAlive}), only observer {onlyObserver} (alive {onlyObserverAlive}); alive {alive}, team {team} (alive {teamAlive}), place {place}, pos {pos}, tick {tick}");
        Console.WriteLine(first.ToString());

        DemoCacheStore store = new(null);
        RoundFactsEvaluator facts = new(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        facts.Evaluate(path, parsed);
        RoundFactsRows rows = store.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("no rows");
        RoundIndexBuild a = RoundIndexBuilder.BuildWithPositions(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance, sampler);
        RoundIndexBuild b = RoundIndexBuilder.BuildWithPositions(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance, observer.Samples);
        Dictionary<(int, int), int> order = [];
        for (int k = 0; k < sampler.Count; k++)
        {
            order[(sampler[k].FrameIndex, sampler[k].PlayerSlot)] = k;
        }

        List<PositionSample> reordered = [.. observer.Samples.OrderBy(o => o.FrameIndex)
            .ThenBy(o => order.TryGetValue((o.FrameIndex, o.PlayerSlot), out int k) ? k : int.MaxValue)];
        RoundIndexBuild c = RoundIndexBuilder.BuildWithPositions(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance, reordered);
        RoundIndexBuild d = RoundIndexBuilder.BuildWithPositions(parsed, rows, RoundIndexOptions.Default, PawnPlaceSource.Instance,
            [.. observer.Samples.Select((o, n) => (o, n)).OrderBy(p => p.o.FrameIndex).ThenBy(p => observer.Index[p.n]).Select(p => p.o)]);
        string id = d.Index.Serialize(), pd = Encoding.UTF8.GetString(d.Positions.SerializeUtf8());
        Console.WriteLine($"in entity-index order: index equal {a.Index.Serialize() == id}, positions equal {Encoding.UTF8.GetString(a.Positions.SerializeUtf8()) == pd}");
        Console.WriteLine($"in sampler order: index equal {a.Index.Serialize() == c.Index.Serialize()}, positions equal {Encoding.UTF8.GetString(a.Positions.SerializeUtf8()) == Encoding.UTF8.GetString(c.Positions.SerializeUtf8())}");
        string ia = a.Index.Serialize(), ib = b.Index.Serialize();
        string pa = Encoding.UTF8.GetString(a.Positions.SerializeUtf8()), pb = Encoding.UTF8.GetString(b.Positions.SerializeUtf8());
        Console.WriteLine($"index equal {ia == ib}, positions equal {pa == pb}");
        foreach ((string x, string y, string label) in new[] { (ia, ib, "index"), (pa, pb, "positions") })
        {
            int at = 0;
            while (at < Math.Min(x.Length, y.Length) && x[at] == y[at])
            {
                at++;
            }

            if (at < Math.Max(x.Length, y.Length))
            {
                int from = Math.Max(0, at - 150);
                Console.WriteLine($"{label} first diff at {at} of {x.Length}/{y.Length}:\n  sampler  {x.Substring(from, Math.Min(300, x.Length - from))}\n  observer {y.Substring(from, Math.Min(300, y.Length - from))}");
            }
        }
        for (int r = 0; r < a.Index.Rounds.Count; r++)
        {
            string ra = string.Join(";", a.Index.Rounds[r].Runs), rb = string.Join(";", b.Index.Rounds[r].Runs);
            if (ra != rb)
            {
                Console.WriteLine($"round {a.Index.Rounds[r].Number} runs differ:\n  sampler {ra}\n  observer {rb}");
                break;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(observer.Misaligned).IsEqualTo(0);
            if (hostAlive)
            {
                await Assert.That(id).IsEqualTo(ia).Because("with liveness and pawn order read, the index is the walk's");
                await Assert.That(pd).IsEqualTo(pa).Because("with liveness and pawn order read, the positions are the walk's");
            }
        }
    }

    private sealed class Recorder(ParsedDemo parsed, int stride) : IFrameObserver
    {
        public List<PositionSample> Samples { get; } = [];
        public List<int> Index { get; } = [];
        public int Frames { get; private set; }
        public int Misaligned { get; private set; }

        public void OnFrame(FrameView view)
        {
            Frames++;
            int i = view.FrameIndex;
            if (i >= parsed.Frames.Count || !ReferenceEquals(view.Frame, parsed.Frames[i]))
            {
                Misaligned++;
            }

            if (i % stride != 0)
            {
                return;
            }

            int tick = view.Frame.ServerTick;
            for (int slot = 0; slot < 64; slot++)
            {
                if (!view.TryReadFloat(Declared[0], slot, out float x) || !view.TryReadFloat(Declared[1], slot, out float y)
                                                                     || !view.TryReadFloat(Declared[2], slot, out float z))
                {
                    continue;
                }

                view.TryReadString(Declared[3], slot, out string? place);
                bool alive = _hostAlive
                    ? view.TryReadInt("dvprobe.pawn.alive", slot, out int a) && a != 0
                    : view.TryReadInt(Declared[4], slot, out _);
                Samples.Add(new PositionSample(i, tick, slot, new System.Numerics.Vector3(x, y, z), place, view.Team(slot), alive));
                Index.Add(_hostAlive && view.TryReadInt("dvprobe.pawn.index", slot, out int e) ? e : int.MaxValue);
            }
        }

        public void Finish()
        {
        }
    }
}
