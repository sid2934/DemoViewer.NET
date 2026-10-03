#region

using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Declares the same compiled-in pack list the heads do, at assembly load, so every test that builds
///     the composition root through <c>App.BuildServices(windowService)</c> or reads a static registry
///     (<c>CommandRegistry.Default</c>, <c>JobKindRegistry.Default</c>, the catalog) sees the pack the
///     shipped build has. Tests of the pack-off path override the gate, not this list.
/// </summary>
internal static class CompiledInPacks
{
    [ModuleInitializer]
    internal static void Declare() => FeaturePacks.Configure([new StratBookPack()]);
}
