#region

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace DemoViewer.NET.EntityMicroBench;

internal static class Program
{
    // EntityMicroBench — BenchmarkDotNet harness for the per-query entity-access primitives that the
    // AnalysisBench per-phase profiler cannot isolate at nanosecond resolution.
    //
    // Scope note: `BuildFieldDescs` was also a candidate target, but profiling measured it
    // directly at ~229 ms across 73 builds for a full demo (~3 ms/build, run once per distinct class)
    // — i.e. it is not a hot path, and a micro-bench would add no signal while requiring a fresh
    // schema-bound tracker per iteration (multi-second seeks) plus exposing a private static. It is
    // therefore deliberately omitted; the AnalysisBench `descriptor build` line is the canonical
    // measurement. The clean public hot-path getters below are what a future optimizer would tune.
    public static void Main(string[] args)
    {
        // No args → run every [Benchmark] with the default job (BenchmarkSwitcher would otherwise
        // drop into an interactive prompt and do nothing under a closed stdin). With args, defer to
        // the switcher so the standard BDN CLI works — e.g. `-- --job short --filter *ResolveHandle*`.
        if (args.Length == 0)
        {
            BenchmarkRunner.Run<EntityTrackingBenchmarks>();
            return;
        }

        BenchmarkSwitcher.FromTypes([typeof(EntityTrackingBenchmarks), typeof(ParallelDecodeBenchmarks)]).Run(args);
    }
}

/// <summary>
///     Micro-benchmarks for <see cref="EntityTracker" />'s public per-query getters, run against a
///     real demo seeked to mid-game so pawns and controllers are live in PVS.
///     <list type="bullet">
///         <item><c>Get&lt;T&gt;</c> — live-aliasing typed wrapper (no copy).</item>
///         <item>
///             <c>Snapshot&lt;T&gt;</c> — detached deep copy (exercises the internal
///             <c>EntityState.FreezeCopy</c> + factory + recursive-freeze hook).
///         </item>
///         <item><c>ResolveHandle&lt;T&gt;</c> — handle → slot mask → <c>Get&lt;T&gt;</c>.</item>
///     </list>
/// </summary>
[MemoryDiagnoser]
public class EntityTrackingBenchmarks
{
    private readonly PawnHealthProvider _healthProvider = new();
    private int _controllerHandle;
    private int _controllerSlot = -1;
    private EntityStateLayer _layer = null!;
    private int _pawnSlot = -1;
    private EntityTracker _tracker = null!;

    [GlobalSetup]
    public void Setup()
    {
        string demoPath = FindSmallestDemo();
        byte[] bytes = File.ReadAllBytes(demoPath);
        ParsedDemo demo = DemoParser.Parse(bytes);

        // EntityStateLayer binds the Lens resolver + typed-wrapper factories (same bootstrap as
        // production) and seeks forward-only, so reusing it is the least-code way to obtain a
        // fully-wired tracker positioned mid-demo.
        _layer = new EntityStateLayer(demo.Frames);
        int midTick = demo.Frames[demo.Frames.Count / 2].ServerTick;
        _layer.SeekToTick(midTick);
        _tracker = _layer.Tracker;
        // Register the SDK-wrapper factories so Get<T>/Snapshot<T>/ResolveHandle<T> dispatch
        // (the local generated registry is gone since the SDK cutover).
        SdkEntityWorlds.For(_tracker);

        foreach ((int idx, EntityState ent) in _tracker.CurrentEntities.AllIndexed())
        {
            if (_pawnSlot < 0 && ent.ClassName == "CCSPlayerPawn")
            {
                _pawnSlot = idx;
            }
            else if (_controllerSlot < 0 && ent.ClassName == "CCSPlayerController")
            {
                _controllerSlot = idx;
            }
        }

        if (_pawnSlot < 0 || _controllerSlot < 0)
        {
            throw new InvalidOperationException(
                $"GlobalSetup found pawn={_pawnSlot}, controller={_controllerSlot} at tick {midTick}; " +
                "pick a different seek tick where both are live in PVS.");
        }

        // ResolveHandle only consults the low 14 bits (the entity index); encoding the controller
        // slot as a bare handle is enough to drive the slot lookup it performs.
        _controllerHandle = _controllerSlot;
    }

