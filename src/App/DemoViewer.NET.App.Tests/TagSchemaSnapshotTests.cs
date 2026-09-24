#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>Shared tag fixtures: a temp root per test, and the document the schema golden is written from.</summary>
internal static class TagTestData
{
    public const string Sha = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    public static readonly DemoIdentity Demo = new(Sha, "match730_sample.dem", 549_715_968);

    public static readonly ClockIdentity Clock = new(ClockIdentity.DvFrameClock, 64, 223_114, 0, 0);

    public static readonly DateTime Created = new(2026, 9, 23, 14, 2, 11, DateTimeKind.Utc);

    public static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-tags-{Guid.NewGuid():N}");

    public static void DeleteQuietly(string root)
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp dir left behind is harmless.
        }
    }

    public static TagInstance Instance(string code, int from, int to, params (string Group, string Value)[] labels) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        FromTick = from,
        ToTick = to,
        CreatedUtc = Created,
        ModifiedUtc = Created,
        Labels = [.. labels.Select(l => new TagLabel(l.Group, l.Value))]
    };

    public static TagDocument Document(string sha = Sha, params TagInstance[] instances)
    {
        TagDocument document = TagDocument.Create(Demo with { Sha256 = sha }, Clock);
        document.Instances.AddRange(instances);
        return document;
    }

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    ///     Every field of schema v1 at least once, both label namespaces, a repeated group and a bare label,
    ///     an accepted proposal's free-form provenance, and an unknown field at root, instance, label and
    ///     position level so the golden pins that they survive.
    /// </summary>
    public static TagDocument SchemaSample()
    {
        TagDocument document = TagDocument.Create(Demo, Clock);
        document.Palette = "cs2-default";
        document.Extra = new Dictionary<string, JsonElement> { ["futureRoot"] = Element("""{ "kept": true }""") };

        TagPosition site = new()
        {
            X = -1180.5,
            Y = 2044.25,
            LevelMinZ = -384,
            Tick = 41600,
            Place = "BombsiteA",
            PlaceSource = "zones:1c2b3a49",
            Extra = new Dictionary<string, JsonElement> { ["futurePosition"] = Element("3") }
        };

        document.Instances.Add(new TagInstance
        {
            Id = Guid.Parse("6f1c0d2e-3a4b-4c5d-8e9f-a0b1c2d3e4f5"),
            Code = "A execute",
            FromTick = 41216,
            ToTick = 42432,
            Round = 7,
            CreatedUtc = Created,
            ModifiedUtc = Created.AddMinutes(3),
            Source = TagSources.Human,
            Labels =
            [
                new TagLabel("outcome", "won"),
                new TagLabel("site", "A"),
                new TagLabel("site", "B"),
                new TagLabel("", "sloppy") { Extra = new Dictionary<string, JsonElement> { ["futureLabel"] = Element("\"kept\"") } }
            ],
            Facts =
            [
                new TagLabel("side", "T"),
                new TagLabel("buy.us", "full"),
                new TagLabel("buy.them", "force"),
                new TagLabel("score", "6-4"),
                new TagLabel("endReason", "bomb"),
                new TagLabel("plantSite", "A")
            ],
            FactsStamp = new TagFactsStamp { Schema = 1, ComputedUtc = Created, Stale = false },
            Note = "smokes were 1s late; the B player rotated before the flash",
            Positions = [site],
            Movements =
            [
                new TagMovement
                {
                    From = new TagPosition { X = -220, Y = 1610, LevelMinZ = -384, Tick = 41300, Place = "Ramp", PlaceSource = "pawn" },
                    To = new TagPosition { X = -1180.5, Y = 2044.25, LevelMinZ = -384, Tick = 41600 }
                }
            ],
            Extra = new Dictionary<string, JsonElement> { ["futureInstance"] = Element("true") }
        });

        document.Instances.Add(new TagInstance
        {
            Id = Guid.Parse("0a1b2c3d-4e5f-4a6b-9c7d-8e9fa0b1c2d3"),
            Code = "B execute",
            FromTick = 60000,
            ToTick = 61200,
            CreatedUtc = Created.AddHours(1),
            ModifiedUtc = Created.AddHours(1),
            Source = TagSources.Suggested,
            Provenance = JsonNode.Parse("""
                { "source": "suggested-tags", "detector": "execute", "proposalId": "exec|r12|T|BombsiteB|s=21",
                  "confidence": 0.78, "detectorSetFingerprint": "abc123", "edited": false }
                """)!.AsObject(),
            Labels = [new TagLabel(TagStore.StratGroup, "b-split")]
        });

        return document;
    }
}

