#region

using Avalonia;
using Avalonia.Browser;
using DemoViewer.NET;

#endregion

internal sealed class Program
{
    /// <summary>Build avalonia app.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();

    private static Task Main(string[] args) => BuildAvaloniaApp()
        .WithInterFont()
        .StartBrowserAppAsync("out");
}
