#region

using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     Trusts a staged directory only when its <see cref="ExtensionSignature.FileName" /> verifies
///     against one of <see cref="PublisherKeys.Current" />. The key list is injectable so a test can sign with
///     a key it generates at run time rather than one committed to this repo.
/// </summary>
public sealed class SignedTrustPolicy(IReadOnlyList<string>? publicKeysBase64Spki = null) : ITrustPolicy
{
    private readonly IReadOnlyList<string> _publicKeys = publicKeysBase64Spki ?? PublisherKeys.Current;

    /// <inheritdoc />
    public bool IsTrusted(string directory, ExtensionManifest manifest) => Judge(directory, manifest).Trusted;

    /// <inheritdoc />
    public TrustVerdict Judge(string directory, ExtensionManifest manifest)
    {
        try
        {
            SignatureCheck check = ExtensionSignature.Verify(directory, _publicKeys);
            return check.Verified ? TrustVerdict.Yes : TrustVerdict.No(check.Detail!, check.LogDetail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Must not throw: ITrustPolicy.Judge's contract reads a throw as untrusted anyway.
            return TrustVerdict.No("signature invalid", ex.Message);
        }
    }
}
