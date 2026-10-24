#region

using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.TestSupport;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>.dem.info</c> wire reader and the Valve file name, against the one committed info file. The file
///     is only read, never copied: every other case is built from bytes in memory.
/// </summary>
public class MatchInfoReaderTests
{
    internal const string CommittedName = "match730_003826256877184877003_0981591541_410.dem";

    internal static byte[] CommittedInfo()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string path = Path.Combine(repo, "demos", CommittedName + ".info");
        if (!File.Exists(path))
        {
            throw new SkipTestException($"the committed .dem.info is missing at {path}");
        }

        return File.ReadAllBytes(path);
    }

    [Test]
    public async Task DecodesTheCommittedInfoFile()
    {
        MatchInfo info = MatchInfoReader.Read(CommittedInfo());
        using (Assert.Multiple())
        {
            await Assert.That(info.MatchTimeUtc).IsEqualTo(new DateTime(2026, 6, 17, 23, 17, 26, DateTimeKind.Utc));
            await Assert.That(info.ReservationId).IsEqualTo(3826256877184877003UL)
                .Because("the reservation id rides only the last round's stats");
            await Assert.That(info.TeamScores).IsEquivalentTo([2, 13], CollectionOrdering.Matching);
            await Assert.That(info.MatchResult).IsEqualTo(2);
            await Assert.That(MatchMetaPass.ParseName(CommittedName)?.Reservation).IsEqualTo(info.ReservationId)
                .Because("the file name's first number is the same reservation");
        }
    }

    [Test]
    public async Task ParsesTheValveFileName()
    {
        using (Assert.Multiple())
        {
            await Assert.That(MatchMetaPass.ParseName(@"C:\replays\" + CommittedName))
                .IsEqualTo((3826256877184877003UL, 981591541L, 410L));
            await Assert.That(MatchMetaPass.ParseName("/demos/furia-vs-vitality-m1-mirage.dem")).IsNull();
            await Assert.That(MatchMetaPass.ParseName("/match730_1_2_3/other.dem")).IsNull()
                .Because("only the file name is matched, never a folder");
        }
    }

    [Test]
    public async Task ReadsPackedScoresAndSkipsUnknownWireTypes()
    {
        // matchtime = 1, an unknown fixed64 and fixed32, then one round with a packed team_scores [16, 9].
        byte[] bytes = [0x10, 0x01, 0x49, 1, 2, 3, 4, 5, 6, 7, 8, 0x55, 1, 2, 3, 4, 0x2A, 0x06, 0x08, 0x07, 0x62, 0x02, 16, 9];
        MatchInfo info = MatchInfoReader.Read(bytes);
        using (Assert.Multiple())
        {
            await Assert.That(info.MatchTimeUtc).IsEqualTo(DateTime.UnixEpoch.AddSeconds(1));
            await Assert.That(info.ReservationId).IsEqualTo(7UL);
            await Assert.That(info.TeamScores).IsEquivalentTo([16, 9], CollectionOrdering.Matching);
            await Assert.That(info.MatchResult).IsNull();
        }
    }

    [Test]
    public async Task TruncatedBytes_AreInvalidData()
    {
        byte[] info = CommittedInfo();
        Assert.Throws<InvalidDataException>(() => MatchInfoReader.Read(info.AsSpan(0, info.Length - 3)));
        Assert.Throws<InvalidDataException>(() => MatchInfoReader.Read([0x10, 0x80]));
        await Assert.That(MatchInfoReader.Read([]).MatchTimeUtc).IsNull().Because("an empty message is a message with no fields");
    }
}
