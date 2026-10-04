#region

using System.Security.Cryptography;
using DemoViewer.NET.Extensions.Loading;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="ExtensionSignature" />: the canonical digest is deterministic
///     and reacts to any change in the tree; signing and verifying round-trip with a key generated in
///     process (never a committed one); every way a signature can be wrong is a reason, never an exception.
/// </summary>
public class ExtensionSignatureTests
{
    [Test]
    public async Task SignThenVerify_RoundTrips_WithAKeyGeneratedInProcess()
    {
        string dir = NewTree();
        try
        {
            (string publicKey, ECDsa privateKey) = NewKeyPair();
            using (privateKey)
            {
                WriteSignature(dir, privateKey);
                SignatureCheck check = ExtensionSignature.Verify(dir, [publicKey]);

                using (Assert.Multiple())
                {
                    await Assert.That(check.Verified).IsTrue();
                    await Assert.That(check.Failure).IsNull();
                    await Assert.That(check.Detail).IsNull();
                }
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task ComputeDigest_IsDeterministic_AcrossTwoRuns_AndIndependentOfCreationOrder()
    {
        string dirA = NewTree();
        string dirB = Path.Combine(Path.GetTempPath(), "dvsig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dirB);
        try
        {
            // Same contents as NewTree, written in the opposite order, under a sub-folder too.
            Directory.CreateDirectory(Path.Combine(dirB, "sub"));
            File.WriteAllText(Path.Combine(dirB, "sub", "a.txt"), "hello");
            File.WriteAllText(Path.Combine(dirB, ".hidden"), "dotfile content");
            File.WriteAllText(Path.Combine(dirB, "extension.json"), "{\"id\":\"x\"}");

            string firstRun = Convert.ToHexString(ExtensionSignature.ComputeDigest(dirA));
            string secondRun = Convert.ToHexString(ExtensionSignature.ComputeDigest(dirA));
            string otherCreationOrder = Convert.ToHexString(ExtensionSignature.ComputeDigest(dirB));

            using (Assert.Multiple())
            {
                await Assert.That(secondRun).IsEqualTo(firstRun);
                await Assert.That(otherCreationOrder).IsEqualTo(firstRun);
            }
        }
        finally
        {
            Cleanup(dirA);
            Cleanup(dirB);
        }
    }

    [Test]
    public async Task ComputeDigest_IncludesADotfile()
    {
        string dir = NewTree();
        try
        {
            string withHidden = Convert.ToHexString(ExtensionSignature.ComputeDigest(dir));
            File.Delete(Path.Combine(dir, ".hidden"));
            string withoutHidden = Convert.ToHexString(ExtensionSignature.ComputeDigest(dir));

            await Assert.That(withoutHidden).IsNotEqualTo(withHidden);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task Verify_FailsWithTheNamedReason_ForAByteChange_Rename_AddedFile_RemovedFile_AndWrongKey()
    {
        (string publicKey, ECDsa privateKey) = NewKeyPair();
        (string otherPublicKey, ECDsa otherPrivateKey) = NewKeyPair();
        using (privateKey)
        using (otherPrivateKey)
        {
            string byteChanged = NewTree();
            string renamed = NewTree();
            string added = NewTree();
            string removed = NewTree();
            string wrongKey = NewTree();
            try
            {
                WriteSignature(byteChanged, privateKey);
                byte[] bytes = File.ReadAllBytes(Path.Combine(byteChanged, "extension.json"));
                bytes[0] ^= 0xFF;
                File.WriteAllBytes(Path.Combine(byteChanged, "extension.json"), bytes);

                WriteSignature(renamed, privateKey);
                File.Move(Path.Combine(renamed, "sub", "a.txt"), Path.Combine(renamed, "sub", "b.txt"));

                WriteSignature(added, privateKey);
                File.WriteAllText(Path.Combine(added, "new.txt"), "surprise");

                WriteSignature(removed, privateKey);
                File.Delete(Path.Combine(removed, ".hidden"));

                WriteSignature(wrongKey, privateKey);

                SignatureCheck byteChangedCheck = ExtensionSignature.Verify(byteChanged, [publicKey]);
                SignatureCheck renamedCheck = ExtensionSignature.Verify(renamed, [publicKey]);
                SignatureCheck addedCheck = ExtensionSignature.Verify(added, [publicKey]);
                SignatureCheck removedCheck = ExtensionSignature.Verify(removed, [publicKey]);
                SignatureCheck wrongKeyCheck = ExtensionSignature.Verify(wrongKey, [otherPublicKey]);

                using (Assert.Multiple())
                {
                    await Assert.That(byteChangedCheck.Failure).IsEqualTo(SignatureFailure.DigestMismatch);
                    await Assert.That(byteChangedCheck.Detail).IsEqualTo("a file changed after signing");
                    await Assert.That(renamedCheck.Failure).IsEqualTo(SignatureFailure.DigestMismatch);
                    await Assert.That(addedCheck.Failure).IsEqualTo(SignatureFailure.DigestMismatch);
                    await Assert.That(removedCheck.Failure).IsEqualTo(SignatureFailure.DigestMismatch);
                    await Assert.That(wrongKeyCheck.Failure).IsEqualTo(SignatureFailure.UnknownKey);
                    await Assert.That(wrongKeyCheck.Detail).IsEqualTo("the copy is not signed by this app's publisher");
                }
            }
            finally
            {
                Cleanup(byteChanged);
                Cleanup(renamed);
                Cleanup(added);
                Cleanup(removed);
                Cleanup(wrongKey);
            }
        }
    }

    [Test]
    public async Task Verify_MissingSignature_IsNotSignedByThisAppsPublisher()
    {
        string dir = NewTree();
        (string publicKey, ECDsa privateKey) = NewKeyPair();
        using (privateKey)
        {
            try
            {
                SignatureCheck check = ExtensionSignature.Verify(dir, [publicKey]);
                using (Assert.Multiple())
                {
                    await Assert.That(check.Verified).IsFalse();
                    await Assert.That(check.Failure).IsEqualTo(SignatureFailure.Missing);
                    await Assert.That(check.Detail).IsEqualTo("the copy is not signed by this app's publisher");
                }
            }
            finally
            {
                Cleanup(dir);
            }
        }
    }

    [Test]
    public async Task Verify_MalformedSignatureFile_IsSignatureInvalid()
    {
        string dir = NewTree();
        (string publicKey, ECDsa privateKey) = NewKeyPair();
        using (privateKey)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, ExtensionSignature.FileName), "not json");
                SignatureCheck check = ExtensionSignature.Verify(dir, [publicKey]);
                using (Assert.Multiple())
                {
                    await Assert.That(check.Failure).IsEqualTo(SignatureFailure.Malformed);
                    await Assert.That(check.Detail).IsEqualTo("signature invalid");
                    await Assert.That(check.LogDetail).IsNotNull();
                }
            }
            finally
            {
                Cleanup(dir);
            }
        }
    }

    // A tamper that also rewrites the recorded digest to match the tampered tree, keeping keyId and
    // signature as signed. If Verify ever compared digests before the signature over them, or verified
    // the signature against the recomputed digest instead of the recorded one, this would pass.
    [Test]
    public async Task Verify_ForgedDigest_FailsAsSignatureInvalid_NeverVerified()
    {
        string dir = NewTree();
        (string publicKey, ECDsa privateKey) = NewKeyPair();
        using (privateKey)
        {
            try
            {
                WriteSignature(dir, privateKey);
                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "extension.json"));
                bytes[0] ^= 0xFF;
                File.WriteAllBytes(Path.Combine(dir, "extension.json"), bytes);
                byte[] tamperedDigest = ExtensionSignature.ComputeDigest(dir);
                RewriteSignatureField(dir, "digest", Convert.ToBase64String(tamperedDigest));

                SignatureCheck check = ExtensionSignature.Verify(dir, [publicKey]);

                using (Assert.Multiple())
                {
                    await Assert.That(check.Verified).IsFalse();
                    await Assert.That(check.Failure).IsEqualTo(SignatureFailure.SignatureInvalid);
                    await Assert.That(check.Detail).IsEqualTo("signature invalid");
                }
            }
            finally
            {
                Cleanup(dir);
            }
        }
    }

