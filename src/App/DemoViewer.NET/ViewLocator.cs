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
        Type? type = Type.GetType(name) ?? PackViewType(name);

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

    // A pack's views live in the pack's assembly under the same naming convention, so the search
    // continues through the compatible packs' assemblies (FeaturePacks.Compatible; an incompatible pack
    // composed nothing, so none of its view models exist). Type.GetType above sees only this assembly
    // and the core library.
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
    ///     <b><see cref="ViewModelBase" />, deliberately, not <c>ObservableObject</c>.</b> This template is
    ///     registered on the <c>Application</c>, so it is the last-resort match for every bound object in
    ///     the app, and most <c>ObservableObject</c>s here have no <c>…View</c> type at all, which
    ///     <see cref="Build" /> would render as "Not Found: …". A view-model hosted by a bare
    ///     <c>ContentControl</c> must derive from <see cref="ViewModelBase" /> or it silently renders as its
    ///     own <c>ToString()</c>: <c>Playback2DExportDialogViewModel</c> shipped as an
    ///     <c>ObservableObject</c> once and the entire 2D export pane rendered as one line of
    ///     fully-qualified type name.
    /// </remarks>
    public bool Match(object? data) => data is ViewModelBase;
}
