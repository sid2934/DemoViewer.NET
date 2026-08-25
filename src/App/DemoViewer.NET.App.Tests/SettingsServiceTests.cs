#region

using System.Text.Json;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Models;
using DemoViewer.NET.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers the writable settings layer end-to-end against a temp config dir (never the real user config
///     folder): atomic write → fresh-service round-trip, synchronous self-write <c>OnChange</c> through an
///     <c>IOptionsMonitor&lt;AppSettings&gt;</c>, corrupt-file resilience, and env-var precedence over the
///     file. <see cref="NotInParallelAttribute" /> because the env-override case mutates a process-global
///     variable that the configuration stack reads for <em>all</em> <c>DEMOVIEWER_</c>-prefixed keys.
/// </summary>
[NotInParallel]
public class SettingsServiceTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvsettings_" + Guid.NewGuid().ToString("N"));
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
    public async Task Write_ThenFreshService_RoundTripsAllValues()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            await Assert.That(svc.NeedsFirstRun).IsTrue(); // no file yet

            svc.Write(s =>
            {
                s.UserCategory = UserCategory.Consumer;
                s.Library.Folders = [.. s.Library.Folders, "/demos/major"];
                s.Features.Overrides["fancyGraph"] = true;
                s.Theme = "Light";
                s.FirstRunCompleted = true; // NeedsFirstRun is driven by this flag, not file existence
            });

            string path = Path.Combine(dir, "settings.json");
            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(svc.NeedsFirstRun).IsFalse();

            // The on-disk JSON is human-editable: enum by string name, folder + override present.
            string json = await File.ReadAllTextAsync(path);
            await Assert.That(json).Contains("Consumer");
            await Assert.That(json).Contains("/demos/major");
            await Assert.That(json).Contains("fancyGraph");

            // A brand-new service over the same dir reads exactly what was written.
            SettingsService svc2 = new(dir);
            AppSettings loaded = svc2.Current;
            await Assert.That(loaded.UserCategory).IsEqualTo(UserCategory.Consumer);
            await Assert.That(loaded.Theme).IsEqualTo("Light");
            await Assert.That(loaded.Library.Folders.Contains("/demos/major")).IsTrue();
            await Assert.That(loaded.Features.Overrides.ContainsKey("fancyGraph")).IsTrue();
            await Assert.That(loaded.Features.Overrides["fancyGraph"]).IsTrue();
            await Assert.That(svc2.NeedsFirstRun).IsFalse();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task NeedsFirstRun_IsDrivenByFlag_NotFileExistence()
    {
        // Regression (the folder-migration-suppresses-wizard bug): NeedsFirstRun must track the
        // FirstRunCompleted flag, NOT whether settings.json exists — the demo-library folder migration can
        // create the file during startup, and that must not count as "setup done" for an upgrading user.
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            await Assert.That(svc.NeedsFirstRun).IsTrue(); // no file, setup not completed

            // A non-wizard write (like the folder migration) creates settings.json but leaves setup
            // incomplete → the wizard must still show.
            svc.Write(s => s.Library.Folders = ["/demos/legacy"]);
            await Assert.That(File.Exists(Path.Combine(dir, "settings.json"))).IsTrue();
            await Assert.That(svc.NeedsFirstRun).IsTrue()
                .Because("creating settings.json is not completing first-run setup");

            // Only marking setup complete flips it.
            svc.Write(s => s.FirstRunCompleted = true);
            await Assert.That(svc.NeedsFirstRun).IsFalse();

            // …and it round-trips: a fresh service over the same dir stays past first-run.
            await Assert.That(new SettingsService(dir).NeedsFirstRun).IsFalse();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task SelfWrite_RaisesOptionsMonitorOnChange_Synchronously()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);

            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            using ServiceProvider sp = services.BuildServiceProvider();
            IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();

            bool fired = false;
            string? observedTheme = null;
            UserCategory observedCategory = default;
            using IDisposable? sub = monitor.OnChange((settings, _) =>
            {
                fired = true;
                observedTheme = settings.Theme;
                observedCategory = settings.UserCategory;
            });

            svc.Write(s =>
            {
                s.Theme = "Light";
                s.UserCategory = UserCategory.Developer;
            });

            // No awaiting, no delay: a self-write reloads synchronously on this thread, so OnChange has
            // already fired by the time Write returns.
            await Assert.That(fired).IsTrue();
            await Assert.That(observedTheme).IsEqualTo("Light");
            await Assert.That(observedCategory).IsEqualTo(UserCategory.Developer);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task CorruptFile_LoadsDefaults_FlagsFirstRun_DoesNotThrow()
    {
        string dir = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), "{ not: valid ]] json ,,,");

            SettingsService svc = new(dir); // must not throw despite the unparseable file
            AppSettings s = svc.Current;

            await Assert.That(s.Theme).IsEqualTo("Dark");
            await Assert.That(s.UserCategory).IsEqualTo(UserCategory.PowerUser);
            await Assert.That(s.Library.Folders.Length).IsEqualTo(0);
            await Assert.That(s.Features.Overrides.Count).IsEqualTo(0);
            await Assert.That(svc.NeedsFirstRun).IsTrue();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task EnvironmentVariable_OverridesFileValue()
    {
        string dir = NewTempDir();
        const string EnvKey = "DEMOVIEWER_Theme";
        string? previous = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            // File on disk says "Dark".
            SettingsService seed = new(dir);
            seed.Write(s => s.Theme = "Dark");

            // Env var is set BEFORE the reading service is constructed (env is read at Build time).
            Environment.SetEnvironmentVariable(EnvKey, "Midnight");
            SettingsService svc = new(dir);

            await Assert.That(svc.Current.Theme).IsEqualTo("Midnight");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, previous);
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     An env override stays effective for READS (highest-precedence layer in <c>Current</c>) but must
    ///     NEVER be baked into settings.json by a write. Here a write of an UNRELATED field must leave the
    ///     env-overridden field at its real FILE value on disk — otherwise a transient <c>DEMOVIEWER_Theme</c>
    ///     would permanently overwrite the user's persisted theme the next time anything is saved.
    /// </summary>
    [Test]
    public async Task Write_OfUnrelatedField_DoesNotPersistEnvOverride()
    {
        string dir = NewTempDir();
        const string EnvKey = "DEMOVIEWER_Theme";
        string? previous = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            // The user's real, persisted theme is "Dark".
            SettingsService seed = new(dir);
            seed.Write(s => s.Theme = "Dark");

            // A transient env override is in effect (env is read at Build time, so construct after setting).
            Environment.SetEnvironmentVariable(EnvKey, "Midnight");
            SettingsService svc = new(dir);
            await Assert.That(svc.Current.Theme).IsEqualTo("Midnight"); // reads DO see the override

            // A write of a DIFFERENT field must not drag the env value into the file.
            svc.Write(s => s.UserCategory = UserCategory.Developer);

            string json = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"));
            await Assert.That(json).Contains("Dark");
            await Assert.That(json.Contains("Midnight")).IsFalse()
                .Because("a transient env override must never be persisted by an unrelated write");

            // Confirm by reading the file with the env layer removed: the real theme survived intact.
            Environment.SetEnvironmentVariable(EnvKey, null);
            SettingsService reader = new(dir);
            await Assert.That(reader.Current.Theme).IsEqualTo("Dark");
            await Assert.That(reader.Current.UserCategory).IsEqualTo(UserCategory.Developer);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, previous);
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The fileless / in-memory (WASM) write path must drop keys a shrink removes — a rebuilt-from-scratch
    ///     provider, not an additive one. A <c>null</c> config dir selects that path even off-browser, so this
    ///     runs the branch the file-backed tests never touch. Without the rebuild a shrunk Library.Folders or a
    ///     cleared Features.Overrides key would linger and a subsequent bind would resurrect it.
    /// </summary>
    [Test]
    public async Task WriteInMemory_ShrinkAndRemove_DropStaleKeys()
    {
        SettingsService svc = new(null); // fileless in-memory path

        svc.Write(s => s.Library.Folders = ["/a", "/b", "/c"]);
        await Assert.That(svc.Current.Library.Folders.Length).IsEqualTo(3);

        svc.Write(s => s.Library.Folders = ["/a"]); // shrink 3 → 1
        await Assert.That(svc.Current.Library.Folders.Length).IsEqualTo(1)
            .Because("the dropped folder indices must not survive as stale keys");
        await Assert.That(svc.Current.Library.Folders[0]).IsEqualTo("/a");

        svc.Write(s => s.Features.Overrides["x"] = true);
        await Assert.That(svc.Current.Features.Overrides.ContainsKey("x")).IsTrue();

        svc.Write(s => s.Features.Overrides.Clear()); // remove the override key
        await Assert.That(svc.Current.Features.Overrides.Count).IsEqualTo(0)
            .Because("a removed override key must not linger in the in-memory provider");
    }

    /// <summary>
    ///     Every <c>Playback2D</c> property must survive the FILELESS write path.
    ///     <para>
    ///         On WASM there is no settings file — only the in-memory provider that
    ///         <c>SettingsService.WriteInMemory</c> populates by hand, key by key. A property that is
    ///         modelled on <c>AppSettings</c> but missing from that method binds fine, writes fine, and
    ///         forgets itself on the next reload, with nothing to see anywhere. B2, B3, B4 and C2 each
    ///         add properties to this one section (registry §3.10), so the trap is set for all of them.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WriteInMemory_RoundTripsEveryPlayback2DProperty()
    {
        SettingsService svc = new(null); // fileless in-memory path

        await Assert.That(svc.Current.Playback2D.LegacyViewport).IsFalse();

        svc.Write(s => s.Playback2D.LegacyViewport = true);
        await Assert.That(svc.Current.Playback2D.LegacyViewport).IsTrue()
            .Because("a Playback2D key missing from WriteInMemory vanishes silently on WASM");

        svc.Write(s => s.Playback2D.LegacyViewport = false);
        await Assert.That(svc.Current.Playback2D.LegacyViewport).IsFalse();
    }

    // ── Consolidated Session + Recents sections ────────────────────────────────

    /// <summary>
    ///     The central consolidation invariant: a <em>preferences</em> write must NOT clobber the
    ///     <c>Session</c> / <c>Recents</c> sections. This also catches the STJ compile-time-<c>TValue</c>
    ///     silent-drop trap an inheritance-DTO would have introduced. Save session + recents, do a preference
    ///     write, then read all three back through a FRESH service.
    /// </summary>
    [Test]
    public async Task PreferenceWrite_PreservesSessionAndRecents()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            SessionPayload session = new(
                new TabSessionState(7, "root/x", true), null, null,
                true, false, "parser");
            List<RecentFile> recents = new()
            {
                new RecentFile("/demos/a.dem", "de_dust2", DateTime.UtcNow)
            };
            svc.SaveSession(session);
            svc.SaveRecents(recents);

            // A PREFERENCES write (reads basis → mutates prefs → writes the whole file) must round-trip the
            // non-preference sections untouched.
            svc.Write(s =>
            {
                s.Theme = "Light";
                s.FirstRunCompleted = true;
            });

            SettingsService svc2 = new(dir);
            await Assert.That(svc2.Current.Theme).IsEqualTo("Light");

            SessionPayload? loadedSession = svc2.LoadSession();
            await Assert.That(loadedSession).IsNotNull();
            await Assert.That(loadedSession!.ActiveTabId).IsEqualTo("parser");
            await Assert.That(loadedSession.Parser!.SelectedFrameIndex).IsEqualTo(7);
            await Assert.That(loadedSession.Parser!.ShowRawHex).IsTrue();
            await Assert.That(loadedSession.DebuggerVisible).IsTrue();

            IReadOnlyList<RecentFile> loadedRecents = svc2.LoadRecents();
            await Assert.That(loadedRecents.Count).IsEqualTo(1);
            await Assert.That(loadedRecents[0].Path).IsEqualTo("/demos/a.dem");
            await Assert.That(loadedRecents[0].MapName).IsEqualTo("de_dust2");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The reverse invariant: a non-reactive <c>Session</c> / <c>Recents</c> write must NOT clobber the
    ///     preference sections (single-serializer round-trips the whole file).
    /// </summary>
    [Test]
    public async Task SessionOrRecentsWrite_PreservesPreferences()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            svc.Write(s =>
            {
                s.UserCategory = UserCategory.Developer;
                s.Theme = "Light";
                s.FirstRunCompleted = true;
                s.Library.Folders = ["/demos/x"];
            });

            // Non-reactive writes (no Reload) into the two consolidated sections.
            svc.SaveSession(new SessionPayload(null, null, null, false, false, "library"));
            svc.SaveRecents([new RecentFile("/demos/a.dem", null, DateTime.UtcNow)]);

            SettingsService svc2 = new(dir);
            await Assert.That(svc2.Current.UserCategory).IsEqualTo(UserCategory.Developer);
            await Assert.That(svc2.Current.Theme).IsEqualTo("Light");
            await Assert.That(svc2.Current.FirstRunCompleted).IsTrue();
            await Assert.That(svc2.Current.Library.Folders.Contains("/demos/x")).IsTrue();
            await Assert.That(svc2.NeedsFirstRun).IsFalse()
                .Because("a session/recents write must not reset the persisted first-run flag");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     One-time migration: legacy standalone <c>session.json</c> / <c>recent-files.json</c> are
    ///     imported into the merged file on first load, the old files are preserved as <c>.bak</c> (never
    ///     deleted), and the import is not repeated — even if a legacy file is dropped back in.
    /// </summary>
    [Test]
    public async Task LegacyFiles_ImportedOnce_AndPreservedAsBak()
    {
        string dir = NewTempDir();
        try
        {
            // Seed the legacy standalone files (as the pre-consolidation stores wrote them).
            SessionPayload legacySession = new(
                new TabSessionState(3, null, false), null, null, false, true, "entity");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "session.json"), JsonSerializer.Serialize(legacySession));
            List<RecentFile> legacyRecents = new()
            {
                new RecentFile("/demos/legacy.dem", "de_nuke", DateTime.UtcNow)
            };
            await File.WriteAllTextAsync(
                Path.Combine(dir, "recent-files.json"), JsonSerializer.Serialize(legacyRecents));

            // First load imports both legacy files into the single config file.
            SettingsService svc = new(dir);
            SessionPayload? s = svc.LoadSession();
            await Assert.That(s!.ActiveTabId).IsEqualTo("entity");
            await Assert.That(s.Parser!.SelectedFrameIndex).IsEqualTo(3);
            IReadOnlyList<RecentFile> r = svc.LoadRecents();
            await Assert.That(r.Count).IsEqualTo(1);
            await Assert.That(r[0].Path).IsEqualTo("/demos/legacy.dem");

            // Old files preserved as .bak (never deleted); the originals are gone.
            await Assert.That(File.Exists(Path.Combine(dir, "session.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, "session.json.bak"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "recent-files.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, "recent-files.json.bak"))).IsTrue();

            // The merged file now owns both sections → a fresh service reads them straight back.
            SettingsService svc2 = new(dir);
            await Assert.That(svc2.LoadSession()!.ActiveTabId).IsEqualTo("entity");
            await Assert.That(svc2.LoadRecents().Count).IsEqualTo(1);

            // Idempotency: a re-dropped legacy file must NOT overwrite the already-migrated section, and is
            // left untouched (the absent-section guard never re-enters once the section exists).
            await File.WriteAllTextAsync(Path.Combine(dir, "recent-files.json"),
                JsonSerializer.Serialize(new List<RecentFile>
                {
                    new("/demos/other.dem", null, DateTime.UtcNow)
                }));
            SettingsService svc3 = new(dir);
            IReadOnlyList<RecentFile> r3 = svc3.LoadRecents();
            await Assert.That(r3.Count).IsEqualTo(1);
            await Assert.That(r3[0].Path).IsEqualTo("/demos/legacy.dem")
                .Because("the merged section already exists, so a re-dropped legacy file is not re-imported");
            await Assert.That(File.Exists(Path.Combine(dir, "recent-files.json"))).IsTrue()
                .Because("a legacy file dropped in after migration is left in place, not consumed");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     No legacy files, no persisted sections → <see cref="SettingsService.LoadSession" /> is
    ///     <c>null</c> and <see cref="SettingsService.LoadRecents" /> is empty (a fresh install).
    /// </summary>
    [Test]
    public async Task FreshInstall_SessionNull_RecentsEmpty()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir);
            await Assert.That(svc.LoadSession()).IsNull();
            await Assert.That(svc.LoadRecents().Count).IsEqualTo(0);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     Best-effort semantics (preserved from the pre-consolidation stores): a <c>SaveSession</c> /
    ///     <c>SaveRecents</c> whose write cannot land (here the <c>settings.json</c> path is occupied by a
    ///     DIRECTORY, so the atomic <c>File.Move</c> fails) must NOT throw — a shutdown-save or demo-open
    ///     never crashes the app. The reactive preference <c>Write</c>, by contrast, still surfaces the error.
    /// </summary>
    [Test]
    public async Task SaveSessionAndRecents_AreBestEffort_DoNotThrowOnWriteFailure()
    {
        string dir = NewTempDir();
        try
        {
            SettingsService svc = new(dir); // constructs cleanly (no settings.json yet)

            // Occupy the settings.json name with a DIRECTORY so the write's File.Move(temp, settings.json)
            // fails on every OS.
            Directory.CreateDirectory(Path.Combine(dir, "settings.json"));

            // Best-effort sections: swallow the failure (no throw).
            bool savesThrew = false;
            try
            {
                svc.SaveSession(new SessionPayload(null, null, null, false, false, "parser"));
                svc.SaveRecents([new RecentFile("/demos/a.dem", null, DateTime.UtcNow)]);
            }
            catch
            {
                savesThrew = true;
            }

            await Assert.That(savesThrew).IsFalse()
                .Because("both best-effort saves swallow a write failure rather than crashing the app");

            // The reactive preference write is NOT best-effort — it still throws so the UI can surface it.
            bool writeThrew = false;
            try
            {
                svc.Write(s => s.Theme = "Light");
            }
            catch
            {
                writeThrew = true;
            }

            await Assert.That(writeThrew).IsTrue()
                .Because("the reactive preference write surfaces a write failure rather than swallowing it");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     The fileless (WASM) path persists neither session nor recents — the saves no-op and the loads
    ///     return empty — matching the pre-consolidation in-memory-only behavior. A <c>null</c> config dir
    ///     selects that branch off-browser.
    /// </summary>
    [Test]
    public async Task Fileless_SessionAndRecents_AreNoOps()
    {
        SettingsService svc = new(null); // fileless in-memory path

        svc.SaveSession(new SessionPayload(null, null, null, false, false, "parser"));
        svc.SaveRecents([new RecentFile("/demos/a.dem", null, DateTime.UtcNow)]);

        await Assert.That(svc.LoadSession()).IsNull();
        await Assert.That(svc.LoadRecents().Count).IsEqualTo(0);
    }
}
