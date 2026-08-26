#region

using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The <c>.dvann.json</c> sidecar: where it lands, what it records about identity, and the two
///     degraded paths that must NOT lose a user's work — a foreign file at the same path, and a sidecar
///     authored against a different parse.
/// </summary>
[NotInParallel]
public class AnnotationStoreTests
{
    [Test]
    public async Task Save_WritableDemoDir_WritesSidecarBesideDemo()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        await Assert.That(store.ResolveLocation(tree.DemoPath))
            .IsEqualTo(AnnotationStoreLocation.DemoSidecar);

        bool saved = await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock,
            [AnnotationFakes.Stroke()]);

        await Assert.That(saved).IsTrue();
        await Assert.That(File.Exists(tree.DemoPath + AnnotationStore.SidecarExtension)).IsTrue();
    }

    [Test]
    public async Task Save_UnwritableDemoDir_FallsBackToAppData()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        // A demo whose directory does not exist stands in for the read-only Steam replay folder: the
        // probe fails the same way, which is the branch under test.
        string unreachable = Path.Combine(tree.Root, "no-such-folder", "match.dem");

        await Assert.That(store.ResolveLocation(unreachable)).IsEqualTo(AnnotationStoreLocation.AppData);

        bool saved = await store.SaveAsync(unreachable, tree.Demo, tree.Clock,
            [AnnotationFakes.Stroke()]);

        await Assert.That(saved).IsTrue();
        await Assert.That(store.ResolvePath(unreachable)!.StartsWith(tree.AppData, StringComparison.Ordinal))
            .IsTrue();
        await Assert.That(File.Exists(store.ResolvePath(unreachable)!)).IsTrue();
    }

    [Test]
    public async Task NoAppDataRoot_AndUnwritableDir_IsNotPersistent()
    {
        using TempTree tree = new();
        AnnotationStore store = new(null);
        string unreachable = Path.Combine(tree.Root, "no-such-folder", "match.dem");

        await Assert.That(store.IsPersistent).IsFalse();
        await Assert.That(store.ResolveLocation(unreachable)).IsEqualTo(AnnotationStoreLocation.None);
        await Assert.That(store.ResolvePath(unreachable)).IsNull();
        await Assert.That(await store.SaveAsync(unreachable, tree.Demo, tree.Clock, [])).IsFalse();
    }

    [Test]
    public async Task RoundTrip_PreservesElements_Exactly()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        AnnotationElement world = AnnotationFakes.Stroke(space: new SpaceRef.World(-384),
            time: new TimeEnvelope(640, 1280, 8, 16),
            style: new AnnotationStyle(0xC0FF8800, 11.5f, 0.8f, true));
        AnnotationElement entity = AnnotationFakes.Stroke(
            space: new SpaceRef.Entity(76561198000000042, -12.5f, 7.25f));

        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [world, entity]);
        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.Elements.Count).IsEqualTo(2);
        await Assert.That(loaded.Elements[0]).IsEqualTo(world);
        await Assert.That(loaded.Elements[1]).IsEqualTo(entity);
        await Assert.That(loaded.DemoMismatch).IsFalse();
        await Assert.That(loaded.ClockMismatch).IsFalse();
        await Assert.That(loaded.SchemaVersion).IsEqualTo(AnnotationStore.SchemaVersion);
    }

    /// <summary>
    ///     A real-time stroke's authoring cadence survives a save/load EXACTLY — same runs, same order,
    ///     same duration.
    ///     <para>
    ///         The equality is not incidental: <c>AnnotationElement.Equals</c> compares
    ///         <c>Timing</c> structurally and <c>StrokeTiming.Equals</c> walks the run table, precisely so
    ///         that a writer which dropped or re-ordered a boundary fails here instead of passing every
    ///         save/load test while silently changing when the stroke replays.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RoundTrip_StrokeTiming_SurvivesExactly()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        // A boundary per speed change: drawn, paused (41 twice), drawn, paused, drawn. The repeated
        // sample index IS the pause, and it is the shape a naive "sort and de-duplicate" would destroy.
        StrokeTiming timing = new(
            [new TimingRun(0, 0), new TimingRun(41, 96), new TimingRun(41, 160),
                new TimingRun(78, 214), new TimingRun(78, 300), new TimingRun(119, 372)],
            372);

        AnnotationElement stroke = Timed(timing, 120);
        AnnotationElement plain = AnnotationFakes.Stroke();

        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [stroke, plain]);
        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.Elements.Count).IsEqualTo(2);
        await Assert.That(loaded.Elements[0]).IsEqualTo(stroke)
            .Because("the element compares its cadence structurally, so this covers order and duration");

        StrokeTiming? round = loaded.Elements[0].Timing;
        await Assert.That(round).IsNotNull();
        await Assert.That(round!.DurationTicks).IsEqualTo(372);
        await Assert.That(round.Runs.Count).IsEqualTo(6);
        await Assert.That(round.Runs[2]).IsEqualTo(new TimingRun(41, 160))
            .Because("a repeated sample index is a PAUSE — reordering or collapsing it is data loss");
        await Assert.That(loaded.Elements[1].Timing).IsNull()
            .Because("a cadence belongs to the element that has one, not to the document");
    }

    /// <summary>
    ///     An element with no cadence writes NO <c>timing</c> field. That is the whole reason the DTO
    ///     property is nullable: the writer's <c>WhenWritingNull</c> then leaves the published v1 schema
    ///     byte-identical, which <c>AnnotationSchemaSnapshotTests</c> pins from the other side.
    /// </summary>
    [Test]
    public async Task Save_AnElementWithNoCadence_WritesNoTimingField()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [AnnotationFakes.Stroke()]);

        string path = store.ResolvePath(tree.DemoPath)!;
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement first = json.RootElement.GetProperty("elements")[0];

        await Assert.That(first.TryGetProperty("timing", out JsonElement _)).IsFalse()
            .Because("a nullable DTO field plus WhenWritingNull is what keeps v1 documents unchanged");
        await Assert.That(File.ReadAllText(path).Contains("timing", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>A v1-shaped document — written before the field existed — loads with no cadence and no fuss.</summary>
    [Test]
    public async Task Load_AV1ShapedDocument_HasNoTiming_AndNoError()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        AnnotationElement element = AnnotationFakes.Stroke(time: new TimeEnvelope(640, 960, 8, 16));
        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [element]);

        // The document under test is one this build wrote for a cadence-less element, and the assertion
        // below is what makes that the same thing as a document written before the field existed.
        string path = store.ResolvePath(tree.DemoPath)!;
        await Assert.That(File.ReadAllText(path).Contains("\"timing\"", StringComparison.Ordinal))
            .IsFalse();

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.Elements.Count).IsEqualTo(1);
        await Assert.That(loaded.Elements[0].Timing).IsNull();
        await Assert.That(loaded.Elements[0]).IsEqualTo(element);
    }

    /// <summary>
    ///     <b>The forward-compatibility case, asserted rather than assumed.</b> A build that predates
    ///     <c>timing</c> has no property to bind it to — it lands in <c>[JsonExtensionData]</c>, and is
    ///     written back out on the next save. So a user can open a real-time stroke in an older build,
    ///     edit something else, save, and still have the cadence when they come back.
    ///     <para>
    ///         The "older build" here is a DTO shaped exactly like the v1 element: every known field, an
    ///         extension bag, and no notion of a cadence. Round-tripping through it is what a v1 build's
    ///         load → edit → save does, and the assertion at the end is against the REAL store, so what
    ///         is proven is that a live <c>StrokeTiming</c> comes back out the far side.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ADocumentRoundTrippedByAV1Reader_StillCarriesTheCadence()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        StrokeTiming timing = new(
            [new TimingRun(0, 0), new TimingRun(30, 64), new TimingRun(30, 192), new TimingRun(59, 260)],
            260);
        AnnotationElement stroke = Timed(timing, 60);

        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [stroke]);
        string path = store.ResolvePath(tree.DemoPath)!;

        // The old build opens it, changes the ink colour, and saves.
        V1Document? v1 = JsonSerializer.Deserialize<V1Document>(File.ReadAllText(path), _v1Json);
        await Assert.That(v1).IsNotNull();
        await Assert.That(v1!.Elements![0].Extra!.ContainsKey("timing")).IsTrue()
            .Because("a field with no property to bind to is exactly what the extension bag is for");

        v1.Elements[0].ColorArgb = 0xFF00FF00;
        File.WriteAllText(path, JsonSerializer.Serialize(v1, _v1Json));

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.Elements.Count).IsEqualTo(1);
        await Assert.That(loaded.Elements[0].Style.ColorArgb).IsEqualTo(0xFF00FF00u)
            .Because("the old build's own edit has to have landed, or this proves nothing about saving");
        await Assert.That(loaded.Elements[0].Timing).IsEqualTo(timing)
            .Because("the cadence went out through a reader that has never heard of it and came back "
                     + "whole — which is the promise annotations-format.md makes to third parties");
    }

    /// <summary>
    ///     A hand-edited table with an odd number of values is a TRUNCATED pair. The orphan goes and
    ///     everything before it stays, matching <c>StrokeTiming</c>'s own contract that a short table
    ///     degrades rather than throws.
    /// </summary>
    [Test]
    public async Task Load_ATruncatedRunTable_DropsTheOrphan_AndKeepsTheRest()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock,
            [Timed(new StrokeTiming([new TimingRun(0, 0), new TimingRun(40, 128)], 128), 50)]);

        // Rewritten by span rather than by Replace: the run table and the duration hold the same numbers
        // (a duration IS the last offset), so a textual substitution would land in whichever came first.
        string path = store.ResolvePath(tree.DemoPath)!;
        string text = File.ReadAllText(path);
        int start = text.IndexOf("\"runs\": [", StringComparison.Ordinal);
        int end = text.IndexOf(']', start);
        File.WriteAllText(path, text[..start] + "\"runs\": [0, 0, 40, 128, 77" + text[end..]);

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.Elements.Count).IsEqualTo(1);
        await Assert.That(loaded.Elements[0].Timing!.Runs.Count).IsEqualTo(2);
        await Assert.That(loaded.Elements[0].Timing!.Runs[1]).IsEqualTo(new TimingRun(40, 128));
    }

    /// <summary>
    ///     The cadence costs O(boundaries), never O(samples) — which is the entire reason plan D7 §2
    ///     chose a sparse run table over a fourth float on every <c>InkPoint</c> (+0.9 % against +26 %).
    ///     <para>
    ///         Asserted as a SHAPE rather than a byte budget: the same run table on a stroke four times
    ///         as long must cost exactly the same number of bytes. A percentage would drift with the
    ///         writer's indentation; this cannot pass for an encoding that stamps points.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheRunTable_CostsBytesPerBoundary_NotPerSample()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        StrokeTiming timing = new(
            [new TimingRun(0, 0), new TimingRun(97, 128), new TimingRun(97, 260),
                new TimingRun(211, 372), new TimingRun(211, 500), new TimingRun(399, 640)],
            640);

        (long shortWith, long shortWithout) = await Measure(store, tree, timing, 400);
        (long longWith, long longWithout) = await Measure(store, tree, timing, 1600);

        long shortDelta = shortWith - shortWithout;
        long longDelta = longWith - longWithout;

        Console.WriteLine(
            $"[timing-size] 400 samples: {shortWithout} B → {shortWith} B (+{shortDelta} B, "
            + $"{100.0 * shortDelta / shortWithout:F2} %) · 1600 samples: {longWithout} B → {longWith} B "
            + $"(+{longDelta} B, {100.0 * longDelta / longWithout:F2} %)");

        await Assert.That(longDelta).IsEqualTo(shortDelta)
            .Because("a six-boundary table costs the same on a 400-sample stroke as on a 1600-sample "
                     + "one; anything else means the encoding grew a per-point stamp");
        await Assert.That(shortDelta).IsGreaterThan(0);
    }

    private static async Task<(long With, long Without)> Measure(
        AnnotationStore store, TempTree tree, StrokeTiming timing, int samples)
    {
        string path = store.ResolvePath(tree.DemoPath)!;

        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [Timed(timing, samples)]);
        long with = new FileInfo(path).Length;

        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [Timed(null, samples)]);
        long without = new FileInfo(path).Length;

        return (with, without);
    }

    // A stroke long enough to be realistic, with a fixed id so the two measurements above differ only
    // by the cadence. The samples are a lissajous rather than a line so the coordinates have the digit
    // count real ink does — a stroke of round numbers would understate the document it is a fraction of.
    private static AnnotationElement Timed(StrokeTiming? timing, int samples)
    {
        InkPoint[] points = new InkPoint[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = i * 0.031;
            points[i] = new InkPoint(
                (float)(Math.Sin(t) * 612.25), (float)(Math.Cos(t * 1.7) * 488.5),
                0.5f + ((i % 7) * 0.03f));
        }

        return new AnnotationElement(
            Guid.Parse("33333333-3333-4333-8333-333333333333"), AnnotationKind.Freehand,
            AnnotationStyle.Default, new SpaceRef.World(-384), new TimeEnvelope(640, 960, 8, 16),
            points, null, timing);
    }

    private static readonly JsonSerializerOptions _v1Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>A document exactly as a build that predates <c>timing</c> models it: no such property.</summary>
    private sealed class V1Document
    {
        public int SchemaVersion { get; set; }

        public JsonElement Demo { get; set; }

        public JsonElement Clock { get; set; }

        public List<V1Element>? Elements { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    /// <summary>The v1 element: every field that build knew, plus the bag everything else lands in.</summary>
    private sealed class V1Element
    {
        public string? Id { get; set; }

        public string? Kind { get; set; }

        public uint ColorArgb { get; set; }

        public float WidthWorld { get; set; }

        public float Opacity { get; set; }

        public bool RevealOnFadeIn { get; set; }

        public string? Space { get; set; }

        public double LevelMinZ { get; set; }

        public ulong SteamId { get; set; }

        public float Dx { get; set; }

        public float Dy { get; set; }

        public int? FromTick { get; set; }

        public int? UntilTick { get; set; }

        public int FadeInTicks { get; set; }

        public int FadeOutTicks { get; set; }

        public List<float>? Points { get; set; }

        public string? Text { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    /// <summary>
    ///     The tolerant-reader half of the format contract: a field written by a NEWER build must survive
    ///     being loaded, edited and saved by this one, at both the root and the element level.
    /// </summary>
    [Test]
    public async Task RoundTrip_PreservesUnknownFields_RootAndElement()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        AnnotationElement element = AnnotationFakes.Stroke();
        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [element]);

        string path = store.ResolvePath(tree.DemoPath)!;
        InjectUnknownFields(path, element.Id);

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);
        await Assert.That(loaded.Elements.Count).IsEqualTo(1);

        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, loaded.Elements);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        await Assert.That(json.RootElement.TryGetProperty("futureRootField", out JsonElement root)).IsTrue();
        await Assert.That(root.GetString()).IsEqualTo("kept");

        JsonElement first = json.RootElement.GetProperty("elements")[0];
        await Assert.That(first.TryGetProperty("futureElementField", out JsonElement onElement)).IsTrue();
        await Assert.That(onElement.GetInt32()).IsEqualTo(7);
    }

    [Test]
    public async Task Load_UnknownSchemaVersion_LoadsTolerantly()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);
        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [AnnotationFakes.Stroke()]);

        string path = store.ResolvePath(tree.DemoPath)!;
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1",
            "\"schemaVersion\": 99", StringComparison.Ordinal));

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.SchemaVersion).IsEqualTo(99);
        await Assert.That(loaded.Elements.Count).IsEqualTo(1)
            .Because("a newer schema is read for what this build understands, never rejected wholesale");
    }

    /// <summary>
    ///     <b>A reserved <c>AnnotationKind</c> in a hand-edited sidecar used to kill the eraser.</b> The
    ///     store parsed any member of the enum; <c>AnnotationHitTester</c> throws
    ///     <c>NotSupportedException</c> for everything but <c>Freehand</c> — correctly, it is an internal
    ///     contract — and <c>EraseTool</c> has no catch, so the throw escaped into Avalonia's pointer
    ///     pipeline on the first erase drag over that stroke. <c>LevelLayouts.Parse</c> fences its own
    ///     reserved member for exactly this reason; the store now does the same.
    ///     <para>
    ///         The drag is part of the test on purpose: asserting only on the loaded <c>Kind</c> proves
    ///         the fence, not the thing the fence exists to prevent.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("Arrow")]
    [Arguments("Text")]
    [Arguments("4")]
    [Arguments("99")]
    public async Task Load_AReservedKind_BecomesFreehand_SoTheEraserSurvivesIt(string edited)
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);
        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [AnnotationFakes.Stroke()]);

        string path = store.ResolvePath(tree.DemoPath)!;
        File.WriteAllText(path, File.ReadAllText(path)
            .Replace("\"kind\": \"Freehand\"", $"\"kind\": \"{edited}\"", StringComparison.Ordinal));

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.Elements.Count).IsEqualTo(1);
        await Assert.That(loaded.Elements[0].Kind).IsEqualTo(AnnotationKind.Freehand)
            .Because("the points are a polyline either way, so loading it as Freehand keeps the stroke");

        AnnotationSession session = new(new AnnotationDocument());
        session.Document.Reset(loaded.Elements);

        LevelPane pane = AnnotationFakes.Pane(400, 400);

        // Straight over the stroke's middle sample — the drag that used to throw out of the pointer
        // pipeline rather than erase anything.
        EraseOver(session, pane, new SKPoint(40, 10));

        await Assert.That(session.Document.Elements).IsEmpty();
    }

    // ToolPointerEvent is a ref struct, so the gesture is driven from a non-async method (the same
    // reason EraseToolTests wraps its samples in a harness).
    private static void EraseOver(AnnotationSession session, LevelPane pane, SKPoint world)
    {
        FakeToolServices services = new(session, pane);
        EraseTool eraser = new();
        ToolPointerEvent over = AnnotationFakes.Press(pane, world);

        eraser.OnPressed(in over, services);
        eraser.OnReleased(in over, services);
    }

    [Test]
    public async Task Load_TruncatedJson_ReturnsEmpty_DoesNotThrow()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);
        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [AnnotationFakes.Stroke()]);

        string path = store.ResolvePath(tree.DemoPath)!;
        string text = File.ReadAllText(path);
        File.WriteAllText(path, text[..(text.Length / 2)]);

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.Elements).IsEmpty();
        await Assert.That(loaded.DemoMismatch).IsFalse();
    }

    [Test]
    public async Task Load_NoFile_ReturnsEmpty()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.Elements).IsEmpty();
        await Assert.That(loaded.Location).IsEqualTo(AnnotationStoreLocation.DemoSidecar);
    }

    /// <summary>
    ///     A sidecar whose demo hash names a different demo belongs to someone else's file that happens
    ///     to share this path. It is ignored — and, critically, the next save must not silently overwrite
    ///     their annotations.
    /// </summary>
    [Test]
    public async Task Load_DemoHashMismatch_IgnoresSidecar_AndPreservesTheirWork()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        DemoIdentity stranger = new(new string('a', 64), "other.dem", 1234);
        await store.SaveAsync(tree.DemoPath, stranger, tree.Clock, [AnnotationFakes.Stroke()]);
        string path = store.ResolvePath(tree.DemoPath)!;
        string before = File.ReadAllText(path);

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.DemoMismatch).IsTrue();
        await Assert.That(loaded.Elements).IsEmpty();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before)
            .Because("loading must never rewrite a file it decided not to trust");
    }

    /// <summary>
    ///     Plan decision D10. A clock mismatch is a WARNING, not a discard: static elements are unaffected
    ///     by the clock at all, and throwing away a session's telestration because a re-parse produced a
    ///     different frame count would be the worst possible response.
    /// </summary>
    [Test]
    public async Task Load_ClockMismatch_LoadsWithFlag_StaticElementsIntact()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        AnnotationElement stat = AnnotationFakes.Stroke();
        AnnotationElement anchored = AnnotationFakes.Stroke(time: new TimeEnvelope(500, 900, 0, 0));
        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [stat, anchored]);

        ClockIdentity reparsed = tree.Clock with
        {
            FrameCount = tree.Clock.FrameCount + 17
        };

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, reparsed);

        await Assert.That(loaded.ClockMismatch).IsTrue();
        await Assert.That(loaded.Elements.Count).IsEqualTo(2);
        await Assert.That(loaded.Elements[0]).IsEqualTo(stat);
        await Assert.That(loaded.Elements[1]).IsEqualTo(anchored);
    }

    [Test]
    public async Task Load_UnknownClock_IsNotAMismatch()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);
        await store.SaveAsync(tree.DemoPath, tree.Demo, ClockIdentity.Unknown, [AnnotationFakes.Stroke()]);

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);

        await Assert.That(loaded.ClockMismatch).IsFalse()
            .Because("a caller that could not supply a clock must not produce a warning banner");
    }

    [Test]
    public async Task Save_IsAtomic_NoTempFileLeftBehind()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);

        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [AnnotationFakes.Stroke()]);
        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock,
            [AnnotationFakes.Stroke(), AnnotationFakes.Stroke()]);

        string path = store.ResolvePath(tree.DemoPath)!;
        await Assert.That(File.Exists(path + ".tmp")).IsFalse();

        AnnotationLoadResult loaded = await store.LoadAsync(tree.DemoPath, tree.Clock);
        await Assert.That(loaded.Elements.Count).IsEqualTo(2);
    }

    /// <summary>Plan decision D12: a failed write is a status string, never an exception mid-gesture.</summary>
    [Test]
    public async Task Save_OnIoFailure_ReturnsFalse_DoesNotThrow()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);
        string path = store.ResolvePath(tree.DemoPath)!;

        // Put a DIRECTORY where the sidecar goes, so the atomic replace at the end of SaveAsync cannot
        // complete. The obvious injection — holding the destination open with FileShare.None — is a
        // Windows-only fact: share modes are mandatory there and merely advisory on Unix, where
        // rename(2) happily replaces a file somebody else has open and the save reported success. A
        // directory is refused by both (EISDIR / ERROR_ACCESS_DENIED), so this exercises the same
        // failure at the same line on every OS the suite runs on.
        Directory.CreateDirectory(path);

        bool saved = await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock,
            [AnnotationFakes.Stroke()]);

        await Assert.That(saved).IsFalse();
        await Assert.That(File.Exists(path + ".tmp")).IsFalse();
    }

    [Test]
    public async Task Delete_RemovesTheSidecar()
    {
        using TempTree tree = new();
        AnnotationStore store = new(tree.AppData);
        await store.SaveAsync(tree.DemoPath, tree.Demo, tree.Clock, [AnnotationFakes.Stroke()]);

        await Assert.That(await store.DeleteAsync(tree.DemoPath)).IsTrue();
        await Assert.That(File.Exists(store.ResolvePath(tree.DemoPath)!)).IsFalse();
        await Assert.That(await store.DeleteAsync(tree.DemoPath)).IsFalse();
    }

    [Test]
    public async Task DemoKeyResolver_IsInjected_AndUsedForTheAppDataPath()
    {
        using TempTree tree = new();
        int calls = 0;
        AnnotationStore store = new(tree.AppData, _ =>
        {
            calls++;
            return "cafebabe";
        });

        string unreachable = Path.Combine(tree.Root, "no-such-folder", "match.dem");
        string? path = store.ResolvePath(unreachable);

        await Assert.That(path!.EndsWith("cafebabe" + AnnotationStore.SidecarExtension,
            StringComparison.Ordinal)).IsTrue();
        await Assert.That(calls).IsGreaterThan(0)
            .Because("the App passes its cached hash in; nothing here may hash on the UI thread");
    }

    private static void InjectUnknownFields(string path, Guid elementId)
    {
        using JsonDocument source = JsonDocument.Parse(File.ReadAllText(path));

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
               {
                   Indented = true
               }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in source.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "elements", StringComparison.Ordinal))
                {
                    property.WriteTo(writer);
                    continue;
                }

                writer.WritePropertyName("elements");
                writer.WriteStartArray();
                foreach (JsonElement element in property.Value.EnumerateArray())
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty field in element.EnumerateObject())
                    {
                        field.WriteTo(writer);
                    }

                    if (string.Equals(element.GetProperty("id").GetString(), elementId.ToString("D"),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteNumber("futureElementField", 7);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteString("futureRootField", "kept");
            writer.WriteEndObject();
        }

        File.WriteAllBytes(path, buffer.ToArray());
    }

    /// <summary>A throwaway demo file, an app-data root, and the identities that go with them.</summary>
    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "dvann-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "demos"));
            AppData = Path.Combine(Root, "appdata");
            Directory.CreateDirectory(AppData);

            DemoPath = Path.Combine(Root, "demos", "match.dem");
            File.WriteAllBytes(DemoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00]);

            Demo = AnnotationStore.IdentityFor(DemoPath);
            Clock = new ClockIdentity(ClockIdentity.DvFrameClock, 64, 12_345, 128, 49_500);
        }

        public string Root { get; }

        public string AppData { get; }

        public string DemoPath { get; }

        public DemoIdentity Demo { get; }

        public ClockIdentity Clock { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A temp tree that outlives the test is noise, not a failure.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
