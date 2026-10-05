#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An extension's settings file: values round-trip by key, a change is told once with its key, a value
///     that does not read as the asked type answers the fallback, and the settings a first-party extension
///     once kept in settings.json are copied into its own file before anything can drop them.
/// </summary>
public class ExtensionSettingsStoreTests
{
    private enum Shade
    {
        Light,
        Dark
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvextsettings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Test]
    public async Task Values_RoundTripThroughTheFile_AndAnotherStoreOverTheSameFileReadsThem()
    {
        string root = NewRoot();
        try
        {
            ExtensionSettingsStore store = new("dev.example.settings", root, a => a());
            store.Set("flag", true);
            store.Set("count", 7);
            store.Set("name", "dust");
            store.Set("shade", Shade.Dark);

            ExtensionSettingsStore reread = new("dev.example.settings", root, a => a());
            string file = ExtensionSettingsStore.PathFor(root, "dev.example.settings");
            using (Assert.Multiple())
            {
                await Assert.That(reread.Get("flag", false)).IsTrue();
                await Assert.That(reread.Get("count", 0)).IsEqualTo(7);
                await Assert.That(reread.Get("name", "")).IsEqualTo("dust");
                await Assert.That(reread.Get("shade", Shade.Light)).IsEqualTo(Shade.Dark);
                await Assert.That(reread.Get("missing", 42)).IsEqualTo(42);
                await Assert.That(File.ReadAllText(file)).Contains("\"Dark\"").Because("an enum is stored by name");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AChange_IsToldOnceWithItsKey_AndRewritingTheSameValueTellsNothing()
    {
        ExtensionSettingsStore store = new("dev.example.settings", null, a => a());
        List<string> told = [];
        store.Changed += told.Add;

        store.Set("flag", true);
        store.Set("flag", true);
        store.Set("other", 1);
        await Assert.That(store.Remove("flag")).IsTrue();
        await Assert.That(store.Remove("flag")).IsFalse();

        await Assert.That(told).IsEquivalentTo(["flag", "other", "flag"]);
    }

    [Test]
    public async Task AValueThatDoesNotReadAsTheAskedType_AnswersTheFallback()
    {
        string root = NewRoot();
        try
        {
            string file = ExtensionSettingsStore.PathFor(root, "dev.example.settings");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, """{ "flag": "maybe", "shade": "Sepia", "count": 2.5 }""");

            ExtensionSettingsStore store = new("dev.example.settings", root, a => a());
            using (Assert.Multiple())
            {
                await Assert.That(store.Get("flag", true)).IsTrue();
                await Assert.That(store.Get("shade", Shade.Light)).IsEqualTo(Shade.Light);
                await Assert.That(store.Get("count", 4)).IsEqualTo(4);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AnUnreadableFile_ReadsAsEmpty_AndTheNextWriteReplacesIt()
    {
        string root = NewRoot();
        try
        {
            string file = ExtensionSettingsStore.PathFor(root, "dev.example.settings");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "{ not json");

            ExtensionSettingsStore store = new("dev.example.settings", root, a => a());
            await Assert.That(store.Get("flag", false)).IsFalse();
            store.Set("flag", true);

            await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(file))!["flag"]!.GetValue<bool>()).IsTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TwoExtensions_KeepTwoFiles_AndNeitherSeesTheOthersValues()
    {
        string root = NewRoot();
        try
        {
            ExtensionSettingsStore one = new("dev.example.one", root, a => a());
            ExtensionSettingsStore two = new("dev.example.two", root, a => a());
            one.Set("shared", "one");

            using (Assert.Multiple())
            {
                await Assert.That(two.Get("shared", "unset")).IsEqualTo("unset");
                await Assert.That(Directory.GetFiles(Path.Combine(root, ExtensionSettingsStore.DirectoryName)))
                    .IsEquivalentTo([ExtensionSettingsStore.PathFor(root, "dev.example.one")]);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    [Arguments("../escape")]
    [Arguments("..")]
    [Arguments("a/b")]
    [Arguments("")]
    public async Task AnIdThatIsNotOneSafeName_IsRefused(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            ExtensionSettingsStore.PathFor("/tmp", id);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task TheStratBooksOldSettings_AreCopiedOnce_AndSurviveTheNextWriteOfSettingsJson()
    {
        string root = NewRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), """
                {
                  "Situations": { "BackgroundIndex": false, "TokenSource": "Zones" },
                  "Grenades": { "BackgroundIndex": "true", "TrajectoryStride": 2 },
                  "Playback2D": { "TagPaletteId": "team-palette", "ReviewMode": true }
                }
                """);
            SettingsService settings = new(root);

            await Assert.That(LegacyExtensionSettings.Import(settings, root)).IsEqualTo(1);
            // A core write rewrites the Playback2D section from what AppSettings models.
            settings.Write(s => s.Playback2D.ExportQuality = "best");
            await Assert.That(LegacyExtensionSettings.Import(settings, root)).IsEqualTo(0)
                .Because("the file exists, so the copy never runs again");

            ExtensionSettingsStore store = new(LegacyExtensionSettings.StratBookId, root, a => a());
            using (Assert.Multiple())
            {
                await Assert.That(store.Get("situations.backgroundIndex", true)).IsFalse();
                await Assert.That(store.Get("situations.tokenSource", "Pawn")).IsEqualTo("Zones");
                await Assert.That(store.Get("grenades.backgroundIndex", false)).IsTrue();
                await Assert.That(store.Get("grenades.trajectoryStride", 4)).IsEqualTo(2);
                await Assert.That(store.Get("tagPalette.id", "cs2-default")).IsEqualTo("team-palette");
                await Assert.That(store.Get("review.mode", false)).IsTrue();
                await Assert.That(store.Get("suggestedTags.background", false)).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task NoOldSettings_WritesNoFile()
    {
        string root = NewRoot();
        try
        {
            SettingsService settings = new(root);
            await Assert.That(LegacyExtensionSettings.Import(settings, root)).IsEqualTo(0);
            await Assert.That(Directory.Exists(Path.Combine(root, ExtensionSettingsStore.DirectoryName))).IsFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
