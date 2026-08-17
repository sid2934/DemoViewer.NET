namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     The per-tab view-model contract. Lifecycle is gated on
///     <see cref="OnActivated" />/<see cref="OnDeactivated" /> (selection), NOT View
///     <c>Loaded</c>/<c>Unloaded</c> (a non-deterministic rendering concern). The host calls
///     <see cref="OnActivated" /> when the descriptor becomes the selected tab and
///     <see cref="OnDeactivated" /> when it stops being so.
/// </summary>
public interface IWorkspaceTabViewModel
{
    /// <summary>Called when the tab becomes selected. Subscribe to clock pushes here.</summary>
    void OnActivated(IModuleContext context);

    /// <summary>Called when another tab is selected. Unsubscribe; do zero per-tick work after this.</summary>
    void OnDeactivated();

    /// <summary>
    ///     Optional state to carry across restarts. <b>Must be JSON-serializable</b> — the shell serializes it
    ///     into the session file. Return null (the default) to persist nothing.
    ///     <para>
    ///         Only called on tabs whose VM already exists: a tab the user never opened is never built just to
    ///         be snapshotted.
    ///     </para>
    /// </summary>
    object? SnapshotState() => null;

    /// <summary>
    ///     Restores a snapshot, applied once when this tab's VM is first built.
    ///     <para>
    ///         Careful: <paramref name="state" /> arrives as a <c>System.Text.Json.JsonElement</c>, NOT as the type
    ///         <see cref="SnapshotState" /> returned — it has been through the session file. Deserialize it
    ///         (<c>((JsonElement)state).Deserialize&lt;T&gt;()</c>) rather than casting, and tolerate a shape
    ///         that no longer matches: session state is a convenience, never a source of truth, so a blob
    ///         written by an older build must degrade to "restore nothing" instead of throwing on startup.
    ///     </para>
    /// </summary>
    /// <param name="state">The persisted blob as a <c>JsonElement</c>, or null when there is none.</param>
    void RestoreState(object? state)
    {
    }
}
