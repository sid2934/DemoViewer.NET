#region

using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     Whether a staged extension directory may be loaded. The loader asks once per candidate, after the
///     manifest parsed and the compatibility check passed and before the assembly is touched. Item 35
///     implements the signature check; until then <see cref="TrustPolicy.Default" /> trusts nothing on
///     disk unless the developer opt-in is set.
/// </summary>
public interface ITrustPolicy
{
    /// <summary>
    ///     True when the assembly under <paramref name="directory" /> may load. Must not throw; a
    ///     policy that does reads as "untrusted" for that candidate.
    /// </summary>
    /// <param name="directory">The staged version directory.</param>
    /// <param name="manifest">Its manifest, already parsed and matched to the directory.</param>
    bool IsTrusted(string directory, ExtensionManifest manifest);
}

/// <summary>The policies this build knows.</summary>
public static class TrustPolicy
{
    /// <summary>
    ///     Developer-only opt-in: when this variable is <c>1</c> an unsigned staged copy loads. For
    ///     building and testing the loader before item 35's signature check exists, and for running an
    ///     extension build from a local checkout afterwards. Never set by the app or the installer.
    /// </summary>
    public const string TrustUnsignedEnvVar = "DEMOVIEWER_EXTENSIONS_TRUST_UNSIGNED";

    /// <summary>Trusts nothing on disk.</summary>
    public static ITrustPolicy Nothing { get; } = new NothingPolicy();

    /// <summary>
    ///     <see cref="Nothing" /> unless <see cref="TrustUnsignedEnvVar" /> is <c>1</c> in this process's
    ///     environment, in which case every staged copy is trusted.
    /// </summary>
    public static ITrustPolicy Default { get; } = UnsignedOptIn(static name => Environment.GetEnvironmentVariable(name));

    /// <summary>
    ///     The <see cref="Default" /> rule over an explicit environment read, so a test can exercise both
    ///     answers without touching the process environment.
    /// </summary>
    public static ITrustPolicy UnsignedOptIn(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return new OptInPolicy(getEnvironmentVariable);
    }

    private sealed class NothingPolicy : ITrustPolicy
    {
        public bool IsTrusted(string directory, ExtensionManifest manifest) => false;
    }

    private sealed class OptInPolicy(Func<string, string?> getEnvironmentVariable) : ITrustPolicy
    {
        public bool IsTrusted(string directory, ExtensionManifest manifest) =>
            string.Equals(getEnvironmentVariable(TrustUnsignedEnvVar)?.Trim(), "1", StringComparison.Ordinal);
    }
}
