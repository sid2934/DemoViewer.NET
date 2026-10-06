using System.Reflection;
using System.Runtime.CompilerServices;
using CS2DemoKit.Parser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     The host as one extension sees it. Resolve it with
///     <see cref="ExtensionServiceProviderExtensions.GetExtensionContext" />; every member is safe to call from
///     a factory registered in <see cref="IExtension.Register" />.
/// </summary>
public interface IExtensionContext
{
    /// <summary>The extension this context belongs to.</summary>
    string ExtensionId { get; }

    /// <summary>The extension's master switch.</summary>
    string FeatureId { get; }

    /// <summary>The running host.</summary>
    HostInfo Host { get; }

    /// <summary>The feature switches, live.</summary>
    IExtensionFeatures Features { get; }

    /// <summary>The processing queue every off-UI-thread job goes through.</summary>
    IExtensionJobs Jobs { get; }

    /// <summary>The scheduling of the extension's own passes.</summary>
    IExtensionPasses Passes { get; }

    /// <summary>The open demo and the shell's navigation.</summary>
    IExtensionShell Shell { get; }

    /// <summary>The extension's own files.</summary>
    IExtensionStorage Storage { get; }

    /// <summary>The extension's own settings, which a <see cref="SettingsSchema" /> page edits.</summary>
    IExtensionSettings Settings { get; }

    /// <summary>The extension's per-demo data, kept by the host.</summary>
    IExtensionDemoData Data { get; }

    /// <summary>The demo library, read only.</summary>
    IExtensionLibrary Library { get; }

    /// <summary>Short messages to the user, shown in a stack above the status strip for the session.</summary>
    IExtensionNotifications Notifications { get; }

    /// <summary>The keymap as shipped, read only.</summary>
    IExtensionKeymap Keymap { get; }

    /// <summary>A logger whose lines land in the app's diagnostics log, tagged with the extension.</summary>
    ILogger CreateLogger(string category);

    /// <summary>Runs <paramref name="action" /> on the UI thread.</summary>
    void Post(Action action);
}

/// <summary>The running host.</summary>
/// <param name="SdkVersion">The contract version the host implements.</param>
/// <param name="AppVersion">The app's release version, or null for a developer build.</param>
/// <param name="IsBrowser">True in the browser build: no filesystem, no processing queue, no ffmpeg.</param>
public sealed record HostInfo(Version SdkVersion, string? AppVersion, bool IsBrowser);

/// <summary>The feature switches: the extension's own and the host's.</summary>
public interface IExtensionFeatures
{
    /// <summary>
    ///     Whether <paramref name="featureId" /> resolves on: the user's choice, its parents and the user's
    ///     category all counted. An extension's features answer false while the extension is off.
    /// </summary>
    bool IsEnabled(string featureId);

    /// <summary>Raised on the UI thread when any switch may have moved.</summary>
    event Action? Changed;
}

/// <summary>The open demo and the shell's navigation. Call from the UI thread.</summary>
public interface IExtensionShell
{
    /// <summary>The path of the demo the shell has open, or null.</summary>
    string? CurrentDemoPath { get; }

    /// <summary>Raised on the UI thread after the open demo changes.</summary>
    event Action? CurrentDemoChanged;

    /// <summary>
    ///     The open demo's parse, or null when none is open. Read it, never change it: the shell and every
    ///     tab share this one instance. Drop any reference to it on <see cref="CurrentDemoChanged" />, since a
    ///     held parse keeps the whole demo in memory after it is closed.
    /// </summary>
    ParsedDemo? CurrentDemo { get; }

    /// <summary>
    ///     The open demo's content key, lowercase hex SHA-256 of the file's bytes, or null when none is open.
    ///     The same key survives a rename or a move, so it is the one to store per-demo data under.
    /// </summary>
    string? CurrentDemoHash { get; }

    /// <summary>The server tick at the playhead, or 0 when no demo is open.</summary>
    int CurrentTick { get; }

    /// <summary>True while playback is running.</summary>
    bool IsPlaying { get; }

