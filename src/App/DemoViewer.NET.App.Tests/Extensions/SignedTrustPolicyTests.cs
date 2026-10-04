#region

using System.Security.Cryptography;
using DemoViewer.NET.Extensions.Loading;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="SignedTrustPolicy" /> and <see cref="TrustPolicy.SignedOrOptIn" /> (strat-book-plugin.md
///     §7.9): a missing signature is untrusted with a reason, a valid one is trusted, and the combined
///     policy falls back to the developer opt-in only when signing does not verify.
/// </summary>
public class SignedTrustPolicyTests
{
    [Test]
    public async Task Judge_MissingSignature_IsUntrustedWithTheGenericReason()
    {
        string dir = NewTree();
        try
        {
            SignedTrustPolicy policy = new(["anything"]);
            TrustVerdict verdict = policy.Judge(dir, FakeManifests.For("net.demoviewer.pack.fake"));

            using (Assert.Multiple())
            {
                await Assert.That(verdict.Trusted).IsFalse();
                await Assert.That(verdict.Reason).IsEqualTo("the copy is not signed by this app's publisher");
                await Assert.That(policy.IsTrusted(dir, FakeManifests.For("net.demoviewer.pack.fake"))).IsFalse();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task Judge_ValidSignature_IsTrusted()
    {
        string dir = NewTree();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        try
        {
            File.WriteAllText(Path.Combine(dir, ExtensionSignature.FileName), ExtensionSignature.Sign(dir, key));
            SignedTrustPolicy policy = new([publicKey]);

            TrustVerdict verdict = policy.Judge(dir, FakeManifests.For("net.demoviewer.pack.fake"));

            using (Assert.Multiple())
            {
                await Assert.That(verdict.Trusted).IsTrue();
                await Assert.That(verdict.Reason).IsNull();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Proves Default is wired to the real PublisherKeys.Current, not just to some list: a directory
    // signed with a key generated here, which is certainly not in PublisherKeys.Current, must still
    // come back untrusted through Default itself. Skipped under the developer opt-in, which bypasses
    // this regardless of the signature (that is its own test below).
    [Test]
    public async Task Default_DoesNotTrustADirectorySignedWithAKeyOutsidePublisherKeysCurrent()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(TrustPolicy.TrustUnsignedEnvVar)?.Trim(), "1", StringComparison.Ordinal))
        {
            throw new TUnit.Core.Exceptions.SkipTestException(
                $"{TrustPolicy.TrustUnsignedEnvVar} is set in this process; Default would bypass regardless of the signature");
        }

        string dir = NewTree();
        using ECDsa notThePublisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            File.WriteAllText(Path.Combine(dir, ExtensionSignature.FileName), ExtensionSignature.Sign(dir, notThePublisher));

            TrustVerdict verdict = TrustPolicy.Default.Judge(dir, FakeManifests.For("net.demoviewer.pack.fake"));

            using (Assert.Multiple())
            {
                await Assert.That(verdict.Trusted).IsFalse();
                await Assert.That(verdict.Reason).IsEqualTo("the copy is not signed by this app's publisher");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task SignedOrOptIn_TrustsASignedDirectory_WithoutTheEnvVar()
    {
        string dir = NewTree();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        try
        {
            File.WriteAllText(Path.Combine(dir, ExtensionSignature.FileName), ExtensionSignature.Sign(dir, key));
            ITrustPolicy policy = TrustPolicy.SignedOrOptIn([publicKey], static _ => null);

            TrustVerdict verdict = policy.Judge(dir, FakeManifests.For("net.demoviewer.pack.fake"));

            await Assert.That(verdict.Trusted).IsTrue();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task SignedOrOptIn_TrustsAnUnsignedDirectory_OnlyWithTheEnvVar_AndReportsTheSigningReasonOtherwise()
    {
        string dir = NewTree();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        try
        {
            ITrustPolicy withoutOptIn = TrustPolicy.SignedOrOptIn([publicKey], static _ => null);
            ITrustPolicy withOptIn = TrustPolicy.SignedOrOptIn([publicKey], name => name == TrustPolicy.TrustUnsignedEnvVar ? "1" : null);

            TrustVerdict refused = withoutOptIn.Judge(dir, FakeManifests.For("net.demoviewer.pack.fake"));
            TrustVerdict allowed = withOptIn.Judge(dir, FakeManifests.For("net.demoviewer.pack.fake"));

            using (Assert.Multiple())
            {
                await Assert.That(refused.Trusted).IsFalse();
                await Assert.That(refused.Reason).IsEqualTo("the copy is not signed by this app's publisher");
                await Assert.That(allowed.Trusted).IsTrue();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task SignedOrOptIn_WithTheEnvVar_BypassesEvenAWrongSignature()
    {
        // The opt-in does not inspect the directory; it trusts unconditionally when set, signed,
        // wrongly signed, or not. It is a developer bypass, not a narrower "unsigned only" rule.
        string dir = NewTree();
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string otherPublicKey = Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo());
        try
        {
            File.WriteAllText(Path.Combine(dir, ExtensionSignature.FileName), ExtensionSignature.Sign(dir, signingKey));
            ITrustPolicy withOptIn = TrustPolicy.SignedOrOptIn([otherPublicKey], static _ => "1");

            TrustVerdict verdict = withOptIn.Judge(dir, FakeManifests.For("net.demoviewer.pack.fake"));

            await Assert.That(verdict.Trusted).IsTrue().Because("the opt-in still trusts it, just not on the strength of the signature");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string NewTree()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvtrust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "extension.json"), "{\"id\":\"x\"}");
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort
        }
    }
}
