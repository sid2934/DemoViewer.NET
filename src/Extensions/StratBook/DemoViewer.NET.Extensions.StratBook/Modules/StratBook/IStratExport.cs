#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Modules.StratBook;

/// <summary>
///     <see cref="StratExportHost" />'s shape as an interface, so <see cref="ModuleContext.GetService{T}" />
///     resolves it by type. Every member is a core type; nothing here is pack-specific beyond the fact
///     that the Strat Book pack is what wires it.
/// </summary>
public interface IStratExport
{
    /// <inheritdoc cref="StratExportHost.NewJob" />
    Func<IExportRunner, Action<string>?, ExportJobService>? NewJob { get; }

    /// <inheritdoc cref="StratExportHost.IsLiveSyncBusy" />
    Func<bool>? IsLiveSyncBusy { get; }

    /// <inheritdoc cref="StratExportHost.IsReelRunning" />
    Func<bool>? IsReelRunning { get; }

    /// <inheritdoc cref="StratExportHost.Settings" />
    Func<AppSettings> Settings { get; }

    /// <inheritdoc cref="StratExportHost.PersistSettings" />
    Action<Action<AppSettings>> PersistSettings { get; }

    /// <inheritdoc cref="StratExportHost.MountStatusChip" />
    Action<Playback2DExportStatusViewModel>? MountStatusChip { get; }

    /// <inheritdoc cref="StratExportHost.OpenExportFolder" />
    Action<string>? OpenExportFolder { get; }
}
