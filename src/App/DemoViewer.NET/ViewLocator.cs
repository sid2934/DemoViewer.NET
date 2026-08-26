#region

using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
        Type? type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock
        {
            Text = "Not Found: " + name
        };
    }

    /// <summary>
    ///     Match.
    ///     <para>
    ///         <b><see cref="ViewModelBase" />, deliberately, not <c>ObservableObject</c>.</b> This template
    ///         is registered on the <c>Application</c>, so it is the last-resort match for every bound
    ///         object in the app — and most <c>ObservableObject</c>s here are row and item view-models with
    ///         no <c>…View</c> type at all, which <see cref="Build" /> would render as "Not Found: …". The
    ///         base class is therefore an opt-in: <b>a view-model hosted by a bare
    ///         <c>ContentControl</c> must derive from <see cref="ViewModelBase" /></b> or it silently
    ///         renders as its own <c>ToString()</c>. That is not hypothetical —
    ///         <c>Playback2DExportDialogViewModel</c> shipped as an <c>ObservableObject</c> and the entire
    ///         2D export pane rendered as one line of fully-qualified type name.
    ///     </para>
    /// </summary>
    public bool Match(object? data) => data is ViewModelBase;
}
