#region

using Avalonia;
using DemoViewer.NET.Visualization.Internal;

#endregion

namespace DemoViewer.NET.Visualization.Sample;

internal sealed class Program
{
    /// <summary>Build avalonia app.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Main.</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless metrics mode — compute the v1 layout baseline and print the
        // markdown table without starting the GUI (CI-friendly, no UI thread).
        if (args.Contains("--baseline"))
        {
            Console.Write(BaselineRunner.Run());
            return;
        }

        // Headless SVG export — draws each fixture's layout straight from the
        // LayoutResult geometry to /tmp/viz-svg/{name}.svg. No render backend /
        // display required (unlike PNG capture, which needs a GPU).
        if (args.Contains("--svg"))
        {
            const string Dir = "/tmp/viz-svg";
            Directory.CreateDirectory(Dir);
            GraphStyle style = new();
            int count = 0;
            foreach (BaselineRunner.Fixture f in BaselineRunner.BuildAll())
            {
                LayoutResult layout = LayoutPipeline.ComputeFullLayout(f.Nodes, f.Edges, f.Groups, f.Tables, style);
                File.WriteAllText(Path.Combine(Dir, $"{f.Name}.svg"),
                    SvgExporter.ToSvg(f.Nodes, f.Edges, f.Tables, style, layout));
                count++;
            }

            Console.WriteLine($"Wrote {count} SVGs to {Dir}");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
