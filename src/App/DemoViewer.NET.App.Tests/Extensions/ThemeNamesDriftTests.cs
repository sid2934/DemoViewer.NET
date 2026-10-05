#region

using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The UI kit publishes the palette keys and shared style classes as constants. These compare the constants
///     against the compiled palette and style files the app loads, so a key or class the generator missed, or one the app
///     renamed, fails here instead of resolving to nothing in an extension's view.
/// </summary>
[NotInParallel]
public partial class ThemeNamesDriftTests
{
    private static readonly string[] _sharedStyleFiles = ["Primitives", "Cards", "Tables", "Chrome"];

    [GeneratedRegex(@"\.([A-Za-z_][A-Za-z0-9_-]*)")]
    private static partial Regex ClassName();

    // The compiled resources the app's App.axaml includes, loaded the way the app loads them.
    private static object Load(string file) => AvaloniaXamlLoader.Load(new Uri($"avares://DemoViewer.NET/Styles/{file}.axaml"));

    private static IEnumerable<IStyle> Walk(IStyle style)
    {
        yield return style;
        IEnumerable<IStyle> children = style switch
        {
            StyleInclude include => [include.Loaded],
            Styles styles => styles,
            StyleBase nested => nested.Children,
            _ => []
        };

        foreach (IStyle child in children)
        {
            foreach (IStyle descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    [Test]
    public async Task EveryToken_IsAKeyOfBothThemes_AndEveryKeyIsAToken()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            IResourceDictionary palette = (IResourceDictionary)Load("DarkPalette");
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                IResourceDictionary theme = (IResourceDictionary)palette.ThemeDictionaries[variant];
                string[] keys = [.. theme.Keys.Cast<string>()];

                await Assert.That(keys).IsEquivalentTo(ThemeTokens.All).Because($"the {variant} palette");
            }
        });
    }

    [Test]
    public async Task EveryToken_ResolvesToABrush_InEachBuiltInVariant()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            string[] unresolved =
            [
                .. from variant in new[] { ThemeVariant.Dark, ThemeVariant.Light }
                from token in ThemeTokens.All
                where !(Application.Current!.TryGetResource(token, variant, out object? value) && value is IBrush)
                select $"{token} ({variant})"
            ];

            await Assert.That(unresolved).IsEmpty();
        });
    }

    [Test]
    public async Task ThemeColors_ResolvesAToken_PerVariant()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            Color fallback = Colors.Magenta;

            Color dark = ThemeColors.Get(ThemeTokens.ShellBg, ThemeVariant.Dark, fallback);
            Color light = ThemeColors.Get(ThemeTokens.ShellBg, ThemeVariant.Light, fallback);

            await Assert.That(dark).IsNotEqualTo(fallback);
            await Assert.That(light).IsNotEqualTo(fallback);
            await Assert.That(dark).IsNotEqualTo(light);
            await Assert.That(ThemeColors.Get("NoSuchToken", ThemeVariant.Dark, fallback)).IsEqualTo(fallback);
        });
    }

    [Test]
    public async Task StyleClasses_AreExactlyTheClassesTheSharedStylesSelectOn()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            HashSet<string> loaded = new(StringComparer.Ordinal);
            foreach (string file in _sharedStyleFiles)
            {
                foreach (Style rule in Walk((IStyle)Load(file)).OfType<Style>())
                {
                    foreach (Match match in ClassName().Matches(rule.Selector?.ToString() ?? ""))
                    {
                        loaded.Add(match.Groups[1].Value);
                    }
                }
            }

            await Assert.That(loaded).IsEquivalentTo(StyleClasses.All);
        });
    }
}
