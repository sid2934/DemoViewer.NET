#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>What the host shows where an extension's view or page could not be built.</summary>
public static class ExtensionPlaceholder
{
    /// <summary>A one-line notice naming the extension and the surface it failed to build.</summary>
    /// <param name="scope">The extension.</param>
    /// <param name="what">The surface, as the sentence names it ("this tab", "this page").</param>
    public static Control View(ExtensionScope scope, string what)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new TextBlock
        {
            Text = $"{scope.Name} could not show {what}. The error is in the diagnostics log.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.75
        };
    }
}
