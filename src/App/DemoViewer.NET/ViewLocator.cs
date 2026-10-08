#region

using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET;

/// <summary>
///     Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    /// <summary>Build.</summary>
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        string name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        Type? type = Type.GetType(name) ?? param.GetType().Assembly.GetType(name) ?? PackViewType(name);

        if (type != null)
        {
            // The process's tracker, not one read from App.Services: that container can be disposed while
            // views are still being built, and resolving from a disposed container throws.
            return Create(type, ExtensionFaults.Current);
        }

        return new TextBlock
        {
            Text = "Not Found: " + name
        };
    }

    /// <summary>
    ///     Builds <paramref name="viewType" />. An extension's view whose constructor throws is reported against
    ///     the extension and replaced by the placeholder: this runs inside a layout pass, so a throw here would
    ///     otherwise rebuild and throw again on every pass until the app went down.
    /// </summary>
    internal static Control Create(Type viewType, ExtensionFaults? faults)
    {
        if (faults?.Owner(viewType.Assembly) is not { } scope)
        {
            return (Control)Activator.CreateInstance(viewType)!;
        }

        return faults.Run<Control>(scope, "view " + viewType.Name, () => (Control)Activator.CreateInstance(viewType)!,
            ExtensionPlaceholder.View(scope, "this view"));
    }

    // Type.GetType sees only this assembly and the core library, and the view model's own assembly covers an
    // extension whose views sit beside its view models. A pack may still keep a view in its entry assembly
    // for a view model defined elsewhere, so the search ends with the compatible packs' assemblies.
    private static Type? PackViewType(string name)
    {
        foreach (IExtension pack in FeaturePacks.Compatible)
        {
            if (pack.GetType().Assembly.GetType(name) is { } type)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>Match.</summary>
    /// <remarks>
    ///     <b><see cref="ViewModelBase" /> or <see cref="IExtensionViewModel" />, deliberately, not
    ///     <c>ObservableObject</c>.</b> This template is registered on the <c>Application</c>, so it is the
    ///     last-resort match for every bound object in the app, and most <c>ObservableObject</c>s here have no
    ///     <c>View</c> type at all, which <see cref="Build" /> would render as "Not Found". A view model hosted
    ///     by a bare <c>ContentControl</c> must be one of the two or it silently renders as its own
    ///     <c>ToString()</c>: <c>Playback2DExportDialogViewModel</c> shipped as an <c>ObservableObject</c> once
    ///     and the entire 2D export pane rendered as one line of fully-qualified type name. Extensions build
    ///     against the SDK alone, so the marker is how their view models opt in.
    /// </remarks>
    public bool Match(object? data) => data is ViewModelBase or IExtensionViewModel;
}
