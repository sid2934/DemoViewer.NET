#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Styling;
using Avalonia.Threading;
using DemoViewer.NET.Services;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Proves the theme drop-in machinery (docs/ui/theme-system-plan.md): the safe JSON parse, the
///     <see cref="AppPaths.ThemesDirectory" /> scan, and — the load-bearing bit — that
///     re-registering a custom variant's tokens + bouncing <c>RequestedThemeVariant</c> re-resolves a LIVE
///     <c>{DynamicResource}</c>. That bounce is the repaint mechanism an edit-and-reload relies on (a same-variant
///     override edit does not fire <c>ActualThemeVariantChanged</c> on its own).
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ThemeReloadTests
{
    // ── ThemeJson (pure, no UI) ───────────────────────────────────────────────────────────────────
    [Test]
    public async Task ThemeJson_ParsesIdNameBaseTokens()
    {
        ThemeDefinition? def = ThemeJson.TryParse(
            """{ "id": "egirl", "name": "E-Girl", "base": "dark", "tokens": { "ShellBg": "#0A0008" } }""",
            "test");

        await Assert.That(def).IsNotNull();
        await Assert.That(def!.Id).IsEqualTo("egirl");
        await Assert.That(def.DisplayName).IsEqualTo("E-Girl");
        await Assert.That(def.BaseVariant).IsEqualTo(ThemeVariant.Dark);
        await Assert.That(def.Tokens["ShellBg"]).IsEqualTo(Color.Parse("#0A0008"));
    }

    [Test]
    public async Task ThemeJson_DegradesGracefully()
    {
        // Unparseable → null.
        await Assert.That(ThemeJson.TryParse("{ not json", "test")).IsNull();
        // No id → null (cannot be persisted/resolved).
        await Assert.That(ThemeJson.TryParse("""{ "name": "x" }""", "test")).IsNull();

        // A bad token hex is skipped; the rest of the file still loads. Missing base defaults to dark; missing
        // name defaults to the id.
        ThemeDefinition? def = ThemeJson.TryParse(
            """{ "id": "partial", "tokens": { "ShellBg": "#123456", "PanelBg": "not-a-colour" } }""", "test");
        await Assert.That(def).IsNotNull();
        await Assert.That(def!.DisplayName).IsEqualTo("partial");
        await Assert.That(def.BaseVariant).IsEqualTo(ThemeVariant.Dark);
        await Assert.That(def.Tokens.ContainsKey("ShellBg")).IsTrue();
        await Assert.That(def.Tokens.ContainsKey("PanelBg")).IsFalse();
    }

    // ── Reload scan (filesystem, temp dir via the config-dir override) ─────────────────────────────
    [Test]
    public async Task Reload_LoadsDropIns_AndReflectsDeletion()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-themes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? prior = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            string themesDir = Path.Combine(dir, "themes");
            Directory.CreateDirectory(themesDir);
            string file = Path.Combine(themesDir, "cool.json");
            await File.WriteAllTextAsync(file,
                """{ "id": "cool", "name": "Cool", "base": "light", "tokens": { "ShellBg": "#010203" } }""");

            ThemeRegistry registry = new();
            registry.Reload();

            await Assert.That(registry.Contains("cool")).IsTrue();
            await Assert.That(registry.Themes.Single(t => t.Id == "cool").Source).IsEqualTo(ThemeSource.User);

            // A user file cannot shadow a built-in id.
            await File.WriteAllTextAsync(Path.Combine(themesDir, "hijack.json"),
                """{ "id": "dark", "name": "Fake Dark", "tokens": { "ShellBg": "#FF0000" } }""");
            registry.Reload();
            await Assert.That(registry.Themes.Single(t => t.Id == "dark").DisplayName).IsEqualTo("Dark");

            // Deleting the file removes the theme on the next reload.
            File.Delete(file);
            registry.Reload();
            await Assert.That(registry.Contains("cool")).IsFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prior);
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    // ── The discriminator: re-register + bounce re-resolves a live DynamicResource ─────────────────
    [Test]
    public async Task ReRegister_ThenBounce_ReResolvesLiveResource()
    {
        // All UI mutations run synchronously on the dispatcher thread (no await between them — an awaited
        // assertion resumes off-thread and the next UI call would then be "from an invalid thread"); results are
        // captured into locals and asserted after. Dispatcher.RunJobs flushes any queued resource-change delivery.
        Color? initial = null;
        Color? afterEdit = null;
        await HeadlessSession.RunOnUi(() =>
        {
            Application app = Application.Current!;
            ThemeVariant? original = app.RequestedThemeVariant;
            ThemeRegistry registry = new();
            try
            {
                registry.RegisterCustom("reloadme", "Reload Me", ThemeVariant.Dark,
                    new Dictionary<string, Color>
                    {
                        ["ShellBg"] = Color.Parse("#111111")
                    });
                registry.Install(app);
                app.RequestedThemeVariant = registry.VariantFor("reloadme");

                object? captured = null;
                using IDisposable sub = app.GetResourceObservable("ShellBg")
                    .Subscribe(new AnonymousObserver<object?>(o => captured = o));
                Dispatcher.UIThread.RunJobs();
                initial = (captured as ISolidColorBrush)?.Color; // resolved against the custom variant

                // Simulate an edit + reload: same id, new colour.
                registry.RegisterCustom("reloadme", "Reload Me", ThemeVariant.Dark,
                    new Dictionary<string, Color>
                    {
                        ["ShellBg"] = Color.Parse("#222222")
                    });

                // The repaint mechanism: bounce the active variant so the live resource re-resolves.
                app.RequestedThemeVariant = ThemeVariant.Default;
                app.RequestedThemeVariant = registry.VariantFor("reloadme");
                Dispatcher.UIThread.RunJobs();
                afterEdit = (captured as ISolidColorBrush)?.Color;
            }
            finally
            {
                app.RequestedThemeVariant = original;
                registry.Uninstall(app);
            }

            return Task.CompletedTask;
        });

        await Assert.That(initial).IsEqualTo(Color.Parse("#111111"));
        await Assert.That(afterEdit).IsEqualTo(Color.Parse("#222222"));
    }
}
