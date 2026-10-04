using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    return args[0] switch
    {
        "keygen" => Keygen(args[1..]),
        "sign" => Sign(args[1..]),
        "verify" => Verify(args[1..]),
        "report" => Report(args[1..]),
        "manifest" => Manifest(args[1..]),
        "zip" => Zip(args[1..]),
        "feed-merge" => FeedMergeCommand(args[1..]),
        "feed-check" => FeedCheck(args[1..]),
        _ => Unknown(args[0])
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command '{command}'");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  extension-signing keygen --out <private.pem>");
    Console.Error.WriteLine("  extension-signing sign <dir> --key <private.pem> [--force]");
    Console.Error.WriteLine("  extension-signing verify <dir> [--key <public-or-private.pem>]");
    Console.Error.WriteLine("  extension-signing report <manifest.json> --contract <ver> --cs2demokit <ver> [--app-version <ver>]");
    Console.Error.WriteLine("  extension-signing manifest <assembly.dll>");
    Console.Error.WriteLine("  extension-signing zip <dir> --out <zip>");
    Console.Error.WriteLine("  extension-signing feed-merge <existing.json> <entry.json> --id <id> --out <merged.json> [--allow-downgrade]");
    Console.Error.WriteLine("  extension-signing feed-check <feed.json>");
}

static string? GetOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal))
        {
            return args[i + 1];
        }
    }

    return null;
}

static bool HasFlag(string[] args, string name) => args.Contains(name, StringComparer.Ordinal);

// The file never exists world- or group-readable, even for the instant between create and chmod:
// the create mode is set on the open itself, off Windows (which has no POSIX mode to set).
static void WritePrivateKeyFile(string path, string pem)
{
    if (OperatingSystem.IsWindows())
    {
        File.WriteAllText(path, pem + Environment.NewLine);
        return;
    }

    FileStreamOptions options = new()
    {
        Mode = FileMode.Create,
        Access = FileAccess.Write,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
    };
    using FileStream stream = new(path, options);
    using StreamWriter writer = new(stream);
    writer.Write(pem);
    writer.Write(Environment.NewLine);
}

static int Keygen(string[] args)
{
    string? outPath = GetOption(args, "--out");
    if (outPath is null)
    {
        Console.Error.WriteLine("usage: extension-signing keygen --out <private.pem>");
        return 1;
    }

    using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string pem = key.ExportPkcs8PrivateKeyPem();
    WritePrivateKeyFile(outPath, pem);

    string publicBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    string keyId = ExtensionSignature.KeyId(key.ExportSubjectPublicKeyInfo());

    Console.WriteLine($"Private key written to {outPath}.");
    Console.WriteLine("Keep it outside this repo. Never commit it, not even to a private branch.");
    Console.WriteLine($"Key id: {keyId}");
    Console.WriteLine();
    Console.WriteLine("Paste into PublisherKeys.cs:");
    Console.WriteLine($"    public const string Primary = \"{publicBase64}\";");
    return 0;
}

static int Sign(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: extension-signing sign <dir> --key <private.pem> [--force]");
        return 1;
    }

    string dir = args[0];
    string? keyPath = GetOption(args, "--key");
    if (keyPath is null)
    {
        Console.Error.WriteLine("usage: extension-signing sign <dir> --key <private.pem> [--force]");
        return 1;
    }

    string sigPath = Path.Combine(dir, ExtensionSignature.FileName);
    if (File.Exists(sigPath) && !HasFlag(args, "--force"))
    {
        Console.Error.WriteLine($"{sigPath} already exists. Pass --force to overwrite it.");
        return 1;
    }

    using ECDsa key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(keyPath));
    string json = ExtensionSignature.Sign(dir, key);
    File.WriteAllText(sigPath, json);
    Console.WriteLine($"Wrote {sigPath}");
    return 0;
}

static int Verify(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: extension-signing verify <dir> [--key <public-or-private.pem>]");
        return 1;
    }

    string dir = args[0];
    // --key is dry-run/local-testing only: an ephemeral keygen'd key never appears in
    // PublisherKeys.Current, so a real release never passes this flag.
    string? keyPath = GetOption(args, "--key");
    IReadOnlyList<string> keys = PublisherKeys.Current;
    if (keyPath is not null)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));
        keys = [Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())];
    }

    SignatureCheck check = ExtensionSignature.Verify(dir, keys);
    if (check.Verified)
    {
        Console.WriteLine($"OK: {dir} verifies against {(keyPath is null ? "this app's publisher keys" : keyPath)}.");
        return 0;
    }

    Console.Error.WriteLine($"FAILED ({check.Failure}): {check.Detail}");
    if (check.LogDetail is not null)
    {
        Console.Error.WriteLine("  " + check.LogDetail);
    }

    return 1;
}

