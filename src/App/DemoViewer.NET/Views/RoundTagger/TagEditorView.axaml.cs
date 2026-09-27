#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DemoViewer.NET.Modules.RoundTagger.Review;

#endregion

namespace DemoViewer.NET.Views.RoundTagger;

/// <summary>
///     The tag editor's view. Enter in a box saves and Esc cancels; the 2D view's keymap ignores keys while a
///     text box has focus, so typing "y" into a label never accepts a suggestion. The code picker takes focus
///     when the editor opens.
/// </summary>
public partial class TagEditorView : UserControl
{
    public TagEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("FromBox")?.Focus());
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TagEditorViewModel editor || e.Source is not (TextBox or AutoCompleteBox or ComboBox)
            && e.Source is not Control { Parent: TextBox or AutoCompleteBox })
        {
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && e.Source is not AutoCompleteBox)
        {
            editor.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            editor.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }
}
