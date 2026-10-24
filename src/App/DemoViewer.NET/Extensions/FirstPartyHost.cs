#region

using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     What the Strat Book reads of the app beyond the SDK: the zones overlay folder, which is the app's and not
///     a store the pack declares. The user's own work the pack keeps outside its folders is reached through
///     <see cref="IExtensionContext.Stores" />. Null in the browser build, which has no filesystem.
/// </summary>
public sealed class FirstPartyHost
{
    /// <summary>
    ///     The app's zones overlay folder, which the core 2D Playback tab reads too. Read only: the zone loader
    ///     takes a folder and reads each map's overlay file from it per call, so an edit shows without a restart.
    /// </summary>
    public string? ZonesDirectory { get; } = AppPaths.ZonesDirectory;
}
