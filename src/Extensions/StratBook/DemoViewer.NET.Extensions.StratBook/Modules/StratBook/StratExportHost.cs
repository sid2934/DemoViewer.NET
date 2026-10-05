#region

using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.StratBook;

/// <summary>
///     What the Strat Book tab needs for a strat export and cannot see through <c>IModuleContext</c>: the
///     machine-wide heavy-job gate, whether something else already owns the machine, the settings the dialog
///     seeds its folder from, and the shell's status-strip mount.
///     <para>
///         <c>Playback2DExportHost</c> minus <c>Frames</c>: a strat has no demo, its frames are
///         <c>StratFrameSource</c>'s. Everything else is the same shape because it is the same job, the same
///         gate and the same chip.
///     </para>
///     <para>
///         <b>Null means no export.</b> On the browser head, in tests and in the designer there is no host, and
///         the tab's Export button stays hidden.
///     </para>
/// </summary>
/// <param name="NewJob">Builds the export job over the app's export session for a runner and a log; null exports without one.</param>
/// <param name="IsLiveSyncBusy">True while a Live Sync session is active.</param>
/// <param name="IsReelRunning">True while a highlight reel is rendering.</param>
/// <param name="Exports">The app's export plumbing, which seeds the dialog's folder and saves the chosen one; null saves nothing.</param>
/// <param name="MountStatusChip">
///     Hands the export's status view-model to the shell for the status strip. The tab builds its job lazily,
///     on the first Export, so the shell supplies the mount point up front. Null leaves the export chip-less.
/// </param>
/// <param name="OpenExportFolder">Reveals a finished file in the OS file manager. Null on the browser head.</param>
public sealed record StratExportHost(
    Func<IExportRunner, Action<string>?, ExportJobService>? NewJob,
    Func<bool>? IsLiveSyncBusy,
    Func<bool>? IsReelRunning,
    FirstPartyExports? Exports,
    Action<Playback2DExportStatusViewModel>? MountStatusChip = null,
    Action<string>? OpenExportFolder = null) : IStratExport;
