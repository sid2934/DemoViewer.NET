#region

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     The detached signature over a staged extension directory (strat-book-plugin.md §7.9): a canonical
///     digest of every file in the tree except <see cref="FileName" /> itself, and the structured form
///     <see cref="FileName" /> carries so verification can tell "not ours" apart from "changed after
///     signing". No project dependency on this file or <see cref="PublisherKeys" />: both are linked, not
///     referenced, into <c>tools/extension-signing</c> so the signing tool and the app run the same code.
/// </summary>
public static class ExtensionSignature
{
    /// <summary>The file beside <c>extension.json</c> that carries the signature.</summary>
    public const string FileName = "extension.sig";

    /// <summary>Refuses a tree with more files than this; a staged extension is a handful.</summary>
    public const int MaxFiles = 2000;

    /// <summary>Refuses a tree whose total content is larger than this, in bytes.</summary>
    public const long MaxTotalBytes = 512L * 1024 * 1024;

    private const string AlgorithmName = "ECDSA-P256-SHA256";
    private const string NotSignedDetail = "the copy is not signed by this app's publisher";
    private const string InvalidDetail = "signature invalid";
    private const string ChangedDetail = "a file changed after signing";

    // Domain-separates the signature from any other 32-byte message this key might ever be asked
    // to sign. The signed message is this tag followed by the digest, never the bare digest.
    private static readonly byte[] DomainTag = Encoding.ASCII.GetBytes("DemoViewer.NET extension signature v1");

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    ///     The canonical digest of <paramref name="directory" />: every file in the tree, recursive,
    ///     except a top-level <see cref="FileName" />, hashed in one deterministic order so renaming,
    ///     adding, removing or altering any file changes the result. For each file, ordinal by its
    ///     '/'-separated relative path: an 8-byte little-endian length and the UTF-8 path, then an 8-byte
    ///     length and the content, preceded once by the file count. Refuses (throws) a reparse point
    ///     anywhere in the tree, or more files or bytes than the caps allow, rather than silently hashing
    ///     less than what is really there.
    /// </summary>
    /// <exception cref="ExtensionSignatureException">A cap was exceeded, a link was found, or a file could not be read.</exception>
    public static byte[] ComputeDigest(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string root = Path.GetFullPath(directory);
        if (IsReparsePoint(root))
        {
            throw new ExtensionSignatureException("the directory is a link");
        }

        List<string> relatives = [];
        Walk(root, root, relatives);
        relatives.Sort(StringComparer.Ordinal);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] prefix = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(prefix, relatives.Count);
        hash.AppendData(prefix);

        long totalBytes = 0;
        byte[] buffer = new byte[81920];
        foreach (string relative in relatives)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(relative);
            BinaryPrimitives.WriteInt64LittleEndian(prefix, pathBytes.Length);
            hash.AppendData(prefix);
            hash.AppendData(pathBytes);

            string fullPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            long length;
            try
            {
                length = new FileInfo(fullPath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ExtensionSignatureException($"'{relative}' could not be read", ex);
            }

            totalBytes += length;
            if (totalBytes > MaxTotalBytes)
            {
                throw new ExtensionSignatureException($"the signed tree is larger than {MaxTotalBytes} bytes");
            }

            BinaryPrimitives.WriteInt64LittleEndian(prefix, length);
            hash.AppendData(prefix);

            try
            {
                using FileStream stream = File.OpenRead(fullPath);
                long remaining = length;
                int read;
                while (remaining > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }

                if (remaining != 0)
                {
                    throw new ExtensionSignatureException($"'{relative}' changed size while it was being read");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ExtensionSignatureException($"'{relative}' could not be read", ex);
            }
        }

        return hash.GetHashAndReset();
    }

