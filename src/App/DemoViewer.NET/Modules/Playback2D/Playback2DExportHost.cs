#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The things the 2D tab needs for a video export and cannot see through <c>IModuleContext</c>: the
///     parsed frame list, the export plumbing, and whether something else already owns the machine.
///     <para>
///         It is not on <c>IModuleContext</c> because that interface deliberately exposes no
///         <c>EntityTracker</c>, no raw buffer and no parser: a module has no API to corrupt state. An
///         export is a first-party capability the shell hands the 2D tab explicitly, the same way it
///         hands it the live-sync HUD projection and the speed lock.
///     </para>
///     <para>
///         <b>Null means no export.</b> On the browser head, in tests and in the designer there is no
///         host, the tab's Export affordance stays hidden, and nothing half-wired is offered. A host whose
///         <see cref="Exports" /> is null hides it for the same reason.
///     </para>
/// </summary>
/// <param name="Frames">
///     The immutable post-parse frame list. Read-only and shared safely: the export walks it with its
///     own private tracker while the app's tracker walks the same list.
/// </param>
/// <param name="Exports">
///     The app's export plumbing: the job over the heavy-job gate's export session, and the settings the
///     dialog seeds its defaults from and writes back. The same object the first-party extensions render
///     through, so there is one home for the gate and the export choices.
/// </param>
/// <param name="IsLiveSyncBusy">
///     True while a Live Sync session is active OR still owns its resources. A predicate rather than a
///     reference because <c>LiveSyncService.OwnsSessionResources</c> is internal to the desktop-only
///     LiveSync project.
/// </param>
/// <param name="IsReelRunning">True while a highlight reel is rendering.</param>
/// <param name="MountStatusChip">
///     Hands the export's status view-model to the shell, which reconciles its chip into the status strip.
///     <para>
///         The tab builds the job lazily, on the first Export, and the shell is constructed long before
///         any module tab exists, so the chip cannot be attached at composition the way the reel's is.
///         This is the one direction that works: the shell supplies the mount point up front and the tab
///         calls it when it finally has something to mount. Null (browser, tests, designer) simply leaves
///         the export chip-less; the job still runs.
///     </para>
/// </param>
/// <param name="OpenExportFolder">Reveals a finished file in the OS file manager. Null on the browser head.</param>
public sealed record Playback2DExportHost(
    Func<IReadOnlyList<DemoFrame>?> Frames,
    FirstPartyExports? Exports,
    Func<bool>? IsLiveSyncBusy,
    Func<bool>? IsReelRunning,
    Action<Playback2DExportStatusViewModel>? MountStatusChip = null,
    Action<string>? OpenExportFolder = null);
