#region

using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Services.Zones;

/// <summary>
///     The last zones-overlay load's diagnostics, held once for the whole app so the Rule Workbench
///     can show them beside the user-tier ruleset problems: the same surface an author already looks
///     at when a file they wrote was partly ignored.
///     <para>
///         A static hub rather than a service because its writer (the 2D Playback tab, constructed by a
///         module factory with no service provider) and its reader (the Workbench tab) meet nowhere
///         else, and the state is one list per process. Every member is safe from any thread;
///         <see cref="Changed" /> fires on the publishing thread and the reader marshals.
///     </para>
/// </summary>
public static class ZoneOverlayDiagnostics
{
    private static readonly Lock _lock = new();
    private static IReadOnlyList<ZoneDiagnostic> _current = [];
    private static string? _mapName;
    private static string? _overlayPath;

    /// <summary>Raised after every <see cref="Publish" />, on the publisher's thread.</summary>
    public static event Action? Changed;

    /// <summary>The diagnostics of the last load. Empty when the overlay applied cleanly or there was none.</summary>
    public static IReadOnlyList<ZoneDiagnostic> Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>The map the last load was for, or null.</summary>
    public static string? MapName
    {
        get
        {
            lock (_lock)
            {
                return _mapName;
            }
        }
    }

    /// <summary>The overlay file the last load looked for, or null.</summary>
    public static string? OverlayPath
    {
        get
        {
            lock (_lock)
            {
                return _overlayPath;
            }
        }
    }

    /// <summary>Replaces the current set. The 2D Playback tab calls this after every zones load.</summary>
    /// <param name="mapName">The map loaded.</param>
    /// <param name="overlayPath">The overlay file looked for, or null.</param>
    /// <param name="diagnostics">What the loader skipped or flagged.</param>
    public static void Publish(string? mapName, string? overlayPath, IReadOnlyList<ZoneDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        lock (_lock)
        {
            _mapName = mapName;
            _overlayPath = overlayPath;
            _current = diagnostics;
        }

        Changed?.Invoke();
    }

    /// <summary>Forgets the last load. A test seam; the app never needs it.</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _mapName = null;
            _overlayPath = null;
            _current = [];
        }

        Changed?.Invoke();
    }
}
