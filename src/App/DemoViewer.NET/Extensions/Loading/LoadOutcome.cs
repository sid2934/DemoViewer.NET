#region

using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>Why a staged extension directory was not loaded. One value per check, in the order the checks run.</summary>
public enum LoadFailure
{
    /// <summary>The directory, its manifest or its assembly resolves outside the extensions root, or through a reparse point.</summary>
    PathEscapes,

    /// <summary>The <c>extension.json</c> is missing, malformed, or fails validation.</summary>
    ManifestInvalid,

    /// <summary>The directory's <c>&lt;id&gt;/&lt;version&gt;</c> is not the manifest's id and version.</summary>
    FolderMismatch,

    /// <summary>The shipped copy's version could not be read, so no staged copy can be shown to be newer.</summary>
    ShippedUnknown,

    /// <summary>The staged version is not above the shipped one.</summary>
    NotNewer,

    /// <summary><see cref="PackCompatibility.Check" /> refused the manifest.</summary>
    Incompatible,

    /// <summary>The <see cref="ITrustPolicy" /> refused the directory.</summary>
    Untrusted,

    /// <summary>The assembly file could not be loaded (missing, corrupt, wrong format).</summary>
    AssemblyLoadFailed,

    /// <summary>The manifest's <c>entryType</c> is not in the assembly.</summary>
    EntryTypeMissing,

    /// <summary>The entry type does not implement <see cref="IExtension" /> or could not be constructed.</summary>
    NotAPack,

    /// <summary>The loaded pack's id or embedded manifest version is not the on-disk manifest's.</summary>
    IdentityMismatch,

    /// <summary>The staged assembly references an assembly the app ships at a different version.</summary>
    ReferenceMismatch,

    /// <summary>Reading the pack's contract members or running its <c>Register</c> on a scratch container threw.</summary>
    ProbeFailed,

    /// <summary>A third-party copy claims an id reserved for extensions this app ships.</summary>
    ReservedId,

    /// <summary>An unverified third-party copy while the user has not allowed unverified extensions.</summary>
    Unverified,

    /// <summary>A third-party copy whose features, job kinds or commands collide with ones already loaded.</summary>
    Conflicts,

    /// <summary>The loader itself failed; the shipped copy was used.</summary>
    LoaderFailed,

    /// <summary>A third-party copy references the app's own assembly instead of building against the SDK alone.</summary>
    ReferencesApp
}

/// <summary>
///     One staged directory the loader did not load, with the reason. Recorded on the pack's
///     <see cref="PackStatus.Rejected" /> so Settings can say why an update did not take, and logged once
///     when the app's logging comes up. Never thrown.
/// </summary>
/// <param name="Directory">The staged directory, as a full path.</param>
/// <param name="Manifest">Its manifest when it parsed, else null.</param>
/// <param name="Failure">The check that failed.</param>
/// <param name="Detail">
///     The specifics in user terms: what was required, what was found. Carries at most a bare file name,
///     never a path or an exception message, since Settings shows it.
/// </param>
/// <param name="LogDetail">The exception message behind <paramref name="Detail" />, for the log only; null when there is none.</param>
/// <param name="External">A third-party extension rather than an update of one this app ships.</param>
public sealed record LoadOutcome(
    string Directory, ExtensionManifest? Manifest, LoadFailure Failure, string Detail, string? LogDetail = null, bool External = false)
{
    /// <summary>The staged version, when the manifest parsed.</summary>
    public SemVersion? Version => Manifest?.Version;

    /// <summary>
    ///     The line Settings shows under the extension's row: "Update 1.0.2 was not loaded: ..." when the
    ///     version is known, else "An update in '1.0.2' was not loaded: ..." naming the directory.
    /// </summary>
    public string UserMessage => Manifest is null
        ? $"An update in '{Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory))}' was not loaded: {Detail}"
        : External
            ? $"{Manifest.Name} {Manifest.Version} was not loaded: {Detail}"
            : $"Update {Manifest.Version} was not loaded: {Detail}";
}
