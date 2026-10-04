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
    Console.Error.WriteLine("  extension-signing sign <dir> --key <private.pem>");
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
    File.WriteAllText(outPath, pem + Environment.NewLine);
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(outPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

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
        Console.Error.WriteLine("usage: extension-signing sign <dir> --key <private.pem>");
        return 1;
    }

    string dir = args[0];
    string? keyPath = GetOption(args, "--key");
    if (keyPath is null)
    {
        Console.Error.WriteLine("usage: extension-signing sign <dir> --key <private.pem>");
        return 1;
    }

    using ECDsa key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(keyPath));
    string json = ExtensionSignature.Sign(dir, key);
    string sigPath = Path.Combine(dir, ExtensionSignature.FileName);
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