// CompatibilityReport.Describe takes the host explicitly rather than ExtensionHost.Current, so this
// tool never links ExtensionHost.cs (which would pull in AppVersionInfo and, through it, Avalonia's
// transitive graph by way of the app assembly). The caller (pack-extension.sh) supplies the contract
// and CS2DemoKit versions it already knows from the repo.
static int Report(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: extension-signing report <manifest.json> --contract <ver> --cs2demokit <ver> [--app-version <ver>]");
        return 1;
    }

    string manifestPath = args[0];
    string? contractText = GetOption(args, "--contract");
    string? kitText = GetOption(args, "--cs2demokit");
    string? appText = GetOption(args, "--app-version");
    if (contractText is null || kitText is null)
    {
        Console.Error.WriteLine("usage: extension-signing report <manifest.json> --contract <ver> --cs2demokit <ver> [--app-version <ver>]");
        return 1;
    }

    ExtensionManifest manifest;
    try
    {
        manifest = ExtensionManifest.Parse(File.ReadAllText(manifestPath));
    }
    catch (ExtensionManifestException ex)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 1;
    }

    if (!SemVersion.TryParse(contractText, out SemVersion? contract))
    {
        Console.Error.WriteLine($"error: '--contract {contractText}' is not a semantic version.");
        return 1;
    }

    if (!SemVersion.TryParse(kitText, out SemVersion? kit))
    {
        Console.Error.WriteLine($"error: '--cs2demokit {kitText}' is not a semantic version.");
        return 1;
    }

    SemVersion? app = null;
    if (appText is not null && !SemVersion.TryParse(appText, out app))
    {
        Console.Error.WriteLine($"error: '--app-version {appText}' is not a semantic version.");
        return 1;
    }

    ExtensionHostInfo host = new(contract, app, kit);
    Console.WriteLine(CompatibilityReport.Describe(manifest, host));
    return PackCompatibility.Check(manifest, host).IsCompatible ? 0 : 1;
}

static int Manifest(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: extension-signing manifest <assembly.dll>");
        return 1;
    }

    try
    {
        Console.WriteLine(ReadEmbeddedManifestText(args[0]));
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or BadImageFormatException)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 1;
    }
}

// Reads the embedded extension.json resource straight out of the PE metadata, without loading the
// assembly: the extension's referenced assemblies (Avalonia, CS2DemoKit) are not on this tool's probe
// path and never need to be, since nothing here runs any code from the DLL.
static string ReadEmbeddedManifestText(string dllPath)
{
    using FileStream stream = File.OpenRead(dllPath);
    using PEReader peReader = new(stream);
    MetadataReader metadataReader = peReader.GetMetadataReader();
    foreach (ManifestResourceHandle handle in metadataReader.ManifestResources)
    {
        ManifestResource resource = metadataReader.GetManifestResource(handle);
        string name = metadataReader.GetString(resource.Name);
        if (!string.Equals(name, ExtensionManifest.FileName, StringComparison.Ordinal))
        {
            continue;
        }

        if (!resource.Implementation.IsNil)
        {
            throw new InvalidOperationException($"'{ExtensionManifest.FileName}' is not embedded directly in {Path.GetFileName(dllPath)}.");
        }

        CorHeader corHeader = peReader.PEHeaders.CorHeader
            ?? throw new InvalidOperationException($"{Path.GetFileName(dllPath)} is not a managed assembly.");
        PEMemoryBlock resourceSection = peReader.GetSectionData(corHeader.ResourcesDirectory.RelativeVirtualAddress);
        BlobReader blobReader = resourceSection.GetReader((int)resource.Offset, resourceSection.Length - (int)resource.Offset);
        int length = blobReader.ReadInt32();
        byte[] bytes = blobReader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }

    throw new InvalidOperationException($"{Path.GetFileName(dllPath)} embeds no '{ExtensionManifest.FileName}' resource.");
}

static int Zip(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: extension-signing zip <dir> --out <zip>");
        return 1;
    }

    string dir = args[0];
    string? outPath = GetOption(args, "--out");
    if (outPath is null)
    {
        Console.Error.WriteLine("usage: extension-signing zip <dir> --out <zip>");
        return 1;
    }

    DeterministicZip.Write(dir, outPath);
    Console.WriteLine($"Wrote {outPath}");
    return 0;
}

static int FeedMergeCommand(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: extension-signing feed-merge <existing.json> <entry.json> --id <id> --out <merged.json> [--allow-downgrade]");
        return 1;
    }

    string existingPath = args[0];
    string entryPath = args[1];
    string? id = GetOption(args, "--id");
    string? outPath = GetOption(args, "--out");
    if (id is null || outPath is null)
    {
        Console.Error.WriteLine("usage: extension-signing feed-merge <existing.json> <entry.json> --id <id> --out <merged.json> [--allow-downgrade]");
        return 1;
    }

    // A missing existing-feed file means "nothing published yet" (the rolling release's first
    // ever entry), never a fatal path: the workflow writes this file only when the release and
    // its extensions.json asset already exist.
    string? existingJson = File.Exists(existingPath) ? File.ReadAllText(existingPath) : null;
    string entryJson = File.ReadAllText(entryPath);

    try
    {
        string merged = ExtensionFeedMerge.Merge(existingJson, entryJson, id, HasFlag(args, "--allow-downgrade"));
        File.WriteAllText(outPath, merged);
        Console.WriteLine($"Wrote {outPath}");
        return 0;
    }
    catch (ExtensionFeedMergeException ex)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 1;
    }
}

static int FeedCheck(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: extension-signing feed-check <feed.json>");
        return 1;
    }

    try
    {
        ExtensionFeed feed = ExtensionFeed.Parse(File.ReadAllText(args[0]));
        string latest = feed.Latest is { } entry ? entry.Version.ToString() : "(none)";
        Console.WriteLine($"OK: {feed.Id} has {feed.Entries.Count} entr{(feed.Entries.Count == 1 ? "y" : "ies")}, latest {latest}.");
        return 0;
    }
    catch (ExtensionFeedException ex)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 1;
    }
}
