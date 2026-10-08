#region

using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.StratBook;

/// <summary>
///     <see cref="StratExportHost" />'s shape as an interface, so <see cref="DemoViewer.NET.Modules.Abstractions.IModuleContext.GetService{T}" />
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

    /// <inheritdoc cref="StratExportHost.Exports" />
    FirstPartyExports? Exports { get; }

    /// <inheritdoc cref="StratExportHost.MountStatusChip" />
    Action<Playback2DExportStatusViewModel>? MountStatusChip { get; }

    /// <inheritdoc cref="StratExportHost.OpenExportFolder" />
    Action<string>? OpenExportFolder { get; }
}
