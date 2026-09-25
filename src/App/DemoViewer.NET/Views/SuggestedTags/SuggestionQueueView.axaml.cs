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
///     them through the VM. What this owns is the editor's focus, which moves into the first box when the
///     editor opens and back to the 2D view when it closes, and Enter and Esc inside the editor.
/// </summary>
public partial class SuggestionQueueView : UserControl
{
    private readonly TextBox? _firstBox;
    private SuggestionQueueViewModel? _bound;

    public SuggestionQueueView()
    {
        AvaloniaXamlLoader.Load(this);
        _firstBox = this.FindControl<TextBox>("EditFromBox");

        // The editor's boxes: Enter saves and Esc cancels, from whichever box has focus.
        AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
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

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_bound is not { IsEditing: true } || e.Source is not TextBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _bound.SaveEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _bound.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SuggestionQueueViewModel.IsEditing) || _bound is null)
        {
            return;
        }

        // Posted: the same change makes the editor visible, and a hidden box cannot take focus.
        bool editing = _bound.IsEditing;
        Dispatcher.UIThread.Post(() =>
        {
            if (editing)
            {
                _firstBox?.Focus();
                _firstBox?.SelectAll();
            }
            else
            {
                ReturnFocus();
            }
        });
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
