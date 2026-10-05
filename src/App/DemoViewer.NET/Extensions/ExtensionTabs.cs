#region

using Avalonia.Controls;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The shell's calls into tab descriptors, guarded when the descriptor is an extension's. An extension
///     module's tabs are copied once, as the shell collects them, with view-model and view factories that
///     fall back to a placeholder; activation, deactivation and the session snapshot then run as the
///     extension's. A descriptor the host built itself runs unguarded, so a host bug still surfaces.
/// </summary>
public static class ExtensionTabs
{
    /// <summary>
    ///     The tabs <paramref name="module" /> contributes. An extension's module is asked under its guard, and
    ///     each tab is a guarded copy; a host module is asked directly.
    /// </summary>
    public static IReadOnlyList<WorkspaceTabDescriptor> CreateTabs(IWorkspaceModule module, IModuleHost host)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (ExtensionGuards.For(module) is not { } guard)
        {
            return [.. module.CreateTabs(host)];
        }

        IReadOnlyList<WorkspaceTabDescriptor> tabs =
            guard.Run<IReadOnlyList<WorkspaceTabDescriptor>>("create tabs", () => [.. module.CreateTabs(host)], []);
        return [.. tabs.Select(t => Guarded(t, guard))];
    }

    /// <summary>
    ///     A copy of <paramref name="tab" /> whose factories run under <paramref name="guard" />. The copy follows
    ///     the original's <see cref="WorkspaceTabDescriptor.Badge" />, which the module keeps setting.
    /// </summary>
    public static WorkspaceTabDescriptor Guarded(WorkspaceTabDescriptor tab, ExtensionGuard guard)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(guard);
        Func<IWorkspaceTabViewModel>? viewModel = tab.ViewModelFactory;
        Func<Control> view = tab.ViewFactory;

        // A failed view model shows the placeholder rather than the extension's view over nothing.
        bool viewModelFailed = false;
        WorkspaceTabDescriptor copy = new()
        {
            TabId = tab.TabId,
            Header = tab.Header,
            Badge = tab.Badge,
            Icon = tab.Icon,
            Order = tab.Order,
            Placement = tab.Placement,
            HostId = tab.HostId,
            FeatureId = tab.FeatureId,
            DataContext = tab.DataContext,
            ViewModelFactory = viewModel is null
                ? null
                : () =>
                {
                    IWorkspaceTabViewModel? built = guard.Run<IWorkspaceTabViewModel?>("tab view model", () => viewModel(), null);
                    viewModelFailed = built is null;
                    return built!;
                },
            ViewFactory = () => viewModelFailed
                ? ExtensionPlaceholder.View(guard.Scope, "this tab")
                : guard.Run("tab view", view, ExtensionPlaceholder.View(guard.Scope, "this tab"))
        };

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceTabDescriptor.Badge))
            {
                copy.Badge = tab.Badge;
            }
        };
        ExtensionGuards.Register(copy, guard);
        return copy;
    }

    /// <summary>Activates <paramref name="tab" />, as its extension's when it has one.</summary>
    public static void Activate(WorkspaceTabDescriptor tab, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (ExtensionGuards.For(tab) is { } guard)
        {
            guard.Run("tab activate", () => tab.Activate(context));
        }
        else
        {
            tab.Activate(context);
        }
    }

    /// <summary>Deactivates <paramref name="tab" />, as its extension's when it has one.</summary>
    public static void Deactivate(WorkspaceTabDescriptor? tab)
    {
        if (tab is null)
        {
            return;
        }

        if (ExtensionGuards.For(tab) is { } guard)
        {
            guard.Run("tab deactivate", tab.Deactivate);
        }
        else
        {
            tab.Deactivate();
        }
    }

    /// <summary>The session state of <paramref name="tab" />'s view model, or null; an extension's throw reads as null.</summary>
    public static object? Snapshot(WorkspaceTabDescriptor tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.TabViewModel is not { } viewModel)
        {
            return null;
        }

        return ExtensionGuards.For(tab) is { } guard
            ? guard.Run("tab session snapshot", viewModel.SnapshotState, null)
            : viewModel.SnapshotState();
    }
}
