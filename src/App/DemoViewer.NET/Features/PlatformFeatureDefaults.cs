#region

using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.Features;

/// <summary>
///     Where a feature's default depends on the platform rather than the user category. The ONE place that
///     question is answered, the way <see cref="ShellModuleFeatureGate.DesktopOnlyIds" /> is the one browser
///     AND site: a second answer would drift from this one.
///     <para>
///         A <b>default</b>, not a gate. The id stays in the catalog, Settings still lists it, and a user who
///         switches it on keeps it on, because the override is read before this is consulted. That is the
///         difference from <c>DesktopOnlyIds</c>, which refuses whatever the override says because the
///         feature cannot physically work there.
///     </para>
/// </summary>
internal static class PlatformFeatureDefaults
{
    /// <summary>
    ///     Ids that default OFF on macOS. <c>tab.highlights</c> is the Reels dashboard: the owner's call is
    ///     that macOS is not a supported target for it, and an empty tab is worse than an absent one. Linux
    ///     is deliberately NOT here, so it keeps the catalog default; whether the launch path actually works
    ///     there is a separate question and flipping this for Linux before answering it would reproduce the
    ///     empty tab on Linux instead.
    /// </summary>
    internal static IReadOnlySet<string> MacOsOffIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "tab.highlights" };

    /// <summary>
    ///     The catalog default for <paramref name="category" />, less any platform rule above.
    /// </summary>
    /// <param name="id">The feature id. A persisted key, so it is matched exactly.</param>
    /// <param name="defaults">The descriptor's category map.</param>
    /// <param name="category">The user's category.</param>
    /// <param name="isMacOs">
    ///     Test seam. <c>OperatingSystem.IsMacOS()</c> is an intrinsic the JIT folds to a constant, so a
    ///     rule keyed on it can only be exercised on one of the two platforms without this.
    /// </param>
    internal static bool DefaultFor(string id, IReadOnlyDictionary<UserCategory, bool> defaults,
        UserCategory category, bool? isMacOs = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (!defaults.TryGetValue(category, out bool byDefault) || !byDefault)
        {
            return false;
        }

        return !(MacOsOffIds.Contains(id) && (isMacOs ?? OperatingSystem.IsMacOS()));
    }
}
