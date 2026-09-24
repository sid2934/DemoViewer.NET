#region

using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The token grammar: canonical order with explicit counts, <c>?</c> for a null place, the empty
///     side, decode as the inverse of encode, ordinal sort, and the two characters a place may not carry.
/// </summary>
public class PlaceCountTokenTests
{
    [Test]
    public async Task Encode_IsCanonical_WithExplicitCounts()
    {
        string token = PlaceCountToken.Encode([("Outside", 3), ("BombsiteA", 2)]);
        await Assert.That(token).IsEqualTo("BombsiteA:2|Outside:3");

        string ones = PlaceCountToken.EncodePlaces(["Ramp", "Ramp", "Lobby"]);
        await Assert.That(ones).IsEqualTo("Lobby:1|Ramp:2").Because(":1 is written, so one decoder has no special case");
    }

    [Test]
    public async Task ANullPlace_CountsUnderTheQuestionMark_AndAnEmptySideIsEmpty()
    {
        using (Assert.Multiple())
        {
            await Assert.That(PlaceCountToken.EncodePlaces([null, "Ramp", "Ramp", "Ramp", "Ramp"])).IsEqualTo("?:1|Ramp:4");
            await Assert.That(PlaceCountToken.EncodePlaces(["", "Ramp"])).IsEqualTo("?:1|Ramp:1");
            await Assert.That(PlaceCountToken.EncodePlaces([])).IsEqualTo("");
            await Assert.That(PlaceCountToken.Decode("")).IsEmpty();
            await Assert.That(PlaceCountToken.AliveCount("?:1|Ramp:4")).IsEqualTo(5).Because("the man-count survives a null place");
        }
    }

    [Test]
    public async Task Decode_IsTheInverseOfEncode()
    {
        (string, int)[] pairs = [("Z", 1), ("a", 2), ("BombsiteB", 5)];
        string token = PlaceCountToken.Encode(pairs.Select(p => ((string?)p.Item1, p.Item2)));
        IReadOnlyList<(string Place, int Count)> decoded = PlaceCountToken.Decode(token);

        using (Assert.Multiple())
        {
            await Assert.That(token).IsEqualTo("BombsiteB:5|Z:1|a:2").Because("ordinal sort places Z before a");
            await Assert.That(decoded.Count).IsEqualTo(3);
            await Assert.That(decoded[0]).IsEqualTo(("BombsiteB", 5));
            await Assert.That(decoded[1]).IsEqualTo(("Z", 1));
            await Assert.That(decoded[2]).IsEqualTo(("a", 2));
            await Assert.That(PlaceCountToken.Encode(decoded.Select(p => ((string?)p.Place, p.Count)))).IsEqualTo(token);
        }
    }

    [Test]
    public async Task RepeatedPlaces_Sum_AndNonPositiveCountsDrop()
    {
        string token = PlaceCountToken.Encode([("Ramp", 2), ("Ramp", 1), ("Hell", 0), ("Silo", -1)]);
        await Assert.That(token).IsEqualTo("Ramp:3");
    }

    [Test]
    public async Task APlaceWithAReservedCharacter_IsRejectedWithAMessage()
    {
        ArgumentException colon = Assert.Throws<ArgumentException>(() => PlaceCountToken.Encode([("Bomb:site", 1)]));
        await Assert.That(colon.Message).Contains("Bomb:site");
        Assert.Throws<ArgumentException>(() => PlaceCountToken.Encode([("A|B", 1)]));
    }

    [Test]
    public async Task AMalformedToken_FailsToDecode()
    {
        FormatException noCount = Assert.Throws<FormatException>(() => PlaceCountToken.Decode("Ramp"));
        await Assert.That(noCount.Message).Contains("Ramp");
        Assert.Throws<FormatException>(() => PlaceCountToken.Decode("Ramp:0"));
        Assert.Throws<FormatException>(() => PlaceCountToken.Decode("Ramp:x"));
    }
}
