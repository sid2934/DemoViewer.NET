#region

using System.Text.Json;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

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
