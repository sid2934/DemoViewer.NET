#region

using System.Globalization;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.Services.Players;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     What an extension reads of the player directory: <see cref="PlayerDirectory" /> through
///     <see cref="HostPlayerDirectory" /> in the SDK's types, and the defaults a host without one answers. Every
///     store is in memory.
/// </summary>
public class ExtensionPlayerDirectoryTests
{
    private const ulong Grim = 76561190000000003;
    private const ulong Gone = 76561190000000020;
    private static readonly long Day = TimeSpan.TicksPerDay;

    private static DemoCacheRecord Demo(string path, long? matchTime, params CachedPlayerInfo[] players)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 100,
            ModifiedTicks = 5 * Day,
            Map = "de_nuke",
            Players = [.. players],
            MatchMeta = matchTime is { } time
                ? new DemoMatchMeta { MatchTimeUtcTicks = time, MatchTimeSource = MatchTimeSource.ValveMatchInfo }
                : null
        };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        return record;
    }

    private static CachedPlayerInfo Player(int slot, string name, ulong steamId, int team) =>
        new() { Slot = slot, Name = name, SteamId64 = steamId.ToString(CultureInfo.InvariantCulture), Team = team };

    private static (DemoCacheStore Store, HostLibrary Library, HostPlayerDirectory Players) Make(ExtensionGuard? guard = null)
    {
        DemoCacheStore store = new(null);
        HostLibrary library = new(store, null, () => false);
        PlayerDirectory directory = new(store, _ => true);
        return (store, library, new HostPlayerDirectory(() => directory, library, guard ?? ExtensionPassHostTests.StandaloneGuard()));
    }

    [Test]
    public async Task Accounts_AreTheDirectorysInTheSdksTypes_AndTheSameListUntilTheIndexMoves()
    {
        (DemoCacheStore store, _, HostPlayerDirectory players) = Make();
        store.Upsert(Demo("/d/old.dem", 10 * Day, Player(1, "Officer", Grim, 3)));
        store.Upsert(Demo("/d/new.dem", 20 * Day, Player(1, "grim", Grim, 3)));

        IReadOnlyList<LibraryPlayerAccount> first = players.Accounts;
        LibraryPlayerAccount grim = first.Single();
        using (Assert.Multiple())
        {
            await Assert.That(grim.SteamId64).IsEqualTo(Grim);
            await Assert.That(grim.Name).IsEqualTo("grim").Because("the most recently seen name comes first");
            await Assert.That(grim.DemoCount).IsEqualTo(2);
            await Assert.That(grim.Aliases.Select(a => a.Name)).IsEquivalentTo(["grim", "Officer"], CollectionOrdering.Matching);
            await Assert.That((grim.FirstSeen.Ticks, grim.LastSeen.Ticks)).IsEqualTo((10 * Day, 20 * Day));
            await Assert.That(players.Accounts).IsSameReferenceAs(first);
            await Assert.That(players.Account(Grim)).IsSameReferenceAs(grim);
            await Assert.That(players.Find("OFFIC").Single()).IsSameReferenceAs(grim);
            await Assert.That(players.Find(Grim.ToString(CultureInfo.InvariantCulture)).Single()).IsSameReferenceAs(grim);
            await Assert.That(players.Find("  ")).IsEmpty();
            await Assert.That(players.Account(Gone)).IsNull();
        }

        store.Upsert(Demo("/d/third.dem", 30 * Day, Player(1, "grim", Grim, 2)));
        await Assert.That(players.Accounts).IsNotSameReferenceAs(first);
    }

    [Test]
    public async Task Matches_CarryTheLibrarysRow_TheSide_AndTheMatchTimeOrTheFilesTime()
    {
        (DemoCacheStore store, HostLibrary library, HostPlayerDirectory players) = Make();
        store.Upsert(Demo("/d/timed.dem", 20 * Day, Player(1, "grim", Grim, 3)));
        store.Upsert(Demo("/d/untimed.dem", null, Player(1, "grim", Grim, 2)));
        DemoCacheRecord departed = Demo("/d/left.dem", 30 * Day, Player(1, "grim", Grim, 3), Player(7, "quitter", Gone, 1));
        departed.FacetPlayers = [new FacetPlayer { SteamId64 = Gone.ToString(CultureInfo.InvariantCulture), Names = ["quitter"], Slots = [7] }];
        store.Upsert(departed);

        IReadOnlyList<LibraryPlayerMatch> matches = players.Matches(Grim);
        LibraryPlayerMatch gone = players.Matches(Gone).Single();
        using (Assert.Multiple())
        {
            await Assert.That(matches.Select(m => m.Demo.FilePath)).IsEquivalentTo(["/d/left.dem", "/d/timed.dem", "/d/untimed.dem"], CollectionOrdering.Matching)
                .Because("newest match first, and a demo with no match time sorts by its file's time");
            await Assert.That(matches[1].Demo).IsSameReferenceAs(library.Find("/d/timed.dem"));
            await Assert.That((matches[1].Time.Ticks, matches[1].Time.Kind)).IsEqualTo((20 * Day, DateTimeKind.Utc));
            await Assert.That((matches[2].Time.Ticks, matches[2].Time.Kind)).IsEqualTo((5 * Day, DateTimeKind.Local));
            await Assert.That(matches.Select(m => m.Side)).IsEquivalentTo([(int?)3, 3, 2], CollectionOrdering.Matching);
            await Assert.That(matches[0].Names).IsEquivalentTo(["grim"], CollectionOrdering.Matching);
            await Assert.That(matches[0].Slots).IsEquivalentTo([1], CollectionOrdering.Matching);
            await Assert.That((gone.LeftEarly, gone.Side)).IsEqualTo((true, (int?)null));
            await Assert.That(players.Matches(1)).IsEmpty();
        }
    }

    [Test]
    public async Task Changed_IsJoinedOnlyWhileListened_AndAThrowingHandlerIsTheExtensions()
    {
        FaultRig rig = new();
        (DemoCacheStore store, _, HostPlayerDirectory players) = Make(rig.Guard);
        int raised = 0;
        Action count = () => raised++;
        players.Changed += () => throw new InvalidOperationException("a faulty handler");
        players.Changed += count;

        store.Upsert(Demo("/d/a.dem", Day, Player(1, "grim", Grim, 3)));
        await Assert.That(raised).IsEqualTo(1).Because("a throwing handler before it does not stop the next");

        players.Changed -= count;
        store.Upsert(Demo("/d/b.dem", Day, Player(1, "grim", Grim, 3)));
        await Assert.That(raised).IsEqualTo(1);
    }

    [Test]
    public async Task WithoutADirectory_ThereAreNoAccounts()
    {
        HostPlayerDirectory players = new(() => null, new HostLibrary(new DemoCacheStore(null), null, () => false),
            ExtensionPassHostTests.StandaloneGuard());
        players.Changed += () => throw new InvalidOperationException("never raised");

        using (Assert.Multiple())
        {
            await Assert.That(players.Accounts).IsEmpty();
            await Assert.That(players.Account(Grim)).IsNull();
            await Assert.That(players.Find("grim")).IsEmpty();
            await Assert.That(players.Matches(Grim)).IsEmpty();
        }
    }

    [Test]
    public async Task AnOlderHost_AnswersNoPlayers_AndOpensNoOverview()
    {
        IPlayerDirectory players = PlayersOf(new HostLibrary(new DemoCacheStore(null), null, () => false));

        using (Assert.Multiple())
        {
            await Assert.That(players.Accounts).IsEmpty();
            await Assert.That(players.Matches(Grim)).IsEmpty();
            await Assert.That(OpenOn(new BareShell(), Grim)).IsFalse();
        }
    }

    private static IPlayerDirectory PlayersOf(IExtensionLibrary library) => library.Players;

    private static bool OpenOn(IExtensionShell shell, ulong steamId64) => shell.OpenPlayerOverview(steamId64);

    [Test]
    public async Task TheExtensionsLibrary_ReadsTheAppsDirectory()
    {
        DemoCacheStore store = new(null);
        store.Upsert(Demo("/d/a.dem", Day, Player(1, "grim", Grim, 3)));
        ExtensionLibraryView library = new(new HostLibrary(store, null, () => false),
            ExtensionPassHostTests.StandaloneGuard(), () => new PlayerDirectory(store, _ => true));

        await Assert.That(PlayersOf(library).Accounts.Select(a => a.SteamId64)).IsEquivalentTo([Grim], CollectionOrdering.Matching);
    }

    // A shell built against an SDK before OpenPlayerOverview: it takes the interface's default.
    private sealed class BareShell : IExtensionShell
    {
        public string? CurrentDemoPath => null;

        public event Action? CurrentDemoChanged
        {
            add { }
            remove { }
        }

        public CS2DemoKit.Parser.ParsedDemo? CurrentDemo => null;

        public string? CurrentDemoHash => null;

        public int CurrentTick => 0;

        public bool IsPlaying => false;

        public event Action? PlayheadChanged
        {
            add { }
            remove { }
        }

        public Task<bool> OpenDemoAsync(string path) => Task.FromResult(false);

        public void SeekToTick(int tick)
        {
        }

        public bool SelectTab(string tabId) => false;

        public void RevealInFileManager(string path)
        {
        }
    }
}
