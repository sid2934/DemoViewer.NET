using DemoViewer.NET.Services.Players;

namespace DemoViewer.NET.AppTests;

/// <summary>Every spelling of an account a person pastes reads back as the same SteamID64.</summary>
public class SteamIdParserTests
{
    [Test]
    [Arguments("76561197960265729", SteamIdForm.SteamId64, 76561197960265729UL)]
    [Arguments(" 76561197960265729 ", SteamIdForm.SteamId64, 76561197960265729UL)]
    [Arguments("https://steamcommunity.com/profiles/76561197960265729", SteamIdForm.ProfileUrl, 76561197960265729UL)]
    [Arguments("steamcommunity.com/profiles/76561197960265729/", SteamIdForm.ProfileUrl, 76561197960265729UL)]
    [Arguments("http://www.steamcommunity.com/profiles/76561197960265729?x=1", SteamIdForm.ProfileUrl, 76561197960265729UL)]
    [Arguments("http://www.steamcommunity.com/profiles/[U:1:1]?l=english", SteamIdForm.ProfileUrl, 76561197960265729UL)]
    [Arguments("[U:1:1]", SteamIdForm.SteamId3, 76561197960265729UL)]
    [Arguments("U:1:1", SteamIdForm.SteamId3, 76561197960265729UL)]
    [Arguments("[u:1:1]", SteamIdForm.SteamId3, 76561197960265729UL)]
    [Arguments("STEAM_1:1:0", SteamIdForm.SteamId2, 76561197960265729UL)]
    [Arguments("STEAM_0:1:0", SteamIdForm.SteamId2, 76561197960265729UL)]
    [Arguments("steam_1:0:1", SteamIdForm.SteamId2, 76561197960265730UL)]
    [Arguments("1", SteamIdForm.AccountId, 76561197960265729UL)]
    [Arguments("4294967295", SteamIdForm.AccountId, 76561197960265728UL + uint.MaxValue)]
    public async Task EveryForm_ReadsAsTheSameAccount(string text, SteamIdForm form, ulong expected)
    {
        SteamIdParse? parse = SteamIdParser.Parse(text);
        using (Assert.Multiple())
        {
            await Assert.That(parse).IsNotNull();
            await Assert.That(parse!.Form).IsEqualTo(form);
            await Assert.That(parse.SteamId64).IsEqualTo(expected);
            await Assert.That(parse.IsFlagged).IsEqualTo(form == SteamIdForm.AccountId);
        }
    }

    [Test]
    public async Task AVanityUrl_NamesNoAccount()
    {
        SteamIdParse? parse = SteamIdParser.Parse("https://steamcommunity.com/id/some_name/");
        using (Assert.Multiple())
        {
            await Assert.That(parse!.Form).IsEqualTo(SteamIdForm.VanityUrl);
            await Assert.That(parse.SteamId64).IsNull();
            await Assert.That(parse.Vanity).IsEqualTo("some_name");
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("grim")]
    [Arguments("0")]
    [Arguments("[U:1:0]")]
    [Arguments("[U:2:5]")]
    [Arguments("STEAM_1:2:0")]
    [Arguments("STEAM_1:1:")]
    [Arguments("https://steamcommunity.com/profiles/abc")]
    [Arguments("https://example.com/profiles/76561197960265729")]
    public async Task NotAnAccount_ReadsAsNothing(string text) => await Assert.That(SteamIdParser.Parse(text)).IsNull();
}
