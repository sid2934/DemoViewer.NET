#region

using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The packs compiled into this build, in composition order, each judged against
///     <see cref="ExtensionHost.Current" /> when the head declares the list. The app assembly references
///     no pack, so the head that does (Desktop, Browser, the test and capture
///     hosts) declares the list through <see cref="Configure(IReadOnlyList{IExtension})" /> before Avalonia starts. The composition
///     root, <see cref="Features.FeatureCatalog" />, <see cref="JobKindRegistry" />,
///     <see cref="CommandRegistry" /> and the <c>ViewLocator</c> all read <see cref="Compatible" />, so a
///     pack that failed the check never registers a service, never puts a descriptor in the catalog (the
///     gate then reads its id as unknown and resolves it off) and never resolves a view; Settings reads
///     <see cref="Statuses" /> to say why.
/// </summary>
public static class FeaturePacks
{
    // The heads configure in Main, before Avalonia starts. The XAML previewer bypasses Main and calls the
    // head's BuildAvaloniaApp directly, so that method calls ConfigureIfUnset with the same list. The
    // check runs inside Set, so no reader can see a pack before its verdict exists.
    private static readonly FrozenList<PackStatus> List = new();

    private static readonly Lazy<IReadOnlyList<IExtension>> _default =
        new(() => [.. List.Value.Select(s => s.Pack)]);

    private static readonly Lazy<IReadOnlyList<IExtension>> _compatible =
        new(() => [.. List.Value.Where(s => s.IsCompatible).Select(s => s.Pack)]);

    /// <summary>Every pack the head declared, compatible or not; empty until <see cref="Configure(IReadOnlyList{IExtension})" /> runs.</summary>
    public static IReadOnlyList<IExtension> Default => _default.Value;

    /// <summary>The declared packs that passed <see cref="PackCompatibility.Check" />; what the app composes.</summary>
    public static IReadOnlyList<IExtension> Compatible => _compatible.Value;

    /// <summary>One verdict per declared pack, in declaration order.</summary>
    public static IReadOnlyList<PackStatus> Statuses => List.Value;

    /// <summary>
    ///     Declares the compiled-in packs, once, and judges each against <see cref="ExtensionHost.Current" />.
    ///     Must run before anything reads <see cref="Default" />, <see cref="Compatible" /> or
    ///     <see cref="Statuses" />: the static registries build from them once, so a later change would leave
    ///     them disagreeing with the container.
    /// </summary>
    /// <param name="packs">The packs, in composition order.</param>
    /// <exception cref="InvalidOperationException">Already configured, or the list was already read.</exception>
    public static void Configure(IReadOnlyList<IExtension> packs) => List.Set(PackStatus.Evaluate(packs, ExtensionHost.Current));

    /// <summary>
    ///     <see cref="Configure(IReadOnlyList{IExtension})" /> with the verdicts already made: what the
    ///     Desktop head passes from <see cref="Loading.ExtensionLoader.Resolve" />, whose statuses carry
    ///     each pack's source and the staged candidates it rejected. Its own name rather than an overload
    ///     so <c>Configure([])</c> stays unambiguous.
    /// </summary>
    /// <param name="statuses">One per pack, in composition order, judged against <see cref="ExtensionHost.Current" />.</param>
    /// <exception cref="InvalidOperationException">Already configured, or the list was already read.</exception>
    public static void ConfigureResolved(IReadOnlyList<PackStatus> statuses) => List.Set(statuses);

    /// <summary>
    ///     <see cref="ConfigureResolved(IReadOnlyList{PackStatus})" /> plus the third-party copies the loader
    ///     refused, which no status carries because none of them loaded.
    /// </summary>
    public static void ConfigureResolved(IReadOnlyList<PackStatus> statuses, IReadOnlyList<LoadOutcome> externalRejected)
    {
        ArgumentNullException.ThrowIfNull(externalRejected);
        List.Set(statuses);
        _externalRejected = externalRejected;
    }

    /// <summary>Third-party copies under the extensions folder that did not load, with the reason.</summary>
    public static IReadOnlyList<LoadOutcome> ExternalRejected => _externalRejected;

    private static volatile IReadOnlyList<LoadOutcome> _externalRejected = [];

    /// <summary>
    ///     <see cref="Configure(IReadOnlyList{IExtension})" /> for a path that cannot know whether Main
    ///     ran (the XAML previewer's call into a head's <c>BuildAvaloniaApp</c>); a no-op once configured.
    /// </summary>
    /// <param name="packs">The packs, in composition order.</param>
    /// <returns>True when this call configured the list.</returns>
    public static bool ConfigureIfUnset(IReadOnlyList<IExtension> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        return !List.IsSet && List.SetIfUnset(PackStatus.Evaluate(packs, ExtensionHost.Current));
    }

    /// <summary>
    ///     <see cref="ConfigureIfUnset(IReadOnlyList{IExtension})" /> with the packs behind a factory
    ///     that runs only when the list is still unset. A head whose Main may have loaded a staged copy of
    ///     a pack uses this so its previewer path never constructs the shipped copy beside it.
    /// </summary>
    /// <param name="packs">Builds the packs, in composition order.</param>
    /// <returns>True when this call configured the list.</returns>
    public static bool ConfigureIfUnset(Func<IReadOnlyList<IExtension>> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        return !List.IsSet && List.SetIfUnset(PackStatus.Evaluate(packs(), ExtensionHost.Current));
    }
}