    [Benchmark]
    public CSPlayerPawn? Get() => _tracker.Get<CSPlayerPawn>(_pawnSlot);

    [Benchmark]
    public CSPlayerPawn? SnapshotFreezeCopy() => _tracker.Snapshot<CSPlayerPawn>(_pawnSlot);

    [Benchmark]
    public CSPlayerController? ResolveHandle() => _tracker.ResolveHandle<CSPlayerController>(_controllerHandle);

    // Hot-path targets — the per-frame entity-set walk that profiling flagged as the
    // dominant scanner cost (pre-frame snapshot). ForEachLivePawn is the raw walk
    // (foreach CurrentEntities + ClassName.Contains); CaptureAllSlots adds the per-pawn health read
    // and the emit closure, i.e. exactly what the scanner does each frame per per-player provider.

    [Benchmark]
    public int ForEachLivePawn()
    {
        int n = 0;
        PawnLookup.ForEachLivePawn(_tracker, (_, _) => n++);
        return n;
    }

    [Benchmark]
    public int CaptureAllSlots()
    {
        int n = 0;
        _healthProvider.CaptureAllSlots(_layer, (_, _) => n++);
        return n;
    }

    internal static string FindSmallestDemo()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "demos", "benchmarks");
            if (Directory.Exists(candidate))
            {
                string[] demos = Directory.GetFiles(candidate, "*.dem");
                if (demos.Length == 0)
                {
                    throw new FileNotFoundException($"No .dem files in {candidate}");
                }

                return demos.OrderBy(f => new FileInfo(f).Length).First();
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate demos/benchmarks from " + AppContext.BaseDirectory);
    }
}

/// <summary>
///     F-7 cross-check: an INDEPENDENT BenchmarkDotNet measurement of the parallel digest producer's
///     allocation. <see cref="MemoryDiagnoserAttribute" /> reports process-wide managed bytes/op, which
///     does NOT depend on the (now-fixed) per-worker <c>GetAllocatedBytesForCurrentThread</c> accountant
///     inside <c>ParallelDigestProducer</c> — so it independently confirms the precompute really
///     allocates GiB-scale, not the small fraction the pre-fix calling-thread bracket reported. It is a
///     magnitude check, NOT a reproduction of AnalysisBench's full-provider figure: this minimal scanner
///     uses fewer providers, so its digest-build allocation (and thus the bytes/op) runs a bit lower —
///     that gap is expected and not a discrepancy to chase. Each op decodes the whole demo (~seconds),
///     so this is deliberately NOT in the no-arg default run; invoke it explicitly, e.g.
///     <c>-- --filter *Precompute* --job short</c>.
/// </summary>
[MemoryDiagnoser]
public class ParallelDecodeBenchmarks
{
    private IReadOnlyList<DemoFrame> _frames = null!;
    private EntityChangeScanner _scanner = null!;

    [GlobalSetup]
    public void Setup()
    {
        byte[] bytes = File.ReadAllBytes(EntityTrackingBenchmarks.FindSmallestDemo());
        ParsedDemo demo = DemoParser.Parse(bytes);
        _frames = demo.Frames;

        // Minimal scanner: empty singleton providers + one representative per-player provider. The
        // parallel ENTITY-STREAM decode (the dominant allocator the F-7 fix accounts for) runs
        // regardless of the provider set, so this exercises ParallelDigestProducer.Produce faithfully
        // without dragging in the YAML config / rule-chain builder.
        EntityStateLayer layer = new(demo.Frames);
        _scanner = new EntityChangeScanner(layer, [], [new PawnHealthProvider()]);
    }

    [Benchmark]
    public void Precompute() => _scanner.PrecomputeParallelDigests(_frames);
}
