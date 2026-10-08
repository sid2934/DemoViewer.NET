#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>The keymap a strat canvas reads in tests: the shipped table, with the user's rows when given.</summary>
internal static class StratTestKeymap
{
    /// <summary>The shipped keymap with no override rows.</summary>
    public static IExtensionKeymap Shipped { get; } = HostKeymap.Instance;

    /// <summary>The shipped keymap with <paramref name="rows" /> as the user's override rows.</summary>
    public static IExtensionKeymap WithOverrides(params string[] rows) =>
        new HostKeymap(() => CommandRegistry.Default.EffectiveBindings, () => rows);
}
