#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Modules.StratBook;

/// <summary>
///     What 2D Playback needs for Create Strat From Round (step-authoring.md §3.9) and cannot see through
///     <c>IModuleContext</c>: the parsed demo, the strat store the new strat is committed to, Team Identity for our
///     side and the book, and the way into the Strat Book tab once the strat exists.
///     <para>
///         <c>Playback2DExportHost</c>'s shape and reason: <c>IModuleContext</c> exposes no parser and no store, so
///         the shell hands the tab this first-party capability explicitly. Unlike the export it works on the browser
///         head too, session only, since the parsed demo is in memory and the store is in-memory there.
///     </para>
///     <para><b>Null means no capture.</b> In tests and the designer there is no host and the round band offers nothing.</para>
/// </summary>
/// <param name="Demo">The open demo's parse, or null while none is loaded. Read-only: the capture walks it with its own tracker.</param>
/// <param name="Store">The strat store; the new strat is committed there as revision 1.</param>
/// <param name="Teams">Team Identity, for our side and the book; null offers the side picker and the <c>me</c> book.</param>
/// <param name="OpenStrat">Shows the Strat Book tab with the new strat open. Null leaves the user where they are.</param>
public sealed record StratCaptureHost(
    Func<ParsedDemo?> Demo,
    StratStore Store,
    TeamIdentityService? Teams,
    Action<Guid>? OpenStrat = null);
