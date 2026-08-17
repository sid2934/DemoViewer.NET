#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Models;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Best-effort persistence for the UI <see cref="SessionPayload" />.
///     <para>
///         Persistence is delegated to <see cref="SettingsService" />: the session-restore snapshot is the
///         <c>Session</c> section of the single consolidated config file (formerly the standalone
///         <c>session.json</c>). A <c>null</c> settings service (the WASM/browser sandbox — no filesystem —
///         or the designer / older-test path) makes every method a no-op: the session simply isn't persisted
///         (it survives in-memory for the life of the process). A runtime check is used rather than a
///         <c>#if BROWSER</c> define because the same <c>DemoViewer.NET</c> assembly is compiled once and
///         shared by both the desktop and browser hosts; mirrors <see cref="BookmarkStore" />.
///     </para>
///     <para>
///         <b>Write cadence.</b> The session is saved at shutdown/teardown only (never per-tab-switch), and
///         <see cref="SettingsService.SaveSession" /> writes it without reloading the configuration, so a
///         save raises no synchronous <c>IOptionsMonitor.OnChange</c> and never thrashes the feature gate.
///     </para>
/// </summary>
public sealed class SessionStore
{
    // The single serializer of the consolidated config file (owns the Session section). Null → no-op.
    private readonly SettingsService? _settings;

    /// <summary>
    ///     Constructs over the consolidated-config serializer that owns the <c>Session</c> section.
    ///     The real app passes the singleton <see cref="SettingsService" />; a temp-dir-backed one is the
    ///     test seam. <c>null</c> → no persistence (the designer / older-test path, and the WASM no-op).
    /// </summary>
    public SessionStore(SettingsService? settings = null) => _settings = settings;

    /// <summary>Loads the persisted session, or <c>null</c> if none exists / unavailable.</summary>
    public SessionPayload? Load() => _settings?.LoadSession();

    /// <summary>Persists <paramref name="payload" />. No-op when there is no settings service.</summary>
    public void Save(SessionPayload payload) => _settings?.SaveSession(payload);
}
