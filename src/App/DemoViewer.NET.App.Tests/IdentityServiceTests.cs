#region

using System.Globalization;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.Services.Players;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The core identity: the active account is always a member, the one-time <c>teams.json</c> import, the "this is
///     you" suggestion and its dismissal, <see cref="IdentityService.Changed" />, and the forms the Settings input
///     reads. Every settings file is in a throwaway folder and every demo store in memory.
/// </summary>
public class IdentityServiceTests
{
    private const string A = "76561198000000001";
    private const string B = "76561198000000002";
    private const string C = "76561198000000003";
    private static readonly DateTime Added = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvidentity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static IdentityAccount Account(string id, string? label = null) => new(id, label, Added);

    private static string[] Ids(IdentityService identity) => [.. identity.Accounts.Select(a => a.SteamId64)];

    // A library where `mine` plays `mineDemos` of `total` demos, everyone else once each.
    internal static PlayerDirectory Library(int total, int mineDemos, ulong mine = 76561198000000009)
    {
        DemoCacheStore store = new(null);
        for (int i = 0; i < total; i++)
        {
            ulong other = 76561198100000000UL + (ulong) i;
            List<CachedPlayerInfo> players =
            [
                new() { Slot = 1, Name = "other" + i, SteamId64 = other.ToString(CultureInfo.InvariantCulture), Team = 2 }
            ];
            if (i < mineDemos)
            {
                players.Add(new CachedPlayerInfo { Slot = 2, Name = "me", SteamId64 = mine.ToString(CultureInfo.InvariantCulture), Team = 3 });
            }

            DemoCacheRecord record = new()
            {
                Path = $"/d/{i}.dem",
                Size = 100,
                ModifiedTicks = 1,
                Map = "de_nuke",
                Players = players,
                MatchMeta = new DemoMatchMeta { MatchTimeUtcTicks = (i + 1) * TimeSpan.TicksPerDay, MatchTimeSource = MatchTimeSource.ValveMatchInfo }
            };
            DemoCacheStore.StampHeader(record);
            DemoCacheStore.StampParse(record);
            store.Upsert(record);
        }

        return new PlayerDirectory(store);
    }

