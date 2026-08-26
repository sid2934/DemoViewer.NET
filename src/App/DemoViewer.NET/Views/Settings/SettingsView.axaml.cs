#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.ViewModels.Settings;

#endregion

namespace DemoViewer.NET.Views.Settings;

// The Settings screen body, hosted by BOTH the desktop SettingsWindow and the WASM in-app overlay (the
// same view renders in both). The only code-behind is the storage-provider handoff for the folder picker,
// which needs the visual tree (TopLevel) and so cannot live in the view-model — mirrors MainView.
/// <summary>Settings view.</summary>
public partial class SettingsView : UserControl
{
    /// <summary>Initializes a new <see cref="SettingsView" /> instance.</summary>
    public SettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;

        // Keybind capture (D1). Registered on the ROOT and TUNNELLING, not on the capture button itself:
        // Button's own class handler claims Space and Enter before any handler attached to the button
        // would run, and those are two of the keys a user is most likely to bind. Tunnelling from here
        // also means the search box cannot swallow a captured letter. Inert unless a row is armed — the
        // view-model owns that state, and it allows only one armed row at a time.
        AddHandler(KeyDownEvent, OnKeybindCaptureKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnKeybindCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.HandleKeybindCapture(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        vm.SetStorageProvider(TopLevel.GetTopLevel(this)?.StorageProvider);

        // Deep link (v0.6.0): scroll a named section header into view once layout has run — posted at
        // Loaded priority because scrolling before the first arrange is a no-op.
        if (vm.ScrollTargetSection is { Length: > 0 } target && this.FindControl<Border>(target) is { } section)
        {
            Dispatcher.UIThread.Post(() => ScrollToSection(section), DispatcherPriority.Loaded);
        }
    }

    // Jump-chip click (v0.6.0 findability pass): the chip's Tag names a section header anchor.
    private void OnJumpChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target } && this.FindControl<Border>(target) is { } section)
        {
            ScrollToSection(section);
        }
    }

    // Scrolls a section anchor to the TOP of the viewport. BringIntoView would only nudge it
    // minimally into view — a below-the-fold section would surface with its header at the bottom
    // edge and its content still hidden, which reads as a broken jump. Since the sections live in
    // collapsible groups (v0.6.x), a collapsed ancestor Expander is expanded first and the scroll
    // re-posts at Loaded priority so the freshly-expanded content has a real layout position.
    private void ScrollToSection(Border section)
    {
        if (section.FindAncestorOfType<Expander>() is { IsExpanded: false } group)
        {
            group.IsExpanded = true;
            Dispatcher.UIThread.Post(() => ScrollToSectionCore(section), DispatcherPriority.Loaded);
            return;
        }

        ScrollToSectionCore(section);
    }

    private void ScrollToSectionCore(Border section)
    {
        if (this.FindControl<ScrollViewer>("SectionsScroll") is not { Content: Control content } scroll)
        {
            return;
        }

        if (section.TranslatePoint(new Point(0, 0), content) is { } position)
        {
            scroll.Offset = scroll.Offset.WithY(Math.Max(0, position.Y));
        }
    }
}
