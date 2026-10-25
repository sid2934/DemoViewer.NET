#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.TestSupport;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>Identity</c> section of <c>settings.json</c> is a published format (<c>docs/identity-format.md</c>): this
///     build writes the committed sample's shape, reads it back through the configuration binder, and keeps it over the
///     browser's in-memory settings.
/// </summary>
public class IdentitySettingsTests
{
    private const string Main = "76561198000000001";
    private const string Dismissed = "76561198000000009";
    private static readonly DateTime Added = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvidentityfmt_" + Guid.NewGuid().ToString("N"));
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

    private static string Lf(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    // The sample's state, written the way the app writes it: the import's flag, then the service.
    private static void WriteSample(SettingsService settings)
    {
        settings.Write(s => s.Identity.MigratedFromTeamsJson = true);
        using IdentityService identity = new(settings);
        identity.SetAccounts([new IdentityAccount(Main, "main", Added)]);
        identity.DismissSuggestion(new IdentitySuggestion(Dismissed, "someone", 30, 0.6));
    }

    // Only the Identity section of a settings file, as the sample holds it.
    private static string SectionOf(string settingsJson) =>
        Lf(new JsonObject { ["Identity"] = JsonNode.Parse(settingsJson)!["Identity"]!.DeepClone() }.ToJsonString(Indented));

    [Test]
    public async Task V1Schema_MatchesTheCheckedInSamples()
    {
        string? repo = DemoTestHelper.FindRepoRoot();
        if (repo is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string dir = NewTempDir();
        string back = NewTempDir();
        try
        {
            WriteSample(new SettingsService(dir));
            string json = SectionOf(File.ReadAllText(Path.Combine(dir, "settings.json")));
            string path = Path.Combine(repo, "tests", "fixtures", "identity", "settings.identity.sample.json");
            if (Environment.GetEnvironmentVariable("IDENTITY_GOLDEN_UPDATE") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }

            if (!File.Exists(path))
            {
                throw new SkipTestException("missing the identity sample; regenerate with IDENTITY_GOLDEN_UPDATE=1");
            }

            string committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            // The committed sample as a whole settings file, read through the binder the app reads it with.
            File.WriteAllText(Path.Combine(back, "settings.json"), committed);
            SettingsService settings = new(back);
            using IdentityService identity = new(settings);
            IdentityAccount account = identity.Accounts.Single();
            using (Assert.Multiple())
            {
                await Assert.That(json).IsEqualTo(committed).Because("the Identity section is a published format; this build still writes the committed shape");
                await Assert.That(account.SteamId64).IsEqualTo(Main);
                await Assert.That(account.Label).IsEqualTo("main");
                await Assert.That(account.AddedUtc).IsEqualTo(Added);
                await Assert.That(account.AddedUtc.Kind).IsEqualTo(DateTimeKind.Utc).Because("the binder reads Z as local time; the service gives UTC");
                await Assert.That(identity.ActiveSteamId64).IsEqualTo(Main);
                await Assert.That(identity.DismissedSuggestions).IsEquivalentTo([Dismissed], CollectionOrdering.Matching);
                await Assert.That(settings.Current.Identity.MigratedFromTeamsJson).IsTrue();
                await Assert.That(committed).Contains("\"SteamId64\": \"76561198000000001\"").Because("SteamIDs are strings");
            }

            // A write through the service over the read-back file leaves the section as committed.
            identity.SetActive(Main);
            identity.SetAccounts([.. identity.Accounts]);
            await Assert.That(SectionOf(File.ReadAllText(Path.Combine(back, "settings.json")))).IsEqualTo(committed).Because("round trip");
        }
        finally
        {
            Cleanup(dir);
            Cleanup(back);
        }
    }

    [Test]
    public async Task TheSection_SurvivesAFilelessWrite()
    {
        SettingsService settings = new(null);
        WriteSample(settings);
        using IdentityService identity = new(settings);
        identity.SetAccounts([.. identity.Accounts, new IdentityAccount("76561198000000002", null, Added)]);

        using IdentityService reread = new(settings);
        using (Assert.Multiple())
        {
            await Assert.That(reread.Accounts.Select(a => a.SteamId64)).IsEquivalentTo([Main, "76561198000000002"], CollectionOrdering.Matching);
            await Assert.That(reread.Accounts[0].Label).IsEqualTo("main");
            await Assert.That(reread.Accounts[1].Label).IsNull();
            await Assert.That(reread.Accounts[0].AddedUtc).IsEqualTo(Added);
            await Assert.That(reread.ActiveSteamId64).IsEqualTo(Main);
            await Assert.That(reread.DismissedSuggestions).IsEquivalentTo([Dismissed], CollectionOrdering.Matching);
            await Assert.That(settings.Current.Identity.MigratedFromTeamsJson).IsTrue();
        }

        identity.SetAccounts([]);
        using IdentityService emptied = new(settings);
        await Assert.That(emptied.Accounts).IsEmpty().Because("a shrinking list leaves no stale index behind");
    }
}
