#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Modules.StratBook;

/// <summary>
///     What the Strat Book tab needs for a strat export and cannot see through <c>IModuleContext</c>: the
///     machine-wide heavy-job gate, whether something else already owns the machine, the settings the dialog
///     seeds its folder from, and the shell's status-strip mount (step-authoring.md §3.6).
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
/// <param name="Gate">The heavy-job gate; the export takes its export-session kind on it.</param>
/// <param name="IsLiveSyncBusy">True while a Live Sync session is active.</param>
/// <param name="IsReelRunning">True while a highlight reel is rendering.</param>
/// <param name="Settings">Reads the current settings, for the dialog's output folder.</param>
/// <param name="PersistSettings">Writes the chosen output folder back.</param>
/// <param name="MountStatusChip">
///     Hands the export's status view-model to the shell for the status strip. The tab builds its job lazily,
///     on the first Export, so the shell supplies the mount point up front. Null leaves the export chip-less.
/// </param>
/// <param name="OpenExportFolder">Reveals a finished file in the OS file manager. Null on the browser head.</param>
public sealed record StratExportHost(
    HeavyJobGate? Gate,
    Func<bool>? IsLiveSyncBusy,
    Func<bool>? IsReelRunning,
    Func<AppSettings> Settings,
    Action<Action<AppSettings>> PersistSettings,
    Action<Playback2DExportStatusViewModel>? MountStatusChip = null,
    Action<string>? OpenExportFolder = null);
