#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.Views.Library;

/// <summary>
///     Demo-library landing tab view. Code-behind carries two view concerns: the double-click-to-open handler
///     (a card/list row carries its <see cref="DemoEntry" /> in <c>Tag</c>; double-tapping invokes the VM's
///     open command), and the drag-drop-a-.dem-to-open handlers which forward a dropped file's local
///     path to <see cref="LibraryTabViewModel.OpenPathCommand" /> — the same shared load core the recents /
///     browser use. Both just route to VM commands; no data is pushed into controls from here.
/// </summary>
public partial class LibraryTabView : UserControl
{
    // Card outer footprint: 196 width + 10 right margin (Border.demoCard); the ListBox itself
    // carries 12+12 horizontal padding and its vertical scrollbar overlays ~14px.
    private const double CardOuterWidth = 206;
    private const double CardGridChrome = 24 + 14;

    /// <summary>Initializes a new <see cref="LibraryTabView" /> instance and wires the file-drop handlers.</summary>
    public LibraryTabView()
    {
        InitializeComponent();

        // Receive-a-file drop (Avalonia 11.3 DragDrop attached events). AllowDrop is set on the root in XAML;
        // the handlers bubble up to the UserControl. This is the RECEIVE path (DataFormats.Files /
        // e.Data.GetFiles()), not the deprecated DoDragDrop source API.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDemoActivated(object? sender, TappedEventArgs e)
    {
        if (sender is Control { Tag: DemoEntry entry } && DataContext is LibraryTabViewModel vm)
        {
            vm.OpenEntryCommand.Execute(entry);
        }
    }

    // ── Drag-drop a .dem to open ────────────────────────────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not LibraryTabViewModel vm)
        {
            return;
        }

        bool accept = vm.CanDropFiles && FirstDemoPath(e) is not null;
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        vm.IsDragOver = accept;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DataContext is LibraryTabViewModel vm)
        {
            vm.IsDragOver = false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not LibraryTabViewModel vm)
        {
            return;
        }

        vm.IsDragOver = false;
        e.Handled = true;

        if (vm.CanDropFiles && FirstDemoPath(e) is { } path)
        {
            // Fire-and-forget via the command (starts the async task) — no async-void handler.
            vm.OpenPathCommand.Execute(path);
        }
    }

    // The local path of the first dropped .dem file, or null if the payload has none (or isn't files).
    // Uses the Avalonia 11.3 IDataTransfer receive API (DataFormat.File / TryGetFiles) — the successor to the
    // now-obsolete e.Data.GetFiles(); TryGetFiles is desktop-only, which suits the desktop drag-drop path.
    private static string? FirstDemoPath(DragEventArgs e)
    {
        IEnumerable<IStorageItem>? items = e.DataTransfer?.TryGetFiles();
        if (items is null)
        {
            return null;
        }

        foreach (IStorageItem item in items)
        {
            if (item is IStorageFile file
                && file.TryGetLocalPath() is { } local
                && local.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }
        }

        return null;
    }

    /// <summary>
    ///     Re-chunks the virtualized card grid when its viewport width changes: cards-per-row is a
    ///     view-model input (<see cref="LibraryTabViewModel.SetCardColumns" />), not a WrapPanel
    ///     measure, because the rows must be pre-chunked for virtualization.
    /// </summary>
    private void OnCardViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is LibraryTabViewModel vm)
        {
            vm.SetCardColumns((int)((e.NewSize.Width - CardGridChrome) / CardOuterWidth));
        }
    }
}
