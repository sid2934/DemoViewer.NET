#region

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DemoViewer.NET.Modules.RoundTagger.Palette;

#endregion

namespace DemoViewer.NET.Views.RoundTagger;

/// <summary>
///     The Tag Palette's view, docked by the 2D Playback view with the tab's <see cref="TagPaletteViewModel" />
///     as its DataContext. Keys never start here: the 2D view's tunnelling handler routes them through the
///     VM. What this owns is the note box's focus, which moves in when a note is begun and goes back to
///     the 2D view when it ends, so the keyboard never strands in a hidden text box.
/// </summary>
public partial class TagPaletteView : UserControl
{
    private readonly TextBox? _noteBox;
    private TagPaletteViewModel? _bound;

    public TagPaletteView()
    {
        AvaloniaXamlLoader.Load(this);
        _noteBox = this.FindControl<TextBox>("NoteBox");
        _noteBox?.AddHandler(KeyDownEvent, OnNoteKeyDown, RoutingStrategies.Tunnel);

        // A click on the palette gives it the keyboard: the mouse path to what the focus key does.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _bound = DataContext as TagPaletteViewModel;
        if (_bound is not null)
        {
            _bound.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => _bound?.Focus();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TagPaletteViewModel.IsEditingNote) || _bound is null)
        {
            return;
        }

        // Posted: the same property change makes the box visible, and focusing a control that is not
        // visible yet does nothing.
        bool editing = _bound.IsEditingNote;
        Dispatcher.UIThread.Post(() =>
        {
            if (editing)
            {
                _noteBox?.Focus();
                _noteBox?.SelectAll();
            }
            else
            {
                ReturnFocus();
            }
        });
    }

    private void OnNoteKeyDown(object? sender, KeyEventArgs e)
    {
        if (_bound?.TryHandleNoteKey(e.Key) == true)
        {
            e.Handled = true;
        }
    }

    // The keymap lives on the 2D view, the nearest focusable ancestor; handing focus back to it is what
    // lets the next palette key work without the mouse.
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
