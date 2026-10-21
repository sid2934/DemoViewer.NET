#region

using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>MatchMeta</c> record field is a published format (<c>docs/match-meta-format.md</c>): this build
///     still writes the committed sample, built from the committed <c>.dem.info</c>.
/// </summary>
public class MatchMetaTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Lf(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    [Test]
    public async Task V1Schema_MatchesTheCheckedInSamples()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        DemoMatchMeta meta = MatchMetaPass.Resolve(null, MatchMetaPass.ParseName(MatchInfoReaderTests.CommittedName),
            MatchInfoReader.Read(MatchInfoReaderTests.CommittedInfo()), new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc));
        string json = Lf(JsonSerializer.Serialize(meta, Indented));
        string path = Path.Combine(repo, "tests", "fixtures", "match-meta", "match-meta.sample.json");
        if (Environment.GetEnvironmentVariable("MATCH_META_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        if (!File.Exists(path))
        {
            throw new SkipTestException("missing the match-meta sample; regenerate with MATCH_META_GOLDEN_UPDATE=1");
        }

        string committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        DemoMatchMeta? back = JsonSerializer.Deserialize<DemoMatchMeta>(committed);
        using (Assert.Multiple())
        {
            await Assert.That(json).IsEqualTo(committed).Because("MatchMeta is a published format; this build still writes the committed shape");
            await Assert.That(Lf(JsonSerializer.Serialize(back, Indented))).IsEqualTo(json).Because("round trip");
            await Assert.That(new DateTime(back!.MatchTimeUtcTicks!.Value, DateTimeKind.Utc).ToString("O"))
                .IsEqualTo("2026-06-17T23:17:26.0000000Z");
            await Assert.That(back.MatchTimeSource).IsEqualTo(MatchTimeSource.ValveMatchInfo);
            await Assert.That(back.ReservationId).IsEqualTo("3826256877184877003");
            await Assert.That(committed).Contains("\"MatchTimeSource\": \"ValveMatchInfo\"").Because("the source is written by name");
        }
    }

    [Test]
    public async Task TheIndexRowMirrorsTheRecord_AndAPlainRecordWritesNoMatchMeta()
    {
        DemoCacheRecord record = new() { Path = "/d/" + MatchInfoReaderTests.CommittedName, Size = 1, ModifiedTicks = 1 };
        string plain = JsonSerializer.Serialize(record);
        string plainRow = JsonSerializer.Serialize(record.ToIndexEntry());
        record.MatchMeta = new DemoMatchMeta
        {
            MatchTimeUtcTicks = 639173350460000000,
            MatchTimeSource = MatchTimeSource.ValveMatchInfo,
            ReservationId = "3826256877184877003"
        };
        DemoCacheIndexEntry row = record.ToIndexEntry();
        DemoCacheRecord copy = new() { Path = record.Path };
        copy.FillMissingFrom(record);
        using (Assert.Multiple())
        {
            await Assert.That(plain).DoesNotContain("MatchMeta").Because("the field is additive: a record without it writes the old bytes");
            await Assert.That(plainRow).DoesNotContain("MatchTime");
            await Assert.That(plainRow).DoesNotContain("ReservationId");
            await Assert.That(row.MatchTimeUtcTicks).IsEqualTo(639173350460000000);
            await Assert.That(row.MatchTimeSource).IsEqualTo(MatchTimeSource.ValveMatchInfo);
            await Assert.That(row.ReservationId).IsEqualTo("3826256877184877003");
            await Assert.That(copy.MatchMeta).IsSameReferenceAs(record.MatchMeta).Because("a second path of the same bytes takes the capture");
        }
    }
}
