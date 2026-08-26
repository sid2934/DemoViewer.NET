#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The three things the 2D tab needs for a video export and cannot see through
///     <c>IModuleContext</c>: the parsed frame list, the machine-wide heavy-job gate, and whether
///     something else already owns the machine.
///     <para>
///         <b>Why it is not on <c>IModuleContext</c>.</b> That interface deliberately exposes no
///         <c>EntityTracker</c>, no raw buffer and no parser — a module simply has no API to corrupt
///         state, and that guardrail is worth more than this convenience. An export is a first-party
///         capability the shell hands the 2D tab explicitly, the same way it hands it the live-sync HUD
///         projection and the speed lock.
///     </para>
///     <para>
///         <b>Null means no export.</b> On the browser head, in tests and in the designer there is no
///         host, the tab's Export affordance stays hidden, and nothing half-wired is offered.
///     </para>
/// </summary>
/// <param name="Frames">
///     The immutable post-parse frame list. Read-only and shared safely: the export walks it with its
///     own private tracker while the app's tracker walks the same list.
/// </param>
/// <param name="Gate">
///     The heavy-job gate. The export takes its own session kind on it, which pauses background parses
///     and refuses a reel — see <c>HeavyJobGate.EnterExportSessionAsync</c>.
/// </param>
/// <param name="IsLiveSyncBusy">
///     True while a Live Sync session is active OR still owns its resources. A predicate rather than a
///     reference because <c>LiveSyncService.OwnsSessionResources</c> is internal to the desktop-only
///     LiveSync project.
/// </param>
/// <param name="IsReelRunning">True while a highlight reel is rendering.</param>
/// <param name="Settings">Reads the current settings, for the dialog's defaults.</param>
/// <param name="PersistSettings">Writes the chosen defaults back.</param>
/// <param name="MountStatusChip">
///     Hands the export's status view-model to the shell, which reconciles its chip into the status strip.
///     <para>
///         The tab builds the job lazily — on the first Export — and the shell is constructed long before
///         any module tab exists, so the chip cannot be attached at composition the way the reel's is.
///         This is the one direction that works: the shell supplies the mount point up front and the tab
///         calls it when it finally has something to mount. Null (browser, tests, designer) simply leaves
///         the export chip-less; the job still runs.
///     </para>
/// </param>
/// <param name="OpenExportFolder">Reveals a finished file in the OS file manager. Null on the browser head.</param>
public sealed record Playback2DExportHost(
    Func<IReadOnlyList<DemoFrame>?> Frames,
    HeavyJobGate? Gate,
    Func<bool>? IsLiveSyncBusy,
    Func<bool>? IsReelRunning,
    Func<AppSettings> Settings,
    Action<Action<AppSettings>> PersistSettings,
    Action<ViewModels.Playback2D.Playback2DExportStatusViewModel>? MountStatusChip = null,
    Action<string>? OpenExportFolder = null);