    /// <summary>
    ///     Signs <paramref name="directory" />'s canonical digest with <paramref name="privateKey" /> and
    ///     returns the JSON to write as <see cref="FileName" />. Does not write the file: the caller (the
    ///     signing tool) decides where.
    /// </summary>
    public static string Sign(string directory, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        byte[] digest = ComputeDigest(directory);
        byte[] signature = privateKey.SignData(Payload(digest), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string keyId = KeyId(privateKey.ExportSubjectPublicKeyInfo());
        SignatureFile file = new(AlgorithmName, keyId, Convert.ToBase64String(digest), Convert.ToBase64String(signature));
        return JsonSerializer.Serialize(file, JsonOptions);
    }

    /// <summary>
    ///     Verifies <paramref name="directory" />'s <see cref="FileName" /> against <paramref name="publisherKeysBase64Spki" />
    ///     (each a SubjectPublicKeyInfo, base64). Never throws: every failure, including an I/O error or a
    ///     cap exceeded while recomputing the digest, is a <see cref="SignatureCheck" /> with a reason.
    ///     The order matters: the recorded digest is attacker-controlled data inside the file until the
    ///     signature over it verifies, so nothing here compares digests before that.
    /// </summary>
    public static SignatureCheck Verify(string directory, IReadOnlyList<string> publisherKeysBase64Spki)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(publisherKeysBase64Spki);

        string sigPath = Path.Combine(directory, FileName);
        string sigText;
        try
        {
            if (!File.Exists(sigPath))
            {
                return SignatureCheck.Fail(SignatureFailure.Missing, NotSignedDetail);
            }

            sigText = File.ReadAllText(sigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SignatureCheck.Fail(SignatureFailure.Unreadable, InvalidDetail, ex.Message);
        }

        SignatureFile? file;
        byte[] recordedDigest;
        byte[] signature;
        try
        {
            file = JsonSerializer.Deserialize<SignatureFile>(sigText, JsonOptions);
            if (file?.KeyId is null || file.Digest is null || file.Signature is null)
            {
                return SignatureCheck.Fail(SignatureFailure.Malformed, InvalidDetail, "the signature file is missing a required field");
            }

            recordedDigest = Convert.FromBase64String(file.Digest);
            signature = Convert.FromBase64String(file.Signature);
            if (recordedDigest.Length != 32)
            {
                return SignatureCheck.Fail(SignatureFailure.Malformed, InvalidDetail, "the recorded digest is not 32 bytes");
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return SignatureCheck.Fail(SignatureFailure.Malformed, InvalidDetail, ex.Message);
        }

        bool signatureOk;
        try
        {
            signatureOk = VerifiesAgainstAnyKey(file.KeyId, Payload(recordedDigest), signature, publisherKeysBase64Spki, out bool keyKnown);
            if (!keyKnown)
            {
                return SignatureCheck.Fail(SignatureFailure.UnknownKey, NotSignedDetail);
            }
        }
        catch (CryptographicException ex)
        {
            return SignatureCheck.Fail(SignatureFailure.SignatureInvalid, InvalidDetail, ex.Message);
        }

        if (!signatureOk)
        {
            return SignatureCheck.Fail(SignatureFailure.SignatureInvalid, InvalidDetail);
        }

        byte[] actualDigest;
        try
        {
            actualDigest = ComputeDigest(directory);
        }
        catch (ExtensionSignatureException ex)
        {
            // The sign path enforces these same caps and refuses the same links, so a tree that now
            // trips one of them changed after it was signed.
            return SignatureCheck.Fail(SignatureFailure.DigestMismatch, ChangedDetail, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Belt over ComputeDigest's own guard: Verify's contract is never to throw regardless.
            return SignatureCheck.Fail(SignatureFailure.DigestMismatch, ChangedDetail, ex.Message);
        }

        return CryptographicOperations.FixedTimeEquals(actualDigest, recordedDigest)
            ? SignatureCheck.Ok
            : SignatureCheck.Fail(SignatureFailure.DigestMismatch, ChangedDetail);
    }

    /// <summary>The SHA-256 of a SubjectPublicKeyInfo, lowercase hex: how a signature names the key it claims.</summary>
    public static string KeyId(byte[] subjectPublicKeyInfo) => Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));

    private static byte[] Payload(byte[] digest) => [..DomainTag, ..digest];

    // True only when a listed key's id matches the claimed one AND that key verifies the signature.
    // keyKnown is false when no listed key has the claimed id, which the caller reads as "not ours"
    // rather than "broken", since nothing here can check a signature against a key it was not given.
    private static bool VerifiesAgainstAnyKey(
        string claimedKeyId, byte[] payload, byte[] signature, IReadOnlyList<string> publisherKeysBase64Spki, out bool keyKnown)
    {
        keyKnown = false;
        foreach (string base64Key in publisherKeysBase64Spki)
        {
            byte[] spki;
            try
            {
                spki = Convert.FromBase64String(base64Key);
            }
            catch (FormatException)
            {
                continue; // a malformed embedded key is this app's bug, never grounds to trust anything
            }

            if (!string.Equals(KeyId(spki), claimedKeyId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            keyKnown = true;
            using ECDsa key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(spki, out _);
            }
            catch (CryptographicException)
            {
                continue;
            }

            // The signature is always IEEE P1363 over a P-256 key; a listed key on another curve
            // cannot be the one that produced it, whatever its id, so it is never asked to verify.
            if (!string.Equals(key.ExportParameters(false).Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return true;
            }
        }

        return false;
    }

    // Manual recursive walk, never Directory.EnumerateFiles(..., AllDirectories): that can follow a
    // symlinked subdirectory transparently. Every entry, file or directory, is checked for a reparse
    // point before it is used and refused rather than followed. AttributesToSkip is cleared because the
    // default skips Hidden, and .NET marks a Unix dotfile Hidden; skipping it here would let an added
    // dotfile hide from the digest.
    //
    // The enumeration itself (not just File.GetAttributes on an entry) can throw mid-iteration, a
    // directory disappearing or a permission revoked under the walk, so the whole foreach is inside
    // the guard, not only the per-entry attribute read.
    private static void Walk(string dir, string root, List<string> relatives)
    {
        // IgnoreInaccessible defaults to true, which would silently skip an unreadable entry instead
        // of throwing: exactly the gap that would let part of the tree go unhashed without a reason.
        EnumerationOptions options = new() { AttributesToSkip = 0, IgnoreInaccessible = false };
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*", options))
            {
                string relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ExtensionSignatureException($"'{relative}' could not be read", ex);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ExtensionSignatureException($"'{relative}' is a link; the signed tree may not contain one");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Walk(entry, root, relatives);
                    continue;
                }

                if (string.Equals(relative, FileName, StringComparison.Ordinal))
                {
                    continue; // the signature cannot sign itself
                }

                relatives.Add(relative);
                if (relatives.Count > MaxFiles)
                {
                    throw new ExtensionSignatureException($"the signed tree has more than {MaxFiles} files");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string relativeDir = string.Equals(dir, root, StringComparison.Ordinal)
                ? "the signed directory"
                : $"'{Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/')}'";
            throw new ExtensionSignatureException($"{relativeDir} could not be read", ex);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    // The on-disk form of extension.sig. JSON, not the canonical form itself: only the signed payload
    // (the domain tag plus Digest's bytes) needs to match byte for byte across tools, so this DTO's own
    // encoding is free to be ordinary JSON.
    private sealed class SignatureFile
    {
        public SignatureFile()
        {
        }

        public SignatureFile(string alg, string keyId, string digest, string signature)
        {
            Alg = alg;
            KeyId = keyId;
            Digest = digest;
            Signature = signature;
        }

        public string? Alg { get; set; }

        public string? KeyId { get; set; }

        public string? Digest { get; set; }

        public string? Signature { get; set; }
    }
}

/// <summary>Why <see cref="ExtensionSignature.Verify" /> did not trust a directory.</summary>
public enum SignatureFailure
{
    /// <summary>No <see cref="ExtensionSignature.FileName" /> in the directory.</summary>
    Missing,

    /// <summary><see cref="ExtensionSignature.FileName" /> is not valid JSON, or a required field is missing or not valid base64.</summary>
    Malformed,

    /// <summary>The signature names a key id none of the given public keys match.</summary>
    UnknownKey,

    /// <summary>The signature does not verify against the key its id names.</summary>
    SignatureInvalid,

    /// <summary>The signature verifies, but the directory's current digest is not the one it signed.</summary>
    DigestMismatch,

    /// <summary>The signature file or the directory could not be read.</summary>
    Unreadable
}

/// <summary>
///     What <see cref="ExtensionSignature.Verify" /> found. <see cref="Detail" /> is always one of three
///     phrases ("not signed by this app's publisher", "signature invalid", "a file changed after
///     signing"), the same vocabulary <see cref="TrustVerdict" /> and the loader's <c>Untrusted</c>
///     outcome use; <see cref="LogDetail" /> carries the specific reason for the log.
/// </summary>
public sealed record SignatureCheck(bool Verified, SignatureFailure? Failure, string? Detail, string? LogDetail = null)
{
    /// <summary>The signature verified against a known key over the current digest.</summary>
    public static SignatureCheck Ok { get; } = new(true, null, null);

    /// <summary>A failure with its public detail and, optionally, a log-only specific.</summary>
    public static SignatureCheck Fail(SignatureFailure failure, string detail, string? logDetail = null) => new(false, failure, detail, logDetail);
}

/// <summary>A staged directory's signature could not be computed or checked: a cap was exceeded, a link was found, or a file could not be read.</summary>
public sealed class ExtensionSignatureException : Exception
{
    public ExtensionSignatureException()
    {
    }

    public ExtensionSignatureException(string message) : base(message)
    {
    }

    public ExtensionSignatureException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
