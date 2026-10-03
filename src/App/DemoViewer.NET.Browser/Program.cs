#region

using Avalonia;
using Avalonia.Browser;
using DemoViewer.NET;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;

#endregion

internal sealed class Program
{
    /// <summary>Build avalonia app.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();

    private static Task Main(string[] args)
    {
        // Same list as the Desktop head: the browser compile-links the extension it was built with.
        FeaturePacks.Configure([new StratBookPack()]);
        return BuildAvaloniaApp()
            .WithInterFont()
            .StartBrowserAppAsync("out");
    }
}
