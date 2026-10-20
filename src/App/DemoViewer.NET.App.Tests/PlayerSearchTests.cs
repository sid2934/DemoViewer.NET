#region

using System.Globalization;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Players;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>Player search over an in-memory directory: any SteamID spelling opens the account, a name lists every account that wore it.</summary>
public class PlayerSearchTests
{
    private const ulong Grim = 76561197960265729;
    private const ulong Other = 76561190000000012;
    private const ulong Third = 76561190000000020;

    private static CachedPlayerInfo Player(int slot, string name, ulong steamId, int team = 3) =>
        new() { Slot = slot, Name = name, SteamId64 = steamId.ToString(CultureInfo.InvariantCulture), Team = team };

    private static void Add(DemoCacheStore store, string path, params CachedPlayerInfo[] players)
    {
        DemoCacheRecord record = new() { Path = path, Size = 100, ModifiedTicks = 1, Map = "de_nuke", Players = [.. players] };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        store.Upsert(record);
    }

    private static PlayerSearch Library(IVanityResolver? resolver = null)
    {
        DemoCacheStore store = new(null);
        Add(store, "/d/a.dem", Player(3, "Officer", Grim), Player(4, "Rex", Other), Player(5, "rex", Third));
        Add(store, "/d/b.dem", Player(3, "Officer", Grim), Player(4, "Rex", Other));
        return new PlayerSearch(new PlayerDirectory(store), resolver);
    }

    [Test]
    [Arguments("76561197960265729")]
    [Arguments("https://steamcommunity.com/profiles/76561197960265729")]
    [Arguments("[U:1:1]")]
    [Arguments("STEAM_1:1:0")]
    public async Task EveryForm_OpensTheSameAccount(string text)
    {
        PlayerSearchResult result = Library().Search(text);
        using (Assert.Multiple())
        {
            await Assert.That(result.SteamId64).IsEqualTo(Grim);
            await Assert.That(result.Candidates.Select(c => c.SteamId64).ToArray()).IsEquivalentTo([Grim]);
            await Assert.That(result.IsFlagged).IsFalse();
        }
    }

    [Test]
    public async Task AnAccountNoDemoHolds_StillNamesItself()
    {
        PlayerSearchResult result = Library().Search("[U:1:99]");
        using (Assert.Multiple())
        {
            await Assert.That(result.SteamId64).IsEqualTo(76561197960265728UL + 99);
            await Assert.That(result.Candidates.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ABareAccountId_IsFlagged()
    {
        PlayerSearchResult result = Library().Search("1");
        using (Assert.Multiple())
        {
            await Assert.That(result.IsFlagged).IsTrue();
            await Assert.That(result.SteamId64).IsEqualTo(Grim);
            await Assert.That(result.Candidates.Single().SteamId64).IsEqualTo(Grim);
        }
    }

    [Test]
    public async Task ANameWithManyAccounts_ListsEach_MostDemosFirst()
    {
        PlayerSearchResult result = Library().Search("REX");
        using (Assert.Multiple())
        {
            await Assert.That(result.SteamId64).IsNull();
            await Assert.That(result.Candidates.Select(c => c.SteamId64).ToArray()).IsEquivalentTo([Other, Third]);
            await Assert.That(result.Candidates[0].SteamId64).IsEqualTo(Other).Because("it is in two demos, the other in one");
        }
    }

    [Test]
    public async Task AVanityUrl_IsLabelled_AndMatchesByName()
    {
        PlayerSearchResult result = Library().Search("https://steamcommunity.com/id/officer/");
        using (Assert.Multiple())
        {
            await Assert.That(result.SteamId64).IsNull();
            await Assert.That(result.Vanity).IsEqualTo("officer");
            await Assert.That(result.Notice).IsEqualTo(PlayerSearch.VanityNotice);
            await Assert.That(result.Candidates.Single().SteamId64).IsEqualTo(Grim);
        }
    }

    [Test]
    public async Task AResolver_TurnsAVanityUrlIntoItsAccount()
    {
        PlayerSearchResult result = await Library(new FixedResolver(Other)).SearchAsync("steamcommunity.com/id/anything");
        using (Assert.Multiple())
        {
            await Assert.That(result.SteamId64).IsEqualTo(Other);
            await Assert.That(result.Notice).IsNull();
            await Assert.That(result.Candidates.Single().SteamId64).IsEqualTo(Other);
        }
    }

    [Test]
    public async Task NoResolver_LeavesTheVanityResultAlone()
    {
        PlayerSearchResult result = await Library().SearchAsync("steamcommunity.com/id/anything");
        await Assert.That(result.Notice).IsEqualTo(PlayerSearch.VanityNotice);
    }

    [Test]
    public async Task BlankText_FindsNothing() => await Assert.That(Library().Search("  ").Candidates.Count).IsEqualTo(0);

    private sealed class FixedResolver(ulong id) : IVanityResolver
    {
        public Task<ulong?> ResolveAsync(string vanity, CancellationToken cancellationToken) => Task.FromResult<ulong?>(id);
    }
}
