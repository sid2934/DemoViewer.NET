#region

using System.Text;
using System.Text.Json;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The v1 sidecar format, pinned by a committed sample. This is what a third party reads when they
///     want to consume DemoViewer annotations, so a field rename here is a compatibility break, and one
///     that would otherwise be invisible, because the store's own round-trip tests would happily agree
///     with themselves about a new spelling.
///     <para>
///         Regenerate deliberately with <c>PB2D_GOLDEN_UPDATE=1</c>, the same switch the image goldens
///         use, and look at the diff before committing it.
///     </para>
/// </summary>
[NotInParallel]
public class AnnotationSchemaSnapshotTests
{
    private const string SampleName = "schema-v1.sample.json";

    [Test]
    public async Task V1Schema_MatchesCheckedInSample()
    {
        string path = Path.Combine(FixtureCorpus.Root, "annotations", SampleName);

        if (Environment.GetEnvironmentVariable("PB2D_GOLDEN_UPDATE") == "1")
        {
            await WriteSample(path);
        }

        if (!File.Exists(path))
        {
            throw new SkipTestException(
                $"missing {path}; regenerate with PB2D_GOLDEN_UPDATE=1");
        }

        // Load → save through the real store and compare the bytes. Field-identical is the assertion:
        // an unknown field that survives, a known field that changes spelling, and a reordering are all
        // things a third-party reader would notice.
        string original = File.ReadAllText(path);

        using TempSidecar sidecar = new(original);
        AnnotationStore store = new(sidecar.AppData, _ => TempSidecar.DemoHash);

        AnnotationLoadResult loaded = await store.LoadAsync(sidecar.DemoPath, TempSidecar.Clock);
        await Assert.That(loaded.Elements.Count).IsEqualTo(2);
        await Assert.That(loaded.DemoMismatch).IsFalse();
        await Assert.That(loaded.ClockMismatch).IsFalse();
        await Assert.That(loaded.SchemaVersion).IsEqualTo(AnnotationStore.SchemaVersion);

        await Assert.That(loaded.Elements[0].Space).IsTypeOf<SpaceRef.World>();
        await Assert.That(loaded.Elements[1].Space).IsTypeOf<SpaceRef.Entity>();

        // The `timing` field is additive and NULLABLE, which is the only reason the byte comparison below
        // still holds: WhenWritingNull emits nothing for an element that has no cadence, and neither of
        // these has one. Named here so a future field that forgets to be nullable fails with a reason
        // rather than as an unexplained golden diff.
        await Assert.That(loaded.Elements.All(e => e.Timing is null)).IsTrue();

        await store.SaveAsync(sidecar.DemoPath, TempSidecar.Demo, TempSidecar.Clock, loaded.Elements);

        string round = File.ReadAllText(sidecar.SidecarPath);
        await Assert.That(Normalize(round)).IsEqualTo(Normalize(original))
            .Because("the v1 sidecar is a published format; a round trip through this build must be " +
                     "field-identical, unknown fields included");
    }

    private static string Normalize(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task WriteSample(string path)
    {
        using TempSidecar sidecar = new(null);
        AnnotationStore store = new(sidecar.AppData, _ => TempSidecar.DemoHash);

        AnnotationElement stat = new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"), AnnotationKind.Freehand,
            new AnnotationStyle(0xFFFFC107, 6f, 1f), new SpaceRef.World(-384), TimeEnvelope.Static,
            [
                new InkPoint(-120.5f, 240.25f, 0.5f), new InkPoint(-60f, 260f, 0.62f),
                new InkPoint(10f, 250f, 0.71f)
            ],
            null);

        AnnotationElement tracked = new(
            Guid.Parse("22222222-2222-4222-8222-222222222222"), AnnotationKind.Freehand,
            new AnnotationStyle(0xC000E5FF, 9.5f, 0.85f, true),
            new SpaceRef.Entity(76561198000000042, 18f, -32f),
            new TimeEnvelope(640, 960, 8, 16),
            [new InkPoint(0f, 0f, 0.5f), new InkPoint(24f, 12f, 0.55f)],
            null);

        await store.SaveAsync(sidecar.DemoPath, TempSidecar.Demo, TempSidecar.Clock, [stat, tracked]);

        // Inject the two forward-compatibility fields the sample exists to prove survive a round trip.
        string text = File.ReadAllText(sidecar.SidecarPath);
        using JsonDocument source = JsonDocument.Parse(text);
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
                bool first = true;
                foreach (JsonElement element in property.Value.EnumerateArray())
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty field in element.EnumerateObject())
                    {
                        field.WriteTo(writer);
                    }

                    if (first)
                    {
                        writer.WriteString("futureElementField", "written by a newer build");
                        first = false;
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteString("futureRootField", "written by a newer build");
            writer.WriteEndObject();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Encoding.UTF8.GetString(buffer.ToArray()));
        Console.WriteLine($"[schema] wrote {path}");
    }

    /// <summary>A temp demo plus a sidecar seeded from the committed sample.</summary>
    private sealed class TempSidecar : IDisposable
    {
        public const string DemoHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        public static readonly DemoIdentity Demo = new(DemoHash, "sample.dem", 8);

        public static readonly ClockIdentity Clock =
            new(ClockIdentity.DvFrameClock, 64, 12345, 128, 49500);

        private readonly string _root;

        public TempSidecar(string? seed)
        {
            _root = Path.Combine(Path.GetTempPath(), "dvann-schema-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "demos"));
            AppData = Path.Combine(_root, "appdata");
            Directory.CreateDirectory(AppData);

            DemoPath = Path.Combine(_root, "demos", "sample.dem");
            File.WriteAllBytes(DemoPath, [0x50, 0x42, 0x44, 0x45, 0x4D, 0x53, 0x32, 0x00]);
            SidecarPath = DemoPath + AnnotationStore.SidecarExtension;

            if (seed is not null)
            {
                File.WriteAllText(SidecarPath, seed);
            }
        }

        public string AppData { get; }

        public string DemoPath { get; }

        public string SidecarPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
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
