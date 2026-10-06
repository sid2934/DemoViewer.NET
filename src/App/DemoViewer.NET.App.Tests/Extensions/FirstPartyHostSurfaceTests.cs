#region

using System.Reflection;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="FirstPartyHost" /> is the Strat Book's one way past the SDK's storage. It names each store of the
///     user's own work that stays where users have it, and nothing broader: no root folder, so the pack cannot
///     reach settings.json, another extension's data or the shared cache through it.
/// </summary>
[NotInParallel]
public class FirstPartyHostSurfaceTests
{
    private const string UserWork = "user work that stays where users have it";

    /// <summary>Every public member, with the reason it is there. A new member needs a reason here first.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["StratsDirectory"] = UserWork + ": strat books",
        ["TagsDirectory"] = UserWork + ": round tags",
        ["TeamsFile"] = UserWork + ": teams",
        ["WatchedSituationsFile"] = UserWork + ": watched situations",
        ["VetoHistoryFile"] = UserWork + ": veto history",
        ["DossierNotesFile"] = UserWork + ": dossier notes",
        ["SuggestedTagsDirectory"] = UserWork + ": the Suggested Tags profile and site regions",
        ["LineupClipsDirectory"] = UserWork + ": rendered lineup clips",
        ["EnsurePalettesDirectory"] = UserWork + ": tag palettes",
        ["ZonesDirectory"] = "the app's zones overlay, read only; the zone loader reads it per call",
        ["ReadLegacyGrenadeLineups"] = "hand-tuned lineups an older build kept in the shared cache, read once to copy",
        ["ReadLegacyStratMiningState"] = "dismissed and promoted patterns an older build kept in the config folder, read once to copy",
    };

    [Test]
    public async Task EveryPublicMember_HasAReason()
    {
        string[] members =
        [
            .. typeof(FirstPartyHost)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m is PropertyInfo or FieldInfo or EventInfo or MethodInfo { IsSpecialName: false })
                .Select(m => m.Name)
                .Order(StringComparer.Ordinal)
        ];

        await Assert.That(members).IsEquivalentTo(Allowed.Keys.Order(StringComparer.Ordinal))
            .Because("FirstPartyHost grows only by a store the user's work already lives in, each with its reason in Allowed");
    }

    [Test]
    public async Task EachStore_IsTheFileOrFolderTheStratBookWrites()
    {
        FirstPartyHost host = new();
        string root = AppPaths.ConfigRoot!;
        using (Assert.Multiple())
        {
            await Assert.That(host.TeamsFile).IsEqualTo(Path.Combine(root, TeamIdentityService.TeamsFileName));
            await Assert.That(host.WatchedSituationsFile).IsEqualTo(Path.Combine(root, WatchedSituationsService.FileName));
            await Assert.That(host.VetoHistoryFile).IsEqualTo(Path.Combine(root, VetoHistoryStore.FileName));
            await Assert.That(host.DossierNotesFile).IsEqualTo(Path.Combine(root, DossierNotesStore.FileName));
            await Assert.That(host.LineupClipsDirectory).IsEqualTo(Path.Combine(root, LineupClipService.DirectoryName));
            await Assert.That(host.StratsDirectory).IsEqualTo(AppPaths.StratsDir);
            await Assert.That(host.TagsDirectory).IsEqualTo(AppPaths.TagsDir);
            await Assert.That(host.SuggestedTagsDirectory).IsEqualTo(AppPaths.SuggestedTagsDirectory);
            await Assert.That(host.ZonesDirectory).IsEqualTo(AppPaths.ZonesDirectory);
        }
    }

    [Test]
    public async Task TheLegacyReads_FindTheFilesWhereAnOlderBuildKeptThem()
    {
        string lineups = Path.Combine(AppPaths.DemoCacheDir!, GrenadeLineupStore.FileName);
        string mining = Path.Combine(AppPaths.ConfigRoot!, StratMiningFiles.StatePath);
        Directory.CreateDirectory(AppPaths.DemoCacheDir!);
        await File.WriteAllBytesAsync(lineups, [1, 2, 3]);
        await File.WriteAllBytesAsync(mining, [4, 5]);
        try
        {
            FirstPartyHost host = new();
            using (Assert.Multiple())
            {
                await Assert.That(host.ReadLegacyGrenadeLineups()).IsEquivalentTo(new byte[] { 1, 2, 3 });
                await Assert.That(host.ReadLegacyStratMiningState()).IsEquivalentTo(new byte[] { 4, 5 });
            }
        }
        finally
        {
            File.Delete(lineups);
            File.Delete(mining);
        }

        await Assert.That(new FirstPartyHost().ReadLegacyGrenadeLineups()).IsNull().Because("no file reads as nothing to copy");
    }
}
