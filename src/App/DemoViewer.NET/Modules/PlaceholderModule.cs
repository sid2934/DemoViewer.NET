#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     A trivial no-op module that proves the framework end-to-end (registration → activation →
///     inactive-zero-cost) WITHOUT building a real viewport — that is the 2D
///     pilot's job. Its single tab subscribes to <c>IModuleContext.Advanced</c> on activation, counts
///     pushes, and unsubscribes on deactivation so it does zero per-tick work while inactive. The
///     count proves pushes accrue only while this tab is the active tab.
/// </summary>
public sealed class PlaceholderModule : IWorkspaceModule
{
    public string Id => "net.demoviewer.placeholder";
    public string DisplayName => "Module Sandbox";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        PlaceholderTabViewModel vm = new();

        yield return new WorkspaceTabDescriptor
        {
            TabId = "placeholder.sandbox",
            Header = "Sandbox",
            Order = 100,
            Placement = TabPlacement.Diagnostics,
            // ViewModelFactory (not DataContext): Activate only populates TabViewModel — and
            // only calls OnActivated — through the factory. The DataContext form left
            // TabViewModel null and skipped the whole module lifecycle (the exact trap
            // Playback2DModule's header comment documents); masked for months by the
            // vacuous-pass harness bug, exposed by honest awaiting.
            ViewModelFactory = () => vm,
            ViewFactory = BuildView
        };
    }

    // The binding resolves against the View's DataContext (the descriptor sets it to the VM).
    private static Border BuildView()
    {
        TextBlock text = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Gray,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Consolas,Menlo,monospace")
        };
        // One-way bind to the VM's status so the push counter is visible.
        text.Bind(TextBlock.TextProperty, new Binding(nameof(PlaceholderTabViewModel.Status)));
        return new Border
        {
            Padding = new Thickness(24),
            Child = text
        };
    }
}
