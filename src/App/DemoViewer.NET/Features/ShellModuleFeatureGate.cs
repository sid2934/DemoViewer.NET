#region

using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Features;

/// <summary>
///     The shell's <see cref="IModuleFeatureGate" /> projection over the singleton <see cref="IFeatureGate" />
///     — the ONE place module-facing feature ids are resolved, and the ONE
///     <c>!OperatingSystem.IsBrowser()</c> AND site for them (<see cref="DesktopOnlyIds" />). A module reads
///     <c>IModuleContext.Features</c> and never re-derives platform conditions, exactly as the shell's own
///     <c>chrome.livesync</c> / <c>chrome.processingQueue</c> surfaces do.
///     <para>
///         <b>Fails open.</b> A null underlying gate (designer / unit-test path) answers <c>true</c>, matching
///         <c>MainViewModel.IsTabEnabled</c>'s documented null-gate behaviour.
///     </para>
/// </summary>
public sealed class ShellModuleFeatureGate : IModuleFeatureGate, IDisposable
{
    private readonly IFeatureGate? _gate;

    /// <summary>Wraps the shell gate; a null gate fails open for every id.</summary>
    public ShellModuleFeatureGate(IFeatureGate? gate)
    {
        _gate = gate;
        if (_gate is not null)
        {
            _gate.Changed += OnGateChanged;
        }
    }

    /// <summary>
    ///     Module feature ids that additionally require a desktop head — the ONE
    ///     <c>!OperatingSystem.IsBrowser()</c> AND site for module-facing ids (B5 D4). A phase that needs a
    ///     desktop-only gate adds its id here and nowhere else; a second shim would be a second answer to
    ///     the same question.
    /// </summary>
    public static IReadOnlySet<string> DesktopOnlyIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Video export writes a file and drives an ffmpeg subprocess. The WASM head has no filesystem and
        // no System.Diagnostics.Process, so the feature cannot exist there whatever the user's override
        // says (B4.13).
        "playback2d.export"
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_gate is not null)
        {
            _gate.Changed -= OnGateChanged;
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(string featureId)
    {
        if (featureId is null)
        {
            return true;
        }

        if (DesktopOnlyIds.Contains(featureId) && OperatingSystem.IsBrowser())
        {
            return false;
        }

        return _gate?.IsEnabled(featureId) ?? true;
    }

    /// <inheritdoc />
    public event Action? Changed;

    private void OnGateChanged(object? sender, EventArgs e) => Changed?.Invoke();
}
