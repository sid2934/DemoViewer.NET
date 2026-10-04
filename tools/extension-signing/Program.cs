using System.Security.Cryptography;
using DemoViewer.NET.Extensions.Loading;

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
    Console.Error.WriteLine("  extension-signing verify <dir>");
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
        Console.Error.WriteLine("usage: extension-signing verify <dir>");
        return 1;
    }

    string dir = args[0];
    SignatureCheck check = ExtensionSignature.Verify(dir, PublisherKeys.Current);
    if (check.Verified)
    {
        Console.WriteLine($"OK: {dir} verifies against this app's publisher keys.");
        return 0;
    }

    Console.Error.WriteLine($"FAILED ({check.Failure}): {check.Detail}");
    if (check.LogDetail is not null)
    {
        Console.Error.WriteLine("  " + check.LogDetail);
    }

    return 1;
}
