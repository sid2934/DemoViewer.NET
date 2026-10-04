#region

using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     Whether a staged extension directory may be loaded. The loader asks once per candidate, after the
///     manifest parsed and the compatibility check passed and before the assembly is touched.
///     <see cref="TrustPolicy.Default" /> checks <see cref="SignedTrustPolicy" /> first and the
///     developer opt-in second.
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

    /// <summary>
    ///     Like <see cref="IsTrusted" /> but with a reason when the answer is false. Default-implemented
    ///     (an additive, minor member) so a policy that predates this still compiles and
    ///     reports the one generic reason every policy gave before this; <see cref="SignedTrustPolicy" />
    ///     overrides it with a specific one. The loader calls this, not <see cref="IsTrusted" /> directly.
    /// </summary>
    TrustVerdict Judge(string directory, ExtensionManifest manifest) =>
        IsTrusted(directory, manifest) ? TrustVerdict.Yes : TrustVerdict.No("the copy is not signed by this app's publisher");
}

/// <summary>What an <see cref="ITrustPolicy" /> decided, and why when it refused.</summary>
/// <param name="Trusted">True when the directory may load.</param>
/// <param name="Reason">The reason in user terms when <paramref name="Trusted" /> is false; null when true.</param>
/// <param name="LogDetail">A more specific reason for the log only, or null when there is none.</param>
public sealed record TrustVerdict(bool Trusted, string? Reason, string? LogDetail = null)
{
    /// <summary>The directory may load.</summary>
    public static TrustVerdict Yes { get; } = new(true, null);

    /// <summary>The directory may not load, with the reason Settings and the log show.</summary>
    public static TrustVerdict No(string reason, string? logDetail = null) => new(false, reason, logDetail);
}

/// <summary>The policies this build knows.</summary>
public static class TrustPolicy
{
    /// <summary>
    ///     Developer-only opt-in: when this variable is <c>1</c> an unsigned staged copy loads. For
    ///     building and testing the loader, and for running an extension built from a local checkout
    ///     without signing it. Never set by the app or the installer.
    /// </summary>
    public const string TrustUnsignedEnvVar = "DEMOVIEWER_EXTENSIONS_TRUST_UNSIGNED";

    /// <summary>Trusts nothing on disk.</summary>
    public static ITrustPolicy Nothing { get; } = new NothingPolicy();

    /// <summary>
    ///     A directory signed by one of <see cref="PublisherKeys.Current" />, or, failing that,
    ///     <see cref="TrustUnsignedEnvVar" /> set to <c>1</c> in this process's environment. Neither key
    ///     parsing nor signature checking happens until a candidate is actually judged (both policies
    ///     underneath are lazy), so a bad embedded key cannot fail at process start.
    /// </summary>
    public static ITrustPolicy Default { get; } = SignedOrOptIn(PublisherKeys.Current, static name => Environment.GetEnvironmentVariable(name));

    /// <summary>
    ///     The developer opt-in alone, over an explicit environment read, so a test can exercise both
    ///     answers without touching the process environment.
    /// </summary>
    public static ITrustPolicy UnsignedOptIn(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return new OptInPolicy(getEnvironmentVariable);
    }

    /// <summary>
    ///     <see cref="SignedTrustPolicy" /> over <paramref name="publisherKeysBase64Spki" />, falling back
    ///     to <see cref="UnsignedOptIn" /> when nothing in that list verifies the directory. On a full
    ///     refusal the reported reason is the signing failure's, not the opt-in's, since that is the one a
    ///     user or the log can act on. Exposed so a test can build the same shape <see cref="Default" /> is
    ///     with an ephemeral key instead of <see cref="PublisherKeys.Current" />.
    /// </summary>
    public static ITrustPolicy SignedOrOptIn(IReadOnlyList<string> publisherKeysBase64Spki, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(publisherKeysBase64Spki);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return new SignedOrOptInPolicy(new SignedTrustPolicy(publisherKeysBase64Spki), UnsignedOptIn(getEnvironmentVariable));
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

    private sealed class SignedOrOptInPolicy(ITrustPolicy signed, ITrustPolicy optIn) : ITrustPolicy
    {
        public bool IsTrusted(string directory, ExtensionManifest manifest) => Judge(directory, manifest).Trusted;

        public TrustVerdict Judge(string directory, ExtensionManifest manifest)
        {
            TrustVerdict bySignature = SafeJudge(signed, directory, manifest);
            if (bySignature.Trusted)
            {
                return bySignature;
            }

            TrustVerdict byOptIn = SafeJudge(optIn, directory, manifest);
            return byOptIn.Trusted ? byOptIn : bySignature;
        }

        private static TrustVerdict SafeJudge(ITrustPolicy policy, string directory, ExtensionManifest manifest)
        {
            try
            {
                return policy.Judge(directory, manifest);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return TrustVerdict.No("the trust check failed", ex.Message);
            }
        }
    }
}