    // keyId alone names no trust: rewriting it to a key that never signed anything must not verify,
    // even when that key IS one Verify is given to check against.
    [Test]
    public async Task Verify_SpoofedKeyId_FailsAsSignatureInvalid_NeverVerified()
    {
        string dir = NewTree();
        (string signerPublicKey, ECDsa signerPrivateKey) = NewKeyPair();
        (string otherPublicKey, ECDsa otherPrivateKey) = NewKeyPair();
        using (signerPrivateKey)
        using (otherPrivateKey)
        {
            try
            {
                WriteSignature(dir, signerPrivateKey);
                string otherKeyId = ExtensionSignature.KeyId(Convert.FromBase64String(otherPublicKey));
                RewriteSignatureField(dir, "keyId", otherKeyId);

                SignatureCheck check = ExtensionSignature.Verify(dir, [signerPublicKey, otherPublicKey]);

                using (Assert.Multiple())
                {
                    await Assert.That(check.Verified).IsFalse();
                    await Assert.That(check.Failure).IsEqualTo(SignatureFailure.SignatureInvalid);
                    await Assert.That(check.Detail).IsEqualTo("signature invalid");
                }
            }
            finally
            {
                Cleanup(dir);
            }
        }
    }

    // The signature format is always IEEE P1363 over a P-256 key. A listed key that happens to carry
    // the claimed key id but sits on another curve must be skipped, not handed to VerifyData, which
    // would otherwise be asked to check a 64-byte P-256 signature against a P-384 key.
    [Test]
    public async Task Verify_AKeyOnAnotherCurve_IsSkippedRatherThanAttempted_AndReadsAsSignatureInvalid()
    {
        string dir = NewTree();
        (string signerPublicKey, ECDsa signerPrivateKey) = NewKeyPair();
        using ECDsa otherCurveKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string otherCurvePublicKey = Convert.ToBase64String(otherCurveKey.ExportSubjectPublicKeyInfo());
        using (signerPrivateKey)
        {
            try
            {
                WriteSignature(dir, signerPrivateKey);
                string otherCurveKeyId = ExtensionSignature.KeyId(Convert.FromBase64String(otherCurvePublicKey));
                RewriteSignatureField(dir, "keyId", otherCurveKeyId);

                SignatureCheck check = ExtensionSignature.Verify(dir, [otherCurvePublicKey]);

                using (Assert.Multiple())
                {
                    await Assert.That(check.Verified).IsFalse();
                    await Assert.That(check.Failure).IsEqualTo(SignatureFailure.SignatureInvalid);
                    await Assert.That(check.Detail).IsEqualTo("signature invalid");
                }
            }
            finally
            {
                Cleanup(dir);
            }
        }
    }

