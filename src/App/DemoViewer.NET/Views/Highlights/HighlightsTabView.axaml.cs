#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DemoViewer.NET.ViewModels.Highlights;

#endregion

namespace DemoViewer.NET.Views.Highlights;

/// <summary>
///     Reels dashboard view. Code-behind carries only view concerns: the responsive-collapse width probe
///     (root SizeChanged → <see cref="HighlightsTabViewModel.SetViewportWidth" />) and the tray's
///     drag-to-reorder gesture. No data is pushed into controls from here.
///     <para>
///         <b>Why drag lives in code-behind at all.</b> Avalonia's drag/drop is an input-event protocol, not a
///         bindable one — there is no `DragDrop` command surface to bind. The handler therefore does the
///         minimum possible: it reads the group key off the dragged/target container's <c>Tag</c> and calls one
///         view-model method (<see cref="IClipTrayHost.MoveGroupTo" />). All ordering logic stays in the VM,
///         and the same operation is reachable without a mouse through the ▲▼ buttons.
///     </para>
/// </summary>
public partial class HighlightsTabView : UserControl
{
    // The group key of the block currently being dragged (null when no drag is in flight). A field rather
    // than DataObject payload because Avalonia's DoDragDrop is async and the tray rebuilds under it.
    private string? _dragGroupKey;

    /// <summary>Builds the view.</summary>
    public HighlightsTabView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnTrayDrop);
        AddHandler(DragDrop.DragOverEvent, OnTrayDragOver);
    }

    private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is HighlightsTabViewModel vm)
        {
            vm.SetViewportWidth(e.NewSize.Width);
        }
    }

    // Inline job-strip "Copy error" — clipboard needs the visual tree, so it lives here rather than on the
    // VM. Copies the SAME full diagnostic block the flyout's Copy button does (JobStatus.CopyDiagnosticsText).
    private async void OnCopyReelErrorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HighlightsTabViewModel { JobStatus: { } jobStatus }
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(jobStatus.CopyDiagnosticsText);
        }
        catch (Exception)
        {
            // Clipboard writes are permission/gesture-gated on some hosts; swallow (the flyout's error text
            // is selectable as the manual fallback).
        }
    }

    // Escape dismisses the Add-clips overlay. A keystroke has no bindable command surface, and an overlay a
    // user can only leave with the mouse is a dead end on a dense list they may have scrolled deep into.
    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not HighlightsTabViewModel { IsPickerOpen: true } vm)
        {
            return;
        }

        vm.ClosePickerCommand.Execute(null);
        e.Handled = true;
    }

    // Click-outside dismisses. Handled here rather than as a transparent Button over the scrim so the picker
    // card itself (drawn ABOVE this layer) keeps every one of its own clicks.
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is HighlightsTabViewModel vm)
        {
            vm.ClosePickerCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnGroupPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only start a drag from the grip glyph itself, so pressing anywhere in a block does not begin one —
        // the block contains buttons and selectable text.
        if (sender is not Control { Tag: string groupKey } grip
            || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragGroupKey = groupKey;
        // The payload carries the key as text purely so the platform has something to drag; the handler
        // reads _dragGroupKey, because the tray rebuilds while the async gesture is in flight.
        DataTransfer payload = new();
        payload.Add(DataTransferItem.CreateText(groupKey));
        try
        {
            await DragDrop.DoDragDropAsync(e, payload, DragDropEffects.Move);
        }
        finally
        {
            _dragGroupKey = null;
        }
    }

    private void OnTrayDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = _dragGroupKey is null ? DragDropEffects.None : DragDropEffects.Move;

    private void OnTrayDrop(object? sender, DragEventArgs e)
    {
        if (_dragGroupKey is not { } dragged || DataContext is not HighlightsTabViewModel vm)
        {
            return;
        }

        // Walk up from whatever was hit to the nearest block that carries a tray position. The drop target is
        // frequently a TextBlock several levels down; without the walk a drop only registers on the block's
        // own background pixels, which is a gesture users read as broken.
        if (e.Source is Visual visual)
        {
            for (Visual? node = visual; node is not null; node = node.GetVisualParent())
            {
                if (node is Control { Tag: ReelClipGroupViewModel target })
                {
                    vm.MoveGroupTo(dragged, target.Position);
                    e.Handled = true;
                    return;
                }
            }
        }
    }
}
