#region

using System.Reflection;
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
    /// <summary>
    ///     The stat heat ramp: four accent tiers plus the two in-cell bar tokens. One family, one
    ///     decision. See docs/ui/theme-token-catalog.md.
    /// </summary>
    private static readonly string[] _statRamp =
    [
        "StatPositive", "StatPositiveSoft", "StatNegativeSoft", "StatNegative",
        "StatBarTrack", "StatBarFill"
    ];

    /// <summary>
    ///     The composition bars' categorical palette, added with the category boards. A family of its
    ///     own — "which thing", not "how good" — and all-or-nothing for the ramp's reason: a legend
    ///     drawing three retinted slots beside three inherited ones reads as a rendering fault rather
    ///     than as a key. Guarded separately rather than folded into the ramp, because a theme is
    ///     entitled to retint one family and inherit the other; what it may not do is half-retint
    ///     either. See section 15 of docs/ui/stats-components.md.
    /// </summary>
    private static readonly string[] _statSlots =
    [
        "StatSlot0", "StatSlot1", "StatSlot2", "StatSlot3", "StatSlot4", "StatSlot5"
    ];

    /// <summary>The <c>Stat*</c> families, each judged on its own.</summary>
    private static readonly (string Name, string[] Tokens)[] _statFamilies =
    [
        ("the stat heat ramp", _statRamp),
        ("the composition slot palette", _statSlots)
    ];

    /// <summary>
    ///     The embedded built-in theme files, parsed the same way <see cref="ThemeRegistry" /> parses
    ///     them. Reads the assembly resources rather than the repository, so the test travels with the
    ///     assembly and cannot drift from what actually ships.
    /// </summary>
    private static (string Name, IReadOnlyDictionary<string, Color> Tokens)[] BuiltInThemeFiles()
    {
        Assembly asm = typeof(ThemeRegistry).Assembly;
        List<(string, IReadOnlyDictionary<string, Color>)> found = [];
        foreach (string resource in asm.GetManifestResourceNames()
                     .Where(n => n.Contains(".Themes.", StringComparison.Ordinal)
                                 && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using Stream stream = asm.GetManifestResourceStream(resource)!;
            using StreamReader reader = new(stream);
            if (ThemeJson.TryParse(reader.ReadToEnd(), resource) is { } def)
            {
                found.Add((resource, def.Tokens));
            }
        }

        return found.ToArray();
    }

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

    /// <summary>
    ///     Each <c>Stat*</c> family is one decision, so a theme retints all of a family or none of it.
    ///     <para>
    ///         A HALF-retinted ramp is worse than an un-retinted one. High-Contrast overrode
    ///         <c>StatPositive</c> to a neon green and inherited the default muted teal for
    ///         <c>StatPositiveSoft</c>, which put a vivid strong-good tier directly beside a washed-out
    ///         mild-good tier and read as a rendering fault rather than as a scale. Omitting the family
    ///         entirely is fine; the base palette is coherent on its own.
    ///     </para>
    ///     <para>
    ///         The slot palette is held to the same rule for the same reason, and separately: an omitted
    ///         token is not transparent or black, it falls through variant-to-base inheritance, so this
    ///         is a coherence guard rather than a crash guard and the two families can be answered
    ///         independently.
    ///     </para>
    ///     <para>
    ///         Asserted against the theme FILES rather than against resolved brushes, because the file is
    ///         where the mistake gets made and a resolved value cannot tell an inherited token from one
    ///         that happens to match.
    ///     </para>
    /// </summary>
    [Test]
    public async Task BuiltInThemes_RetintTheWholeStatRamp_OrNoneOfIt()
    {
        foreach ((string name, IReadOnlyDictionary<string, Color> tokens) in BuiltInThemeFiles())
        {
            foreach ((string family, string[] keys) in _statFamilies)
            {
                string[] present = keys.Where(tokens.ContainsKey).ToArray();
                if (present.Length == 0)
                {
                    continue;
                }

                string[] missing = keys.Except(present, StringComparer.Ordinal).ToArray();
                await Assert.That(missing).IsEmpty()
                    .Because($"{name} retints {present.Length} of the {keys.Length} tokens in {family}; "
                             + $"missing: {string.Join(", ", missing)}");
            }
        }
    }

    /// <summary>
    ///     Both shipped alternates carry every <c>Stat*</c> family, so neither renders a scale or a
    ///     legend it did not choose.
    /// </summary>
    [Test]
    public async Task BuiltInThemes_BothCarryEveryStatFamily()
    {
        (string Name, IReadOnlyDictionary<string, Color> Tokens)[] files = BuiltInThemeFiles();

        await Assert.That(files.Length).IsEqualTo(2);
        foreach ((string name, IReadOnlyDictionary<string, Color> tokens) in files)
        {
            foreach ((string _, string[] keys) in _statFamilies)
            {
                foreach (string key in keys)
                {
                    await Assert.That(tokens.ContainsKey(key)).IsTrue().Because($"{name} is missing {key}");
                }
            }
        }
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