    /// <summary>
    ///     Raised on the UI thread after <see cref="CurrentTick" /> or <see cref="IsPlaying" /> moves, at most
    ///     once per rendered frame. A handler runs during playback, so it must be cheap.
    /// </summary>
    event Action? PlayheadChanged;

    /// <summary>Opens <paramref name="path" /> through the shell's own load. True when it is open afterwards.</summary>
    Task<bool> OpenDemoAsync(string path);

    /// <summary>Moves the shared playback clock to <paramref name="tick" />.</summary>
    void SeekToTick(int tick);

    /// <summary>Selects a tab or a section by its id. False when no such tab is showing.</summary>
    bool SelectTab(string tabId);

    /// <summary>Shows <paramref name="path" /> in the system file manager.</summary>
    void RevealInFileManager(string path);
}

/// <summary>
///     Files of the extension's own, in two folders the host keeps for it: one for the user's work, one for
///     what the extension can rebuild. The extension names a file by its path under the folder and never sees
///     where the folder is. The browser build has no folders: writes keep nothing and reads find nothing.
/// </summary>
public interface IExtensionStorage
{
    /// <summary>
    ///     Writes <paramref name="content" /> to a file under one of the extension's folders, whole or not at
    ///     all: the bytes go to a temporary file beside the target, which then replaces it. A crash mid-write
    ///     leaves the previous file. The safe way to write the extension's data.
    /// </summary>
    /// <param name="root">Which of the extension's folders.</param>
    /// <param name="relativePath">The file under that folder, with '/' or '\' between folders. Missing folders are created.</param>
    /// <param name="content">The file's new contents.</param>
    /// <param name="cancellationToken">Stops the write before the file is replaced.</param>
    /// <returns>False on the browser build, which has no folders; true once the file is replaced.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="relativePath" /> is empty, rooted, has a ".." segment, resolves outside the folder, or
    ///     enters the cache folder's <c>demo-data</c> folder, which holds <see cref="IExtensionContext.Data" />.
    /// </exception>
    Task<bool> WriteAtomicAsync(StoreRoot root, string relativePath, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a file under one of the extension's folders, by the same path rule as <see cref="WriteAtomicAsync" />.</summary>
    /// <param name="root">Which of the extension's folders.</param>
    /// <param name="relativePath">The file under that folder.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The file's contents, or null when it does not exist or the build has no folders.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="relativePath" /> is empty, rooted, has a ".." segment, or resolves outside the folder.
    /// </exception>
    Task<byte[]?> ReadAsync(StoreRoot root, string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>Resolves an extension's <see cref="IExtensionContext" />.</summary>
public static class ExtensionServiceProviderExtensions
{
    /// <summary>
    ///     The context the host registered for <paramref name="extensionId" />. Only that extension's own code
    ///     may resolve it: asking for another extension's context throws.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="extensionId">Your extension's id.</param>
    /// <exception cref="InvalidOperationException">
    ///     No extension has that id, or the calling code belongs to a different extension.
    /// </exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IExtensionContext GetExtensionContext(this IServiceProvider services, string extensionId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        Assembly caller = Assembly.GetCallingAssembly();
        return services.GetService<IExtensionContextAccess>() is { } access
            ? access.Resolve(extensionId, caller)
            : services.GetRequiredKeyedService<IExtensionContext>(new ExtensionContextKey(extensionId));
    }
}

/// <summary>
///     The key the host registers each context under. A lookup by the plain id string finds nothing, so
///     <see cref="ExtensionServiceProviderExtensions.GetExtensionContext" /> and its owner check are the only way in.
/// </summary>
/// <param name="ExtensionId">The extension's id.</param>
internal sealed record ExtensionContextKey(string ExtensionId);

/// <summary>
///     The host's check on <see cref="ExtensionServiceProviderExtensions.GetExtensionContext" />: resolves the
///     context only for code that belongs to that extension, or to no extension.
/// </summary>
internal interface IExtensionContextAccess
{
    /// <summary>The context of <paramref name="extensionId" />, asked for by code in <paramref name="caller" />.</summary>
    IExtensionContext Resolve(string extensionId, Assembly caller);
}