/// <summary>
///     Schema v1 of the <c>.dvtag.json</c> sidecar, pinned by a committed sample
///     (tests/fixtures/tags/schema-v1.sample.json). A third party reads this file, so a renamed field is a
///     compatibility break the store's own round trips would happily agree with themselves about.
///     Regenerate deliberately with <c>PB2D_GOLDEN_UPDATE=1</c>, the annotation golden's switch, and read
///     the diff before committing it.
/// </summary>
[NotInParallel]
public class TagSchemaSnapshotTests
{
    private const string SampleName = "schema-v1.sample.json";

    [Test]
    public async Task V1Schema_MatchesCheckedInSample()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string path = Path.Combine(repo, "tests", "fixtures", "tags", SampleName);
        if (Environment.GetEnvironmentVariable("PB2D_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(TagTestData.SchemaSample(), TagJsonContext.Default.TagDocument) + "\n");
        }

        if (!File.Exists(path))
        {
            throw new SkipTestException($"missing {path}; regenerate with PB2D_GOLDEN_UPDATE=1");
        }

        string original = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        string root = TagTestData.TempRoot();
        try
        {
            // Load and save through the real store, not the serializer alone: the bytes a user's file
            // goes through are the store's.
            string sidecar = TagStore.SidecarPathFor(root, TagTestData.Sha);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            File.WriteAllText(sidecar, original);

            TagStore store = new(root);
            TagLoadResult loaded = store.Load(TagTestData.Sha, TagTestData.Clock);
            TagDocument document = loaded.Document!;

            using (Assert.Multiple())
            {
                await Assert.That(loaded.DemoMismatch).IsFalse();
                await Assert.That(loaded.ClockMismatch).IsFalse();
                await Assert.That(loaded.SchemaVersion).IsEqualTo(TagStore.SchemaVersion);
                await Assert.That(document.Instances.Count).IsEqualTo(2);
                await Assert.That(document.Instances[0].Labels.Count(l => l.Group == "site")).IsEqualTo(2)
                    .Because("groups repeat: labels are a list, not a dictionary");
                await Assert.That(document.Instances[0].Facts.Count).IsEqualTo(6);
                await Assert.That(document.Instances[1].Provenance!["detector"]!.GetValue<string>()).IsEqualTo("execute");
                await Assert.That(document.Extra!.ContainsKey("futureRoot")).IsTrue();
            }

            await Assert.That(store.Save(document)).IsTrue();
            string round = File.ReadAllText(sidecar);
            await Assert.That(round + "\n").IsEqualTo(original)
                .Because("the v1 sidecar is a published format; a round trip must be field-identical, unknown fields included");
            await Assert.That(JsonSerializer.Serialize(TagTestData.SchemaSample(), TagJsonContext.Default.TagDocument) + "\n")
                .IsEqualTo(original).Because("this build still writes the committed shape");
        }
        finally
        {
            TagTestData.DeleteQuietly(root);
        }
    }

    [Test]
    public async Task NoSuggestionBlock_AndNullFieldsAreAbsent()
    {
        TagInstance bare = TagTestData.Instance("Default", 100, 200);
        string json = JsonSerializer.Serialize(TagTestData.Document(TagTestData.Sha, bare), TagJsonContext.Default.TagDocument);

        using (Assert.Multiple())
        {
            // Overview correction 2 withdrew the per-instance suggestion block; provenance replaces it.
            await Assert.That(json).DoesNotContain("\"suggestion\"");
            await Assert.That(json).DoesNotContain("\"provenance\"");
            await Assert.That(json).DoesNotContain("\"note\"");
            await Assert.That(json).DoesNotContain("\"round\"");
            await Assert.That(json).Contains("\"positions\": []");
            await Assert.That(json).Contains("\"movements\": []");
        }
    }
}
