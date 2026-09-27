#region

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DemoViewer.NET.Modules.SuggestedTags;

#endregion

namespace DemoViewer.NET.Views.SuggestedTags;

/// <summary>
///     The Proposal Queue's view, docked by the 2D Playback view with the tab's
///     <see cref="SuggestionQueueViewModel" /> as its DataContext. Keys never start here: the 2D view routes
///     them through the VM. The editor is its own view (<c>TagEditorView</c>), hosted by the review panel;
///     what this owns is handing focus back to the 2D view when the editor closes.
/// </summary>
public partial class SuggestionQueueView : UserControl
{
    private SuggestionQueueViewModel? _bound;

    public SuggestionQueueView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _bound = DataContext as SuggestionQueueViewModel;
        if (_bound is not null)
        {
            _bound.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    // A click on a row selects it and seeks to its start, the way K and J do.
    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_bound is not null && sender is Control { Tag: SuggestionRowViewModel row })
        {
            _bound.Selected = row;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SuggestionQueueViewModel.IsEditing) || _bound is null)
        {
            return;
        }

        // Posted: the editor's own view takes focus when it opens; closing hands it back to the keymap.
        if (!_bound.IsEditing)
        {
            Dispatcher.UIThread.Post(ReturnFocus);
        }
    }

    // The keymap lives on the 2D view, the nearest focusable ancestor.
    private void ReturnFocus()
    {
        for (Control? c = Parent as Control; c is not null; c = c.Parent as Control)
        {
            if (c.Focusable)
            {
                c.Focus();
                return;
            }
        }
    }
}
