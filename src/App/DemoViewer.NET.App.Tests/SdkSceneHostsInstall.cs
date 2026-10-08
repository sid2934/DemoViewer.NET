#region

using System.Runtime.CompilerServices;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Installs the SDK scene controls' renderers at assembly load. App.Initialize does the same, but a test
///     that never starts the headless session would otherwise build a scene timeline with no renderer, which
///     holds nothing and never seeks.
/// </summary>
internal static class SdkSceneHostsInstall
{
    [ModuleInitializer]
    internal static void Install() => SdkSceneHosts.Install();
}