    [Test]
    public async Task TheActiveAccount_IsAlwaysAMember()
    {
        string dir = NewTempDir();
        try
        {
            using IdentityService identity = new(new SettingsService(dir));
            await Assert.That(identity.ActiveSteamId64).IsNull();

            identity.SetAccounts([Account(A), Account(B), Account(A), Account("0"), Account("not an id"), Account(" 76561198000000003")]);
            await Assert.That(Ids(identity)).IsEquivalentTo([A, B], CollectionOrdering.Matching).Because("repeats and non-ids are dropped");
            await Assert.That(identity.ActiveSteamId64).IsEqualTo(A).Because("the first is active when none was");

            await Assert.That(identity.SetActive(B)).IsTrue();
            await Assert.That(identity.SetActive(C)).IsFalse().Because("only a member can be active");
            await Assert.That(identity.ActiveSteamId64).IsEqualTo(B);

            identity.SetAccounts([Account(A), Account(B), Account(C)]);
            await Assert.That(identity.ActiveSteamId64).IsEqualTo(B).Because("a member stays active");

            identity.SetAccounts([Account(C), Account(A)]);
            await Assert.That(identity.ActiveSteamId64).IsEqualTo(C).Because("removing the active one makes the first active");

            identity.SetAccounts([]);
            using (Assert.Multiple())
            {
                await Assert.That(identity.Accounts).IsEmpty();
                await Assert.That(identity.ActiveSteamId64).IsNull();
                await Assert.That(identity.IsMine(C)).IsFalse();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task AHandEditThatBreaksTheRule_IsReadThroughIt_AndWrittenBackCorrected()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), $$"""
                {
                  "Identity": {
                    "Accounts": [ { "SteamId64": "{{A}}" }, { "SteamId64": "{{A}}" }, { "SteamId64": "junk" }, { "SteamId64": "{{B}}", "Label": "" } ],
                    "ActiveSteamId64": "{{C}}"
                  }
                }
                """);
            SettingsService settings = new(dir);
            using IdentityService identity = new(settings);
            using (Assert.Multiple())
            {
                await Assert.That(Ids(identity)).IsEquivalentTo([A, B], CollectionOrdering.Matching);
                await Assert.That(identity.ActiveSteamId64).IsEqualTo(A);
                await Assert.That(identity.Accounts[1].Label).IsNull();
                await Assert.That(identity.IsMine(ulong.Parse(B, CultureInfo.InvariantCulture))).IsTrue();
            }

            identity.SetActive(B);
            IdentitySettings written = new SettingsService(dir).Current.Identity;
            using (Assert.Multiple())
            {
                await Assert.That(written.Accounts.Select(a => a.SteamId64)).IsEquivalentTo([A, B], CollectionOrdering.Matching);
                await Assert.That(written.ActiveSteamId64).IsEqualTo(B);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task TheTeamsJsonImport_TakesMe_OnceAndLeavesTheFileAlone()
    {
        string dir = NewTempDir();
        try
        {
            string teams = Path.Combine(dir, "teams.json");
            string json = $$"""
                {
                  "schemaVersion": 1,
                  // a comment, as a hand edit might leave
                  "Me": { "SteamIDs": [ "{{B}}", 42, "junk", "{{A}}", "{{B}}" ] },
                  "teams": [ { "id": "not a guid this build could bind" } ],
                }
                """;
            File.WriteAllText(teams, json);
            SettingsService settings = new(dir);

            await Assert.That(IdentityService.ImportTeamsJson(settings, teams)).IsEqualTo(2);
            using IdentityService identity = new(settings);
            using (Assert.Multiple())
            {
                await Assert.That(Ids(identity)).IsEquivalentTo([B, A], CollectionOrdering.Matching);
                await Assert.That(identity.ActiveSteamId64).IsEqualTo(B).Because("the first id is active");
                await Assert.That(settings.Current.Identity.MigratedFromTeamsJson).IsTrue();
                await Assert.That(File.ReadAllText(teams)).IsEqualTo(json).Because("the file is only read");
            }

            File.WriteAllText(teams, $$"""{ "me": { "steamIds": [ "{{C}}" ] } }""");
            await Assert.That(IdentityService.ImportTeamsJson(settings, teams)).IsEqualTo(0).Because("the import runs once");
            await Assert.That(Ids(new IdentityService(settings))).IsEquivalentTo([B, A], CollectionOrdering.Matching);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task AnEmptyListAfterMigration_StaysEmpty()
    {
        string dir = NewTempDir();
        try
        {
            string teams = Path.Combine(dir, "teams.json");
            File.WriteAllText(teams, $$"""{ "me": { "steamIds": [ "{{A}}" ] } }""");
            SettingsService settings = new(dir);
            IdentityService.ImportTeamsJson(settings, teams);
            using (IdentityService identity = new(settings))
            {
                identity.SetAccounts([]);
            }

            SettingsService restarted = new(dir);
            await Assert.That(IdentityService.ImportTeamsJson(restarted, teams)).IsEqualTo(0);
            using IdentityService again = new(restarted, Library(30, 25));
            using (Assert.Multiple())
            {
                await Assert.That(again.Accounts).IsEmpty();
                await Assert.That(again.ActiveSteamId64).IsNull();
                await Assert.That(again.Suggest()).IsNotNull().Because("a suggestion is offered, never applied");
                await Assert.That(again.Accounts).IsEmpty();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task TheImport_WithNoTeamsFile_WritesNothing_AndACorruptOneImportsNothingOnce()
    {
        string dir = NewTempDir();
        try
        {
            string teams = Path.Combine(dir, "teams.json");
            SettingsService settings = new(dir);
            using (Assert.Multiple())
            {
                await Assert.That(IdentityService.ImportTeamsJson(settings, teams)).IsEqualTo(0);
                await Assert.That(IdentityService.ImportTeamsJson(settings, null)).IsEqualTo(0).Because("the browser has no file");
                await Assert.That(File.Exists(Path.Combine(dir, "settings.json"))).IsFalse().Because("there was nothing to read yet");
            }

            File.WriteAllText(teams, "{ \"me\": ");
            await Assert.That(IdentityService.ImportTeamsJson(settings, teams)).IsEqualTo(0);
            using (Assert.Multiple())
            {
                await Assert.That(settings.Current.Identity.MigratedFromTeamsJson).IsTrue();
                await Assert.That(settings.Current.Identity.Accounts).IsEmpty();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task TheImport_KeepsAccountsTheUserAlreadyHas()
    {
        string dir = NewTempDir();
        try
        {
            string teams = Path.Combine(dir, "teams.json");
            File.WriteAllText(teams, $$"""{ "me": { "steamIds": [ "{{B}}" ] } }""");
            SettingsService settings = new(dir);
            settings.Write(s => s.Identity.Accounts = [new IdentityAccountSettings { SteamId64 = A, AddedUtc = Added }]);

            await Assert.That(IdentityService.ImportTeamsJson(settings, teams)).IsEqualTo(0);
            using IdentityService identity = new(settings);
            using (Assert.Multiple())
            {
                await Assert.That(Ids(identity)).IsEquivalentTo([A], CollectionOrdering.Matching);
                await Assert.That(settings.Current.Identity.MigratedFromTeamsJson).IsTrue();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task TheSuggestion_NeedsTwentyDemosAndMoreThanHalf()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService settings = new(dir);
            using (Assert.Multiple())
            {
                await Assert.That(new IdentityService(settings, Library(19, 19)).Suggest()).IsNull().Because("under twenty demos");
                await Assert.That(new IdentityService(settings, Library(20, 10)).Suggest()).IsNull().Because("exactly half is not more than half");
                await Assert.That(new IdentityService(settings).Suggest()).IsNull().Because("no directory, no suggestion");
            }

            IdentitySuggestion? suggestion = new IdentityService(settings, Library(20, 11)).Suggest();
            using (Assert.Multiple())
            {
                await Assert.That(suggestion).IsNotNull();
                await Assert.That(suggestion!.SteamId64).IsEqualTo("76561198000000009");
                await Assert.That(suggestion.Name).IsEqualTo("me");
                await Assert.That(suggestion.DemoCount).IsEqualTo(11);
                await Assert.That(suggestion.Share).IsEqualTo(11 / 20.0);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task AcceptingTheSuggestion_SetsActiveOnlyWhenTheListWasEmpty()
    {
        string dir = NewTempDir();
        string dir2 = NewTempDir();
        try
        {
            using IdentityService empty = new(new SettingsService(dir), Library(20, 15));
            empty.AcceptSuggestion(empty.Suggest()!);
            using (Assert.Multiple())
            {
                await Assert.That(Ids(empty)).IsEquivalentTo(["76561198000000009"], CollectionOrdering.Matching);
                await Assert.That(empty.ActiveSteamId64).IsEqualTo("76561198000000009");
                await Assert.That(empty.Suggest()).IsNull().Because("it is the user's now");
            }

            using IdentityService some = new(new SettingsService(dir2), Library(20, 15));
            some.SetAccounts([Account(A)]);
            some.AcceptSuggestion(some.Suggest()!);
            using (Assert.Multiple())
            {
                await Assert.That(Ids(some)).IsEquivalentTo([A, "76561198000000009"], CollectionOrdering.Matching).Because("it adds");
                await Assert.That(some.ActiveSteamId64).IsEqualTo(A).Because("an existing active account stays");
            }
        }
        finally
        {
            Cleanup(dir);
            Cleanup(dir2);
        }
    }

    [Test]
    public async Task ADismissedSuggestion_IsNotOfferedAgain_AcrossARestart()
    {
        string dir = NewTempDir();
        try
        {
            PlayerDirectory library = Library(20, 15);
            using (IdentityService identity = new(new SettingsService(dir), library))
            {
                identity.DismissSuggestion(identity.Suggest()!);
                using (Assert.Multiple())
                {
                    await Assert.That(identity.Suggest()).IsNull();
                    await Assert.That(identity.Accounts).IsEmpty().Because("dismissing adds nothing");
                }
            }

            using IdentityService restarted = new(new SettingsService(dir), library);
            using (Assert.Multiple())
            {
                await Assert.That(restarted.DismissedSuggestions).IsEquivalentTo(["76561198000000009"], CollectionOrdering.Matching);
                await Assert.That(restarted.Suggest()).IsNull();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task Changed_IsRaisedOncePerOwnWrite_AndThroughPostForAnEditElsewhere()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService settings = new(dir);
            List<Action> posted = [];
            using IdentityService identity = new(settings, post: posted.Add);
            int raised = 0;
            identity.Changed += () => raised++;

            identity.SetAccounts([Account(A), Account(B)]);
            identity.SetActive(A);
            await Assert.That(raised).IsEqualTo(1).Because("the second call changed nothing");
            await Assert.That(posted).IsEmpty().Because("an own write raises inline");

            settings.Write(s => s.Identity.ActiveSteamId64 = B);
            await Assert.That(identity.ActiveSteamId64).IsEqualTo(B);
            await Assert.That(posted.Count).IsEqualTo(1);
            posted[0]();
            await Assert.That(raised).IsEqualTo(2);

            settings.Write(s => s.Theme = "light");
            await Assert.That(posted.Count).IsEqualTo(1).Because("an unrelated write is not an identity change");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    [Arguments("76561197960265729", "76561197960265729", false)]
    [Arguments("  76561197960265729 ", "76561197960265729", false)]
    [Arguments("[U:1:1]", "76561197960265729", false)]
    [Arguments("[u:1:2]", "76561197960265730", false)]
    [Arguments("STEAM_1:1:0", "76561197960265729", false)]
    [Arguments("STEAM_0:0:1", "76561197960265730", false)]
    [Arguments("https://steamcommunity.com/profiles/76561197960265729/", "76561197960265729", false)]
    [Arguments("steamcommunity.com/profiles/76561197960265729", "76561197960265729", false)]
    [Arguments("http://www.steamcommunity.com/profiles/[U:1:1]?l=english", "76561197960265729", false)]
    [Arguments("1", "76561197960265729", true)]
    [Arguments("4294967295", "76561202255233023", true)]
    public async Task ParseAccount_ReadsEveryForm(string text, string expected, bool fromAccountId)
    {
        AccountInput input = IdentityService.ParseAccount(text);
        using (Assert.Multiple())
        {
            await Assert.That(input.SteamId64).IsEqualTo(expected);
            await Assert.That(input.FromAccountId).IsEqualTo(fromAccountId);
            await Assert.That(input.Problem).IsNull();
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("grim")]
    [Arguments("0")]
    [Arguments("76561197960265728")]
    [Arguments("76561202255233024")]
    [Arguments("STEAM_1:2:5")]
    [Arguments("[U:1:4294967296]")]
    [Arguments("https://steamcommunity.com/id/grimnir/")]
    public async Task ParseAccount_RefusesWhatNamesNoAccount(string text)
    {
        AccountInput input = IdentityService.ParseAccount(text);
        using (Assert.Multiple())
        {
            await Assert.That(input.SteamId64).IsNull();
            await Assert.That(input.Problem).IsNotNull();
        }
    }

    [Test]
    public async Task ParseAccount_SaysAVanityLinkNeedsTheOnlineExtension()
    {
        AccountInput input = IdentityService.ParseAccount("https://steamcommunity.com/id/grimnir");
        await Assert.That(input.Problem).Contains("Online Connectivity");
    }
}
