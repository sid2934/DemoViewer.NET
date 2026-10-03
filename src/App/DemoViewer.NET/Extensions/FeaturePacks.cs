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
    private static readonly Lock Gate = new();
    private static IReadOnlyList<IFeaturePack> _default = [];
    private static bool _read;

    /// <summary>Every first-party pack the head declared; empty until <see cref="Configure" /> runs.</summary>
    public static IReadOnlyList<IFeaturePack> Default
    {
        get
        {
            lock (Gate)
            {
                _read = true;
                return _default;
            }
        }
    }

    /// <summary>
    ///     Declares the compiled-in packs. Must run before anything reads <see cref="Default" />: the
    ///     static registries build from it once, so a later change would leave them disagreeing with the
    ///     container.
    /// </summary>
    /// <param name="packs">The packs, in composition order.</param>
    /// <exception cref="InvalidOperationException"><see cref="Default" /> was already read.</exception>
    public static void Configure(IReadOnlyList<IFeaturePack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        lock (Gate)
        {
            if (_read)
            {
                throw new InvalidOperationException(
                    "FeaturePacks.Configure must run before the first read of FeaturePacks.Default (the registries build from it once).");
            }

            _default = [.. packs];
        }
    }
}
