#region

using System.Text.Json;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Identity;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     What an extension reads of the user's accounts: <see cref="IdentityService" /> through <see cref="HostIdentity" />,
///     and the Strat Book's copy of it. Every settings file is in a throwaway folder and every demo store in memory.
/// </summary>
public class ExtensionIdentityTests
{
    private const string A = "76561198000000001";
    private const string B = "76561198000000002";
    private static readonly DateTime Added = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvextidentity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IdentityAccount Account(string id) => new(id, null, Added);

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = "de_nuke"
        };
        int slot = 0;
        foreach ((string id, int team) in t.Select(id => (id, 2)).Concat(ct.Select(id => (id, 3))))
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = team });
        }

        DemoCacheStore.StampParse(record);
        return record;
    }

    private static string[] Five(int first) => [.. Enumerable.Range(first, 5).Select(n => $"7656119810000{n:D4}")];

    [Test]
    public async Task TheHostIdentity_ReadsTheService_AndRaisesOnlyWhenTheAccountsOrTheActiveMove()
    {
        string dir = NewTempDir();
        try
        {
            using IdentityService service = new(new SettingsService(dir));
            HostIdentity identity = new(() => service, ExtensionPassHostTests.StandaloneGuard());
            int raised = 0;
            identity.Changed += () => throw new InvalidOperationException("a faulty handler");
            identity.Changed += () => raised++;

            service.SetAccounts([Account(A), Account(B)]);
            using (Assert.Multiple())
            {
                await Assert.That(identity.Accounts).IsEquivalentTo([A, B]);
                await Assert.That(identity.ActiveAccount).IsEqualTo(A);
                await Assert.That(identity.IsMine(B)).IsTrue();
                await Assert.That(identity.IsMine("76561198000000003")).IsFalse();
                await Assert.That(identity.IsMine(null)).IsFalse();
                await Assert.That(raised).IsEqualTo(1).Because("a throwing handler before it does not stop the next");
            }

            service.SetActive(B);
            await Assert.That(raised).IsEqualTo(2);
            service.DismissSuggestion(new IdentitySuggestion("76561198000000009", "x", 30, 0.9));
            await Assert.That(raised).IsEqualTo(2).Because("a dismissed suggestion is not the user's accounts");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task WithNoService_TheIdentityIsEmpty_AndNeverRaises()
    {
        HostIdentity identity = new(() => null, ExtensionPassHostTests.StandaloneGuard());
        identity.Changed += () => throw new InvalidOperationException("never raised");
        using (Assert.Multiple())
        {
            await Assert.That(identity.Accounts).IsEmpty();
            await Assert.That(identity.ActiveAccount).IsNull();
            await Assert.That(identity.IsMine(A)).IsFalse();
        }
    }

    [Test]
    public async Task EditingMeInSettings_GivesOneList_InSettings_TeamsJson_AndOurSide()
    {
        string dir = NewTempDir();
        try
        {
            string teamsPath = Path.Combine(dir, "teams.json");
            using IdentityService service = new(new SettingsService(dir));
            HostIdentity identity = new(() => service, ExtensionPassHostTests.StandaloneGuard());
            DemoCacheStore cache = new(null);
            using TeamIdentityService teams = new(new TeamIdentityFiles(teamsPath, null), cache.Library(), run: _inline, identity: identity);
            await teams.StartAsync();
            string[] mine = [A, .. Five(1)[1..]];
            string[] other = [B, .. Five(11)[1..]];
            using (cache.BeginBatch())
            {
                cache.Upsert(Record("/d/a.dem", 1, mine, Five(21)));
                cache.Upsert(Record("/d/b.dem", 2, Five(31), other));
            }

            await teams.Idle;
            await Assert.That(teams.OurDemos()).IsEmpty();

            service.SetAccounts([Account(A), Account(B)]);
            string[] inSettings = [.. new SettingsService(dir).Current.Identity.Accounts.Select(a => a.SteamId64)];
            TeamsFile file = JsonSerializer.Deserialize<TeamsFile>(File.ReadAllText(teamsPath), TeamsFile.JsonOptions)!;
            using (Assert.Multiple())
            {
                await Assert.That(inSettings).IsEquivalentTo([A, B]);
                await Assert.That(file.Me.SteamIds).IsEquivalentTo(inSettings);
                await Assert.That(teams.MyAccounts).IsEquivalentTo(inSettings);
                await Assert.That(teams.GetAssignment("/d/a.dem")!.OurSide).IsEqualTo(2);
                await Assert.That(teams.GetAssignment("/d/b.dem")!.OurSide).IsEqualTo(3);
            }

            service.SetAccounts([Account(B)]);
            file = JsonSerializer.Deserialize<TeamsFile>(File.ReadAllText(teamsPath), TeamsFile.JsonOptions)!;
            using (Assert.Multiple())
            {
                await Assert.That(file.Me.SteamIds).IsEquivalentTo([B]);
                await Assert.That(teams.GetAssignment("/d/a.dem")!.OurSide).IsNull().Because("the account that resolved it was removed");
                await Assert.That(teams.OurDemos().Select(d => d.Path)).IsEquivalentTo(["/d/b.dem"]);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
