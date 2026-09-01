#region

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Proves the central theme system's load-bearing Avalonia behaviour: a CUSTOM <see cref="ThemeVariant" />
///     registered by <see cref="ThemeRegistry" /> resolves its own token overrides, and inherits every OMITTED
///     token from its base (Dark/Light) palette in <c>DarkPalette.axaml</c>. If this holds, a theme is pure data,
///     a base + a set of overrides, and needs no per-file changes anywhere.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ThemeRegistryTests
{
    [Test]
    public async Task CustomVariant_OverrideWins_AndOmittedTokensInheritBase()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ThemeRegistry registry = new();
            try
            {
                registry.RegisterCustom(
                    "test-custom", "Test Custom", ThemeVariant.Dark,
                    new Dictionary<string, Color>
                    {
                        ["ShellBg"] = Color.Parse("#FF00FF")
                    });
                registry.Install(Application.Current!);

                ThemeVariant v = registry.VariantFor("test-custom");

                // (a) an overridden token resolves to the custom value
                Application.Current!.TryGetResource("ShellBg", v, out object? shell);
                await Assert.That((shell as ISolidColorBrush)?.Color).IsEqualTo(Color.Parse("#FF00FF"));

                // (b) an OMITTED token inherits the Dark base palette (PanelBg dark = #0C0C1A)
                Application.Current.TryGetResource("PanelBg", v, out object? panel);
                await Assert.That((panel as ISolidColorBrush)?.Color).IsEqualTo(Color.Parse("#0C0C1A"));

                // (c) a code-token (2D canvas bg) also inherits, proving the whole namespace is reachable
                Application.Current.TryGetResource("Pb2dCanvasBg", v, out object? canvas);
                await Assert.That((canvas as ISolidColorBrush)?.Color).IsEqualTo(Color.Parse("#15181C"));
            }
            finally
            {
                registry.Uninstall(Application.Current!);
            }
        });
    }

    [Test]
    public async Task BuiltIns_AreRegistered_AndVariantForResolves()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ThemeRegistry registry = new();
            List<string> ids = registry.Themes.Select(t => t.Id).ToList();

            await Assert.That(ids).Contains("dark");
            await Assert.That(ids).Contains("light");
            await Assert.That(ids).Contains("system");
            await Assert.That(registry.VariantFor("light")).IsEqualTo(ThemeVariant.Light);
            await Assert.That(registry.VariantFor("unknown-id")).IsEqualTo(ThemeVariant.Default);
        });
    }

    // T4: High-Contrast + E-Girl ship as EMBEDDED built-in themes (Themes/*.json), loaded via the same JSON
    // parser as user drop-ins. Each is a BuiltIn-source custom variant whose overrides resolve and whose omitted
    // tokens inherit its base, the proof that a new built-in theme needs zero per-file changes.
    [Test]
    public async Task BuiltInCustomThemes_HighContrastAndEGirl_AreRegistered_AndResolve()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ThemeRegistry registry = new();

            Theme hc = registry.Themes.Single(t => t.Id == "high-contrast");
            Theme eg = registry.Themes.Single(t => t.Id == "egirl");
            await Assert.That(hc.Source).IsEqualTo(ThemeSource.BuiltIn);
            await Assert.That(eg.Source).IsEqualTo(ThemeSource.BuiltIn);

            try
            {
                registry.Install(Application.Current!);

                // High-Contrast overrides ShellBg to pure black; E-Girl to its near-black magenta.
                Application.Current!.TryGetResource("ShellBg", hc.Variant, out object? hcShell);
                await Assert.That((hcShell as ISolidColorBrush)?.Color).IsEqualTo(Color.Parse("#000000"));
                Application.Current.TryGetResource("ShellBg", eg.Variant, out object? egShell);
                await Assert.That((egShell as ISolidColorBrush)?.Color).IsEqualTo(Color.Parse("#0A0008"));

                // A token neither overrides (DeltaRowBg) inherits the Dark base palette (#25FFC107) in both.
                Application.Current.TryGetResource("DeltaRowBg", hc.Variant, out object? hcDelta);
                await Assert.That((hcDelta as ISolidColorBrush)?.Color).IsEqualTo(Color.Parse("#25FFC107"));
            }
            finally
            {
                registry.Uninstall(Application.Current!);
            }
        });
    }
}
