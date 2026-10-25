#region

using DemoViewer.NET.Models;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="ProtoIndex" /> against the real descriptors compiled into CS2OpenDev.Protos.
///     Nothing here is mocked: the index is the one the shell builds at startup, so a package whose
///     message moved to another .proto turns a silently dead parse-chain link into a red test.
///     Links are file-level because descriptors carry no line numbers.
/// </summary>
public class ProtoIndexTests
{
    private static readonly ProtoIndex _idx = ProtoIndex.Build();

    /// <summary>The frame row's link: <c>frame.Command</c> is looked up as an EDemoCommands value.</summary>
    [Test]
    [Arguments("DEM_Packet")]
    [Arguments("DEM_SignonPacket")]
    [Arguments("DEM_FullPacket")]
    public async Task FrameCommand_ResolvesAsAnEDemoCommandsValue(string frameCommand)
    {
        await Assert.That(_idx.TryGetField("EDemoCommands", frameCommand, out string? file)).IsTrue();
        await Assert.That(file).IsEqualTo("demo.proto");
    }

    [Test]
    public async Task FieldUrl_IsFileLevelWithNoLineAnchor()
    {
        string? url = _idx.FieldUrl("CNETMsg_SetConVar", "convars");

        await Assert.That(url)
            .IsEqualTo("https://github.com/SteamDatabase/GameTracking-CS2/blob/master/Protobufs/networkbasetypes.proto");
        await Assert.That(url!.Contains("#L", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    ///     The .proto files CS2OpenDev.Protos generates from. Each name is appended to the GitHub
    ///     blob base verbatim, so a package bump that adds one must be checked against the tracking
    ///     repo by hand: a name that is not a file there links to a 404 and fails silently.
    /// </summary>
    [Test]
    public async Task Index_CoversEveryProtoFileInThePackage()
    {
        string[] expected =
        [
            "clientmessages.proto", "cs_gameevents.proto", "cs_usercmd.proto", "cstrike15_gcmessages.proto",
            "cstrike15_usermessages.proto", "demo.proto", "gameevents.proto", "netmessages.proto",
            "network_connection.proto", "networkbasetypes.proto", "source2_steam_stats.proto", "te.proto",
            "usercmd.proto", "usermessages.proto", "valveextensions.proto"
        ];

        await Assert.That(_idx.ProtoFiles).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("CDemoPacket", "demo.proto")]
    [Arguments("EDemoCommands", "demo.proto")]
    [Arguments("CSVCMsg_PacketEntities", "netmessages.proto")]
    [Arguments("CNETMsg_SetConVar", "networkbasetypes.proto")]
    public async Task KnownType_ResolvesToItsDeclaringProtoFile(string type, string expectedFile)
    {
        await Assert.That(_idx.TryGetType(type, out string? file)).IsTrue();
        await Assert.That(file).IsEqualTo(expectedFile);
    }

    [Test]
    public async Task KnownMessageField_ResolvesToItsDeclaringProtoFile()
    {
        await Assert.That(_idx.TryGetField("CNETMsg_SetConVar", "convars", out string? file)).IsTrue();
        await Assert.That(file).IsEqualTo("networkbasetypes.proto");

        await Assert.That(_idx.TryGetField("CDemoPacket", "data", out string? dataFile)).IsTrue();
        await Assert.That(dataFile).IsEqualTo("demo.proto");
    }

    [Test]
    public async Task Search_PutsPrefixMatchesFirst()
    {
        List<ProtoResult> hits = _idx.Search("C").ToList();

        await Assert.That(hits.Count).IsGreaterThan(1);
        await Assert.That(hits[0].Rank).IsEqualTo(0);

        for (int i = 1; i < hits.Count; i++)
        {
            await Assert.That(hits[i].Rank).IsGreaterThanOrEqualTo(hits[i - 1].Rank);
        }
    }

    /// <summary>What the command palette hands to the browser.</summary>
    [Test]
    public async Task Search_YieldsAFileLevelUrlPerHit()
    {
        ProtoResult hit = _idx.Search("CDemoPacket").First();

        await Assert.That(hit.MessageName).IsEqualTo("CDemoPacket");
        await Assert.That(hit.ProtoFile).IsEqualTo("demo.proto");
        await Assert.That(hit.Rank).IsEqualTo(0);
        await Assert.That(hit.Url)
            .IsEqualTo("https://github.com/SteamDatabase/GameTracking-CS2/blob/master/Protobufs/demo.proto");
    }

    [Test]
    public async Task TypeUrl_IsFileLevelWithNoLineAnchor()
    {
        string? url = _idx.TypeUrl("CDemoPacket");

        await Assert.That(url)
            .IsEqualTo("https://github.com/SteamDatabase/GameTracking-CS2/blob/master/Protobufs/demo.proto");
        await Assert.That(url!.Contains("#L", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>A miss yields no link, so the parse-chain row stays unclickable rather than dead.</summary>
    [Test]
    public async Task UnknownNames_YieldNoUrl()
    {
        await Assert.That(_idx.TypeUrl("CNoSuchMessage")).IsNull();
        await Assert.That(_idx.TypeUrl(null)).IsNull();
        await Assert.That(_idx.FieldUrl("CDemoPacket", "no_such_field")).IsNull();
        await Assert.That(_idx.FieldUrl(null, "data")).IsNull();
    }
}
