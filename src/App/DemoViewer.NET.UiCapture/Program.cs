#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using DemoViewer.NET.UiCapture;

#endregion

// UiCapture — renders a named design variant (or an A/B pair) to a PNG via headless Skia, for the
// UI/UX screenshot-review workflow.
//
//   dotnet run --project src/App/DemoViewer.NET.UiCapture -- <variant> [--out <path>] [--size WxH]
//   dotnet run --project src/App/DemoViewer.NET.UiCapture -- ab <variantA> <variantB> [--out <path>] [--size WxH]
//   dotnet run --project src/App/DemoViewer.NET.UiCapture -- list
//
// --out defaults to %TEMP%/demoviewer-uitests/<variant>.png; --size defaults to the variant's own size
// (the WxH override sizes the render window, e.g. 800x600).

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

if (args[0] is "list")
{
    Console.WriteLine("Variants:");
    foreach (string name in Variants.All.Keys.OrderBy(n => n, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {name}");
    }

    return 0;
}

string? outArg = OptValue("--out");
Size? sizeArg = ParseSize(OptValue("--size"));
// --theme accepts ANY registry id: dark / light / system, the built-in customs (high-contrast, egirl), or a
// drop-in id (a *.json in <config>/themes/, re-scanned each run — set DEMOVIEWER_CONFIG_DIR to author against
// a scratch folder). Blank → the app default.
ThemeVariant? themeArg = CaptureHost.ResolveTheme(OptValue("--theme"));

try
{
    if (args[0] is "ab")
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("ab mode needs two variant names: ab <variantA> <variantB>");
            return 2;
        }

        Func<Control> a = Resolve(args[1]);
        Func<Control> b = Resolve(args[2]);
        Size half = sizeArg ?? new Size(620, 360);
        string outPng = CaptureHost.ResolveOut(outArg, $"ab-{args[1]}-vs-{args[2]}.png");
        string written = await CaptureHost.CaptureAb(a, b, half, outPng, args[1], args[2], themeArg);
        Console.WriteLine($"[uicapture] wrote {written}");
        return 0;
    }

    string variant = args[0];
    Func<Control> factory = Resolve(variant);
    Size size = sizeArg ?? new Size(640, 360);
    string outPath = CaptureHost.ResolveOut(outArg, $"{variant}.png");
    string result = await CaptureHost.CaptureView(factory, size, outPath, theme: themeArg);
    Console.WriteLine($"[uicapture] wrote {result}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[uicapture] FAILED: {ex.Message}");
    return 1;
}

Func<Control> Resolve(string name)
{
    if (Variants.All.TryGetValue(name, out Func<Control>? f))
    {
        return f;
    }

    throw new ArgumentException($"Unknown variant '{name}'. Run with 'list' to see variants.");
}

string? OptValue(string flag)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static Size? ParseSize(string? s)
{
    if (string.IsNullOrWhiteSpace(s))
    {
        return null;
    }

    string[] parts = s.Split('x', 'X');
    if (parts.Length == 2
        && double.TryParse(parts[0], out double w)
        && double.TryParse(parts[1], out double h))
    {
        return new Size(w, h);
    }

    throw new ArgumentException($"Bad --size '{s}'; expected WxH like 800x600.");
}

static void PrintUsage()
{
    Console.WriteLine("UiCapture — render a design variant to PNG (headless Skia).");
    Console.WriteLine();
    Console.WriteLine("  <variant> [--out <path>] [--size WxH]     render one variant");
    Console.WriteLine("  ab <a> <b> [--out <path>] [--size WxH]    render two variants side-by-side");
    Console.WriteLine("  list                                      list available variants");
}
