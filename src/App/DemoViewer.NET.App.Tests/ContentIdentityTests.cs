#region

using System.Security.Cryptography;
using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Playback;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Content identity: one hash helper behind every per-demo store, a hash on every index row once tier 2
///     has run, the reverse lookup that turns a hash back into a path, and the open demo's hash on the
///     module context so nothing hashes twice.
/// </summary>
public class ContentIdentityTests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-identity-{Guid.NewGuid():N}");

    private static DemoCacheRecord Record(string path, string? sha) => new()
    {
        Path = path,
        Size = 1000,
        ModifiedTicks = 2000,
        Sha256 = sha,
        Map = "de_nuke"
    };

    [Test]
    public async Task TheBreakpointKey_IsTheSharedHelper()
    {
        byte[] bytes = new byte[70_001];
        new Random(7).NextBytes(bytes);
        string expected = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using (Assert.Multiple())
        {
            await Assert.That(GraphBreakpointStore.ComputeDemoKey(bytes)).IsEqualTo(expected)
                .Because("GraphBreakpoints.v2.json on disk is keyed by exactly this string");
            await Assert.That(DemoContentHash.Compute(bytes)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task TryGetIndexBySha256_RoundTripsAcrossReopen_AndFollowsUpsertAndRemove()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Record("/demos/b.dem", "sha-1"));
            store.Upsert(Record("/demos/a.dem", "sha-1")); // a byte-identical copy
            store.Upsert(Record("/demos/c.dem", null)); // not yet at tier 2
            store.SaveIndex();

            await Assert.That(store.TryGetIndexBySha256("sha-1")!.Path).IsEqualTo("/demos/a.dem")
                .Because("two rows under one hash are a copied demo, and the smallest path is the library's primary");

            DemoCacheStore reopened = new(root);
            using (Assert.Multiple())
            {
                await Assert.That(reopened.TryGetIndexBySha256("sha-1")!.Path).IsEqualTo("/demos/a.dem")
                    .Because("the reverse map is rebuilt from index.json, not only from live upserts");
                await Assert.That(reopened.TryGetIndex("/demos/c.dem")!.Sha256).IsNull();
                await Assert.That(reopened.TryGetIndexBySha256("sha-none")).IsNull();
                await Assert.That(reopened.TryGetIndexBySha256("")).IsNull();
            }

            reopened.Remove("/demos/a.dem");
            await Assert.That(reopened.TryGetIndexBySha256("sha-1")!.Path).IsEqualTo("/demos/b.dem")
                .Because("removing the primary falls back to the remaining copy");

            // The file at b's path was replaced and re-indexed: the old hash must not keep pointing at it.
            reopened.Upsert(Record("/demos/b.dem", "sha-2"));
            using (Assert.Multiple())
            {
                await Assert.That(reopened.TryGetIndexBySha256("sha-1")).IsNull();
                await Assert.That(reopened.TryGetIndexBySha256("sha-2")!.Path).IsEqualTo("/demos/b.dem");
            }

            reopened.Remove("/demos/b.dem");
            await Assert.That(reopened.TryGetIndexBySha256("sha-2")).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     An <c>index.json</c> written before any row carried a hash has no <c>Sha256</c> property at all. It
    ///     must load as "not hashed yet", never as corrupt, or every library would re-index from nothing.
    /// </summary>
    [Test]
    public async Task AnIndexWrittenWithoutTheHashField_StillLoads()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "index.json"),
                """
                {
                  "Version": 1,
                  "LegacyMigrationVersion": 1,
                  "Entries": [
                    { "Path": "/demos/old.dem", "Size": 10, "ModifiedTicks": 20, "Map": "de_mirage",
                      "PlayerNames": ["a", "b"], "RoundCount": 24, "ParseSchema": 1 }
                  ]
                }
                """);

            DemoCacheStore store = new(root);
            DemoCacheIndexEntry? entry = store.TryGetIndex("/demos/old.dem");

            using (Assert.Multiple())
            {
                await Assert.That(entry).IsNotNull();
                await Assert.That(entry!.Sha256).IsNull();
                await Assert.That(entry.Tier).IsEqualTo(DemoCacheTier.Parse);
                await Assert.That(entry.Map).IsEqualTo("de_mirage");
                await Assert.That(store.Index).HasCount(1);
            }

            // And the row is still writable in the new shape: a tier-2 pass fills the hash in place.
            store.Update("/demos/old.dem", 10, 20, r => r.Sha256 = "sha-old");
            store.SaveIndex();
            await Assert.That(new DemoCacheStore(root).TryGetIndexBySha256("sha-old")!.Path)
                .IsEqualTo("/demos/old.dem");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task DemoRef_ProjectsAnIndexRow()
    {
        DemoRef r = DemoRef.From(new DemoCacheIndexEntry
        {
            Path = "/demos/x.dem",
            Sha256 = "sha-x"
        });

        using (Assert.Multiple())
        {
            await Assert.That(r.Path).IsEqualTo("/demos/x.dem");
            await Assert.That(r.StableKey).IsEqualTo(DemoCacheStore.StableKey("/demos/x.dem"));
            await Assert.That(r.Sha256).IsEqualTo("sha-x");
        }
    }

    [Test]
    public async Task TheContext_PublishesTheHashTheShellSets_AndDefaultsToNull()
    {
        // Through the interface: the default member is what a double that never opted in exposes.
        await Assert.That(((IModuleContext)new Playback2DFakeContext()).DemoSha256).IsNull()
            .Because("a double that never opted in reads as no hash");

        ModuleContext context = new(new PlaybackController(), () => null);
        await Assert.That(context.DemoSha256).IsNull();

        context.SetDemoSha256("sha-open");
        await Assert.That(((IModuleContext)context).DemoSha256).IsEqualTo("sha-open");

        context.SetDemoSha256(null);
        await Assert.That(context.DemoSha256).IsNull().Because("unload clears it like the map name");
    }

    [Test]
    public async Task TheContext_ReadsFirstAndLastTickOffTheFrameList()
    {
        await Assert.That(((IModuleContext)new Playback2DFakeContext()).FirstTick).IsEqualTo(0)
            .Because("a double that never opted in reads as no extent, like TotalFrames");

        using PlaybackController controller = new();
        ModuleContext context = new(controller, () => null);
        using (Assert.Multiple())
        {
            await Assert.That(context.FirstTick).IsEqualTo(0);
            await Assert.That(context.LastTick).IsEqualTo(0).Because("no demo, no extent");
        }

        controller.LoadDemo(Frames(1, 64, 64, 128, 132_516), 64);
        using (Assert.Multiple())
        {
            await Assert.That(context.FirstTick).IsEqualTo(1).Because("Valve demos measure 1 on the first frame");
            await Assert.That(context.LastTick).IsEqualTo(132_516).Because("the last frame's tick is TickCount");
        }

        controller.Unload();
        await Assert.That(context.LastTick).IsEqualTo(0);
    }

    /// <summary>
    ///     The one place a frame-clock header is assembled. Every store that writes one calls this, so the
    ///     fill rule (rate, frame count, first and last tick straight off the context) has a single owner.
    /// </summary>
    [Test]
    public async Task FrameClock_FillsTheHeaderFromTheContext()
    {
        Playback2DFakeContext ctx = new()
        {
            TickRate = 128,
            TotalFrames = 154_869,
            FirstTick = 1,
            LastTick = 132_516
        };

        ClockIdentity clock = FrameClock.IdentityFor(ctx);
        using (Assert.Multiple())
        {
            await Assert.That(clock).IsEqualTo(new ClockIdentity(ClockIdentity.DvFrameClock, 128, 154_869, 1, 132_516));
            await Assert.That(clock.Matches(clock with { LastTick = 132_000 })).IsFalse()
                .Because("a real extent is what lets a reparse be told apart from the authored one");
        }

        ctx.TickRate = 0;
        await Assert.That(FrameClock.IdentityFor(ctx).TickRate).IsEqualTo(64)
            .Because("the session divides by the rate, so an unknown one falls back to the shipped 64");
    }

    /// <summary>
    ///     End to end from the context: the tab's attach carries the demo's real extent, not the 0, 0 every
    ///     sidecar used to be written with (which Matches could only read as "unknown, never warn").
    /// </summary>
    [Test]
    public async Task TheTab_AttachesWithTheDemoExtent()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DFakeContext ctx = new()
            {
                TickRate = 64,
                TotalFrames = 2_000,
                FirstTick = 1,
                LastTick = 4_000,
                Gate = new FakeModuleFeatureGate()
            };

            Playback2DTabViewModel vm = new();
            vm.OnActivated(ctx);

            // The attach flushes the previous demo before it takes the clock, so give it a moment.
            ClockIdentity expected = new(ClockIdentity.DvFrameClock, 64, 2_000, 1, 4_000);
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (vm.Annotations.Clock != expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(15);
            }

            await Assert.That(vm.Annotations.Clock).IsEqualTo(expected);
        });
    }

    private static DemoFrame[] Frames(params int[] ticks)
    {
        DemoFrame[] frames = new DemoFrame[ticks.Length];
        for (int i = 0; i < ticks.Length; i++)
        {
            frames[i] = new DemoFrame
            {
                CommandKind = EDemoCommands.DemPacket,
                FrameNumber = i,
                ServerTick = ticks[i],
                HeaderLength = 0,
                RawLength = 0,
                RawStart = 0,
                IsCompressed = false
            };
        }

        return frames;
    }

    /// <summary>
    ///     The library's tier-2 pass hashes EVERY demo, not only the size collisions reconcile hashes, and
    ///     writes the value to the record and the index row. Driven through the tier-2 core directly: a
    ///     folder scan would need the demo linked or copied into a temp library, and demos are never
    ///     moved or linked by a test.
    /// </summary>
    [Test]
    [NotInParallel]
    [Category("RealDemo")]
    public async Task Tier2_HashesAReplaysFolderDemo_AndTheIndexFindsItByHash()
    {
        string path = ResolveDemo();
        ParsedDemo parsed = DemoParser.Parse((await File.ReadAllBytesAsync(path)).AsMemory());
        FileInfo file = new(path);
        DemoEntry entry = new()
        {
            FilePath = path,
            FileName = file.Name,
            Directory = file.DirectoryName ?? "",
            FileSizeBytes = file.Length,
            Modified = file.LastWriteTime
        };

        string libraryJson = Path.Combine(Path.GetTempPath(), $"dv-identity-{Guid.NewGuid():N}.json");
        DemoCacheStore cache = new(null);
        try
        {
            using DemoLibraryService library = new(a => a(), libraryJson, demoCache: cache);
            library.IndexTier2Core(entry, parsed, false);

            string expected = DemoContentHash.Compute(path);
            using (Assert.Multiple())
            {
                await Assert.That(entry.State).IsEqualTo(DemoIndexState.Indexed);
                await Assert.That(cache.TryGetIndex(path)!.Sha256).IsEqualTo(expected)
                    .Because("the index row carries the hash after one tier-2 pass");
                await Assert.That(cache.TryLoadRecord(path)!.Sha256).IsEqualTo(expected);
                await Assert.That(cache.TryGetIndexBySha256(expected)!.Path).IsEqualTo(path)
                    .Because("a store keyed by hash can find the file again");
            }
        }
        finally
        {
            File.Delete(libraryJson);
        }
    }

    // DEMO_PATH names a file for every other real-demo test; here it may also name the Steam replays folder,
    // in which case the smallest demo in it is the subject (the parse dominates the test's cost, not the hash).
    private static string ResolveDemo()
    {
        string? env = Environment.GetEnvironmentVariable("DEMO_PATH");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            string? smallest = new DirectoryInfo(env).EnumerateFiles("*.dem")
                .OrderBy(f => f.Length)
                .Select(f => f.FullName)
                .FirstOrDefault();
            return smallest ?? throw new SkipTestException($"DEMO_PATH folder holds no .dem: {env}");
        }

        return DemoTestHelper.RequireDemo();
    }
}
