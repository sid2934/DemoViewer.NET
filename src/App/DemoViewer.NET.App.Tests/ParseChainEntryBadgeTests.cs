#region

using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The badge on a parse-chain row is the only thing telling a reader where the row goes before they
///     click it. Every web link in the chain is a GitHub blob URL, so badging the host labelled all of them
///     "github.com"; this pins the file name instead. The proto rows are why it matters: they lost their
///     local-path badge when the proto index moved to descriptors, since the local path was the submodule.
/// </summary>
public class ParseChainEntryBadgeTests
{
    [Test]
    [Arguments("https://github.com/SteamDatabase/GameTracking-CS2/blob/master/Protobufs/demo.proto", "demo.proto")]
    [Arguments("https://github.com/CS2OpenDev/CS2DemoKit/blob/main/src/Parser/DemoParser.cs#L20", "DemoParser.cs:20")]
    [Arguments("https://github.com/o/r/blob/main/a/b/File.cs#L1", "File.cs:1")]
    public async Task AWebLink_IsBadgedByItsFileName_WithTheLineFromTheAnchor(string url, string expected)
    {
        ParseChainEntry entry = ParseChainEntry.Linked("row", webUrl: url);

        using (Assert.Multiple())
        {
            await Assert.That(entry.SourceBadge).IsEqualTo(expected);
            await Assert.That(entry.OpenCommand).IsNotNull().Because("a row with a link must be clickable");
        }
    }

    /// <summary>A fragment that is not a line anchor must not leak into the badge.</summary>
    [Test]
    [Arguments("https://github.com/o/r/blob/main/x.proto#readme", "x.proto")]
    [Arguments("https://github.com/o/r/blob/main/x.proto", "x.proto")]
    public async Task ANonLineFragment_IsIgnored(string url, string expected) =>
        await Assert.That(ParseChainEntry.Linked("row", webUrl: url).SourceBadge).IsEqualTo(expected);

    /// <summary>A local path still wins, and still carries its line. That path is unused today but the shape stands.</summary>
    [Test]
    public async Task ALocalPath_StillBeatsTheUrl()
    {
        ParseChainEntry entry = ParseChainEntry.Linked("row",
            localPath: Path.Combine("x", "netmessages.proto"), localLine: 201,
            webUrl: "https://github.com/o/r/blob/main/netmessages.proto");

        await Assert.That(entry.SourceBadge).IsEqualTo("netmessages.proto:201");
    }

    [Test]
    public async Task ARowWithNoLink_HasNoBadgeAndNoCommand()
    {
        ParseChainEntry entry = ParseChainEntry.Linked("row");

        using (Assert.Multiple())
        {
            await Assert.That(entry.SourceBadge).IsNull();
            await Assert.That(entry.OpenCommand).IsNull();
        }
    }
}
