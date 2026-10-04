#region

using System.Text.Json;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Models;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="SessionPayload.Packs" /> through the real <see cref="SettingsService" />: a
///     pre-<c>Packs</c> file's top-level <c>StratBook</c> member folds once, a new file's <c>Packs</c>
///     dictionary round-trips byte for byte including ids this build does not own, and a per-pack blob
///     this build cannot make sense of never throws. The pack-off carry-through and the hub's own
///     tolerant restore are shell-level (<c>AppTests.StratBookShellTests</c>): the store itself
///     has no notion of a pack being on or off.
/// </summary>
public class SessionPackStateTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvsessionpacks_" + Guid.NewGuid().ToString("N"));
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

    [Test]
    public async Task OldFile_WithTopLevelStratBook_FoldsIntoPacksKeyedByThePackId()
    {
        string dir = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), """
                {
                  "Session": {
                    "DebuggerVisible": false,
                    "OutputVisible": false,
                    "ActiveTabId": "stratbook.browser",
                    "StratBook": { "RailCollapsed": true, "ListCollapsed": false }
                  }
                }
                """);

            SessionPayload? loaded = new SettingsService(dir).LoadSession();

            await Assert.That(loaded).IsNotNull();
            JsonElement folded = loaded!.Packs![StratBookPack.PackId];
            using (Assert.Multiple())
            {
                await Assert.That(folded.GetProperty("RailCollapsed").GetBoolean()).IsTrue();
                await Assert.That(folded.GetProperty("ListCollapsed").GetBoolean()).IsFalse();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task OldFile_WithBothTopLevelStratBookAndAPacksEntryForTheSameId_ThePacksEntryWins()
    {
        string dir = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), """
                {
                  "Session": {
                    "DebuggerVisible": false,
                    "OutputVisible": false,
                    "StratBook": { "RailCollapsed": true, "ListCollapsed": true },
                    "Packs": { "net.demoviewer.pack.stratbook": { "RailCollapsed": false, "ListCollapsed": false } }
                  }
                }
                """);

            SessionPayload? loaded = new SettingsService(dir).LoadSession();

            await Assert.That(loaded!.Packs![StratBookPack.PackId].GetProperty("RailCollapsed").GetBoolean()).IsFalse()
                .Because("the new-shape entry already present must not be overwritten by the legacy fold");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task NewFile_WithMultiplePackEntries_RoundTripsThemAllUnchanged()
    {
        string dir = NewTempDir();
        try
        {
            Dictionary<string, JsonElement> packs = new(StringComparer.Ordinal)
            {
                [StratBookPack.PackId] = JsonSerializer.SerializeToElement(new { RailCollapsed = true, ListCollapsed = false }),
                // An id no pack in this build owns: must survive untouched, the way a pack that has not
                // shipped yet (or one this build does not have) still round-trips.
                ["net.demoviewer.pack.future"] = JsonSerializer.SerializeToElement(new { Anything = 1 })
            };

            SettingsService svc = new(dir);
            svc.SaveSession(new SessionPayload(null, null, null, false, false, null, null, null, packs));

            SessionPayload? loaded = new SettingsService(dir).LoadSession();

            await Assert.That(loaded!.Packs!.Keys).IsEquivalentTo(packs.Keys);
            using (Assert.Multiple())
            {
                await Assert.That(JsonElement.DeepEquals(loaded.Packs[StratBookPack.PackId], packs[StratBookPack.PackId])).IsTrue();
                await Assert.That(JsonElement.DeepEquals(loaded.Packs["net.demoviewer.pack.future"], packs["net.demoviewer.pack.future"])).IsTrue();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task OldFile_AfterSave_WritesThePacksShapeAndKeepsOtherSessionFields()
    {
        string dir = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), """
                {
                  "Session": {
                    "DebuggerVisible": true,
                    "OutputVisible": true,
                    "ActiveTabId": "stratbook.browser",
                    "ModuleTabs": { "builtin.parser": { "Hex": true } },
                    "Window": { "Width": 1280.0, "Height": 720.0, "X": null, "Y": null, "Maximized": false },
                    "StratBook": { "RailCollapsed": true, "ListCollapsed": false }
                  }
                }
                """);

            SettingsService svc = new(dir);
            SessionPayload? loaded = svc.LoadSession();
            svc.SaveSession(loaded!);

            using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "settings.json")));
            JsonElement session = doc.RootElement.GetProperty("Session");

            using (Assert.Multiple())
            {
                await Assert.That(session.TryGetProperty("StratBook", out _)).IsFalse()
                    .Because("a re-save after the fold writes the new shape, never the legacy one");
                await Assert.That(session.GetProperty("Packs").GetProperty(StratBookPack.PackId)
                    .GetProperty("RailCollapsed").GetBoolean()).IsTrue();
                await Assert.That(session.GetProperty("ActiveTabId").GetString()).IsEqualTo("stratbook.browser");
                await Assert.That(session.GetProperty("ModuleTabs").GetProperty("builtin.parser")
                    .GetProperty("Hex").GetBoolean()).IsTrue();
                await Assert.That(session.GetProperty("Window").GetProperty("Width").GetDouble()).IsEqualTo(1280.0);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task OldFile_WithTopLevelStratBookAsNull_FoldsNothing_AndDoesNotThrow()
    {
        string dir = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), """
                {
                  "Session": {
                    "DebuggerVisible": false,
                    "OutputVisible": false,
                    "StratBook": null
                  }
                }
                """);

            SessionPayload? loaded = new SettingsService(dir).LoadSession();

            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.Packs).IsNull();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task OldFile_WithTopLevelStratBookAsANumber_FoldsNothing_AndDoesNotThrow()
    {
        string dir = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), """
                {
                  "Session": {
                    "DebuggerVisible": false,
                    "OutputVisible": false,
                    "StratBook": 42
                  }
                }
                """);

            SessionPayload? loaded = new SettingsService(dir).LoadSession();

            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.Packs).IsNull();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task UnreadablePackBlob_RoundTripsWithoutThrowing()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            svc.SaveSession(new SessionPayload(null, null, null, false, false, null, null, null,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [StratBookPack.PackId] = JsonSerializer.SerializeToElement(42)
                }));

            SessionPayload? loaded = new SettingsService(dir).LoadSession();

            await Assert.That(loaded!.Packs![StratBookPack.PackId].ValueKind).IsEqualTo(JsonValueKind.Number)
                .Because("the store holds a pack blob opaquely; only the owning pack's Restore interprets its shape");
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
