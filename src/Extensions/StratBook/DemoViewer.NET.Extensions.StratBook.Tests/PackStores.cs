#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A declared store opened the way the app opens it, for the store tests that need a real folder. The
///     host's own <c>ExtensionFolder</c> and not a double, so what these tests exercise is what ships:
///     its relative-path refusals, its atomic writes and the stamps they leave.
/// </summary>
internal static class PackStores
{
    /// <summary>The folder a store declares, under <paramref name="configRoot" />. Neither folder need exist.</summary>
    /// <param name="configRoot">The config root to resolve against.</param>
    /// <param name="storeId">A <see cref="StratBookStores" /> id of a store declared under the config root.</param>
    public static IExtensionFolder Folder(string configRoot, string storeId) =>
        // The cache root is a sibling path that nothing here opens, so it is never created.
        new ExtensionStores(StratBookPack.PackId, configRoot, configRoot + "-cache", StratBookStores.All).Open(storeId)!;
}