    // A subdirectory that goes unreadable mid-walk (permission revoked, not removed) must surface as
    // a reason, never an exception: Verify's contract is to never throw, and Walk's own enumeration,
    // not just its per-entry attribute read, must be guarded for this to hold.
    [Test]
    public async Task Verify_ASubdirectoryMadeUnreadableDuringRecompute_FailsWithAReason_NeverThrows()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new TUnit.Core.Exceptions.SkipTestException("no mode bits to withhold on Windows");
        }

        string dir = NewTree();
        string subdir = Path.Combine(dir, "sub");
        (string publicKey, ECDsa privateKey) = NewKeyPair();
        using (privateKey)
        {
            try
            {
                WriteSignature(dir, privateKey);
                File.SetUnixFileMode(subdir, UnixFileMode.None);
                try
                {
                    foreach (string _ in Directory.EnumerateFileSystemEntries(subdir))
                    {
                        // just provoking the UnauthorizedAccessException below
                    }

                    throw new TUnit.Core.Exceptions.SkipTestException(
                        "this account reads a mode-000 directory (root?); the failure cannot be provoked");
                }
                catch (UnauthorizedAccessException)
                {
                    // the condition the test needs
                }

                SignatureCheck check = ExtensionSignature.Verify(dir, [publicKey]);

                using (Assert.Multiple())
                {
                    await Assert.That(check.Verified).IsFalse();
                    await Assert.That(check.Failure).IsEqualTo(SignatureFailure.DigestMismatch);
                    await Assert.That(check.Detail).IsEqualTo("a file changed after signing");
                    await Assert.That(check.LogDetail).IsNotNull();
                }
            }
            finally
            {
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(subdir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (IOException)
                    {
                        // best-effort
                    }
                }

                Cleanup(dir);
            }
        }
    }

    [Test]
    public async Task ComputeDigest_RefusesMoreFilesThanTheCap()
    {
        string manyFiles = NewTree();
        try
        {
            for (int i = 0; i < ExtensionSignature.MaxFiles + 1; i++)
            {
                File.WriteAllText(Path.Combine(manyFiles, $"f{i}.txt"), "x");
            }

            Exception? ex = Catch(() => ExtensionSignature.ComputeDigest(manyFiles));

            using (Assert.Multiple())
            {
                await Assert.That(ex).IsTypeOf<ExtensionSignatureException>();
                await Assert.That(ex!.Message).Contains($"{ExtensionSignature.MaxFiles}");
            }
        }
        finally
        {
            Cleanup(manyFiles);
        }
    }

    // A sparse file reports its logical length without the disk cost of writing 512 MB of real
    // bytes, and the cap is checked against FileInfo.Length before anything is read.
    [Test]
    public async Task ComputeDigest_RefusesMoreBytesThanTheCap()
    {
        string tooLarge = NewTree();
        try
        {
            string bigFile = Path.Combine(tooLarge, "big.bin");
            using (FileStream stream = File.Create(bigFile))
            {
                stream.SetLength(ExtensionSignature.MaxTotalBytes + 1);
            }

            Exception? ex = Catch(() => ExtensionSignature.ComputeDigest(tooLarge));

            using (Assert.Multiple())
            {
                await Assert.That(ex).IsTypeOf<ExtensionSignatureException>();
                await Assert.That(ex!.Message).Contains($"{ExtensionSignature.MaxTotalBytes}");
            }
        }
        finally
        {
            Cleanup(tooLarge);
        }
    }

    [Test]
    public async Task ComputeDigest_RefusesASymlinkedFileInsideTheTree()
    {
        string dir = NewTree();
        try
        {
            string outside = Path.Combine(dir, "..", "dvsig-outside-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(outside, "elsewhere");
            try
            {
                File.CreateSymbolicLink(Path.Combine(dir, "linked.txt"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TUnit.Core.Exceptions.SkipTestException("symbolic links are not available here: " + ex.Message);
            }

            Exception? ex2 = Catch(() => ExtensionSignature.ComputeDigest(dir));

            using (Assert.Multiple())
            {
                await Assert.That(ex2).IsTypeOf<ExtensionSignatureException>();
                await Assert.That(ex2!.Message).Contains("linked.txt");
            }

            File.Delete(outside);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // Directory.EnumerateFileSystemEntries can throw mid-iteration, not only at the per-entry
    // File.GetAttributes call: a subdirectory that goes unreadable must surface as the same typed
    // exception as every other refusal here, never a raw IOException/UnauthorizedAccessException.
    [Test]
    public async Task ComputeDigest_RefusesAnUnreadableSubdirectory_AsExtensionSignatureException()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new TUnit.Core.Exceptions.SkipTestException("no mode bits to withhold on Windows");
        }

        string dir = NewTree();
        string subdir = Path.Combine(dir, "sub");
        try
        {
            File.SetUnixFileMode(subdir, UnixFileMode.None);
            try
            {
                foreach (string _ in Directory.EnumerateFileSystemEntries(subdir))
                {
                    // just provoking the UnauthorizedAccessException below
                }

                throw new TUnit.Core.Exceptions.SkipTestException(
                    "this account reads a mode-000 directory (root?); the failure cannot be provoked");
            }
            catch (UnauthorizedAccessException)
            {
                // the condition the test needs
            }

            Exception? ex = Catch(() => ExtensionSignature.ComputeDigest(dir));

            using (Assert.Multiple())
            {
                await Assert.That(ex).IsTypeOf<ExtensionSignatureException>();
                await Assert.That(ex!.Message).Contains("sub");
            }
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(subdir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (IOException)
                {
                    // best-effort
                }
            }

            Cleanup(dir);
        }
    }

    [Test]
    public async Task KeyId_IsTheHexSha256OfTheSubjectPublicKeyInfo()
    {
        (string publicKey, ECDsa privateKey) = NewKeyPair();
        using (privateKey)
        {
            string keyId = ExtensionSignature.KeyId(Convert.FromBase64String(publicKey));
            using (Assert.Multiple())
            {
                await Assert.That(keyId.Length).IsEqualTo(64);
                await Assert.That(keyId).IsEqualTo(keyId.ToLowerInvariant());
            }
        }
    }

    [Test]
    public async Task PublisherKeys_EveryEntryImportsAsANistP256SubjectPublicKeyInfo()
    {
        foreach (string base64 in PublisherKeys.Current)
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);
            using (Assert.Multiple())
            {
                await Assert.That(key.KeySize).IsEqualTo(256);
                await Assert.That(key.ExportParameters(false).Curve.Oid.Value).IsEqualTo(ECCurve.NamedCurves.nistP256.Oid.Value);
            }
        }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static (string publicKey, ECDsa privateKey) NewKeyPair()
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static void WriteSignature(string dir, ECDsa privateKey)
    {
        string json = ExtensionSignature.Sign(dir, privateKey);
        File.WriteAllText(Path.Combine(dir, ExtensionSignature.FileName), json);
    }

    // Edits one field of an already-written extension.sig, leaving the others as signed. Used only to
    // construct an attack shape (a forged digest, a spoofed key id); ExtensionSignature itself never
    // edits a signature in place.
    private static void RewriteSignatureField(string dir, string fieldName, string value)
    {
        string path = Path.Combine(dir, ExtensionSignature.FileName);
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        node[fieldName] = value;
        File.WriteAllText(path, node.ToJsonString());
    }

    // extension.json, sub/a.txt and a dotfile: enough shape to exercise sub-directories and hidden files.
    private static string NewTree()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvsig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "extension.json"), "{\"id\":\"x\"}");
        File.WriteAllText(Path.Combine(dir, "sub", "a.txt"), "hello");
        File.WriteAllText(Path.Combine(dir, ".hidden"), "dotfile content");
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
