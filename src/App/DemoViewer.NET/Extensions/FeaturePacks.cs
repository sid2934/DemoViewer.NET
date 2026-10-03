namespace DemoViewer.NET.Extensions;

/// <summary>
///     The packs compiled into this build, in composition order. The app assembly references no pack
///     (strat-book-plugin.md §13), so the head that does (Desktop, Browser, the test and capture hosts)
///     declares the list through <see cref="Configure" /> before Avalonia starts; the composition root,
///     <see cref="Features.FeatureCatalog" />, <see cref="JobKindRegistry" /> and <see cref="CommandRegistry" />
///     all read <see cref="Default" />.
/// </summary>
public static class FeaturePacks
{
    // The heads configure in Main, before Avalonia starts. The XAML previewer bypasses Main and calls the
    // head's BuildAvaloniaApp directly, so that method calls ConfigureIfUnset with the same list.
    private static readonly FrozenList<IFeaturePack> List = new();

    /// <summary>Every first-party pack the head declared; empty until <see cref="Configure" /> runs.</summary>
    public static IReadOnlyList<IFeaturePack> Default => List.Value;

    /// <summary>
    ///     Declares the compiled-in packs, once. Must run before anything reads <see cref="Default" />:
    ///     the static registries build from it once, so a later change would leave them disagreeing with
    ///     the container.
    /// </summary>
    /// <param name="packs">The packs, in composition order.</param>
    /// <exception cref="InvalidOperationException">Already configured, or <see cref="Default" /> was already read.</exception>
    public static void Configure(IReadOnlyList<IFeaturePack> packs) => List.Set(packs);

    /// <summary>
    ///     <see cref="Configure" /> for a path that cannot know whether Main ran (the XAML previewer's
    ///     call into a head's <c>BuildAvaloniaApp</c>); a no-op once configured.
    /// </summary>
    /// <param name="packs">The packs, in composition order.</param>
    /// <returns>True when this call configured the list.</returns>
    public static bool ConfigureIfUnset(IReadOnlyList<IFeaturePack> packs) => List.SetIfUnset(packs);
}
