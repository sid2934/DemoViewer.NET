#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.Views;

// The shell: real TabControl + SplitView debugger rail + StatusStrip. The "Parse Chain"
// button binds to OpenParseChainInspectorCommand, which routes through IWindowService
// (DesktopWindowService opens the window; BrowserWindowService no-ops). The only
// remaining code-behind is the storage-provider handoff + DEBUG auto-load + the global
// idle-activity input hook, which need the visual tree (TopLevel) and so can't move to the VM.
/// <summary>Main view.</summary>
public partial class MainView : UserControl
{
    private TopLevel? _idleActivityTop;

    /// <summary>Initializes a new <see cref="MainView" /> instance.</summary>
    public MainView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        TopLevel? top = TopLevel.GetTopLevel(this);
        vm.SetStorageProvider(top?.StorageProvider);

        // Idle-activity hook (desktop). ONE set of TUNNELING handlers on the window's TopLevel catches every
        // pointer / key / wheel interaction before any control handles it, regardless of which control is
        // the target, so ANY user interaction resets the idle countdown with no per-control wiring.
        // handledEventsToo:true so an already-handled event still counts as activity. The handler does a
        // single field write (MainViewModel.NotifyIdleActivity → IdleController.NotifyActivity).
        if (top is not null && !ReferenceEquals(top, _idleActivityTop))
        {
            _idleActivityTop = top;
            top.AddHandler(PointerMovedEvent, OnIdleActivity, RoutingStrategies.Tunnel, true);
            top.AddHandler(PointerPressedEvent, OnIdleActivity, RoutingStrategies.Tunnel, true);
            top.AddHandler(PointerWheelChangedEvent, OnIdleActivity, RoutingStrategies.Tunnel, true);
            top.AddHandler(KeyDownEvent, OnIdleActivity, RoutingStrategies.Tunnel, true);
        }

#if DEBUG
        string? autoLoadPath = Environment.GetEnvironmentVariable("DEMO_PATH");
        if (!string.IsNullOrEmpty(autoLoadPath) && File.Exists(autoLoadPath))
        {
            _ = vm.AutoLoadDemoAsync(autoLoadPath);
        }
#endif
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_idleActivityTop is not { } top)
        {
            return;
        }

        top.RemoveHandler(PointerMovedEvent, OnIdleActivity);
        top.RemoveHandler(PointerPressedEvent, OnIdleActivity);
        top.RemoveHandler(PointerWheelChangedEvent, OnIdleActivity);
        top.RemoveHandler(KeyDownEvent, OnIdleActivity);
        _idleActivityTop = null;
    }

    private void OnIdleActivity(object? sender, RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.NotifyIdleActivity();
}
