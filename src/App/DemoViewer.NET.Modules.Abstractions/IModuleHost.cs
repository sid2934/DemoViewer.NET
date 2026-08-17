namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>Severity for <see cref="IModuleHost.Log" /> — routed to the shell's Output panel channel.</summary>
public enum ModuleLogLevel
{
    /// <summary>Diagnostic detail.</summary>
    Debug,

    /// <summary>Informational.</summary>
    Info,

    /// <summary>A recoverable problem.</summary>
    Warning,

    /// <summary>A failure (e.g. a circuit-breaker trip).</summary>
    Error
}

/// <summary>
///     The creation-time surface handed to <see cref="IWorkspaceModule.CreateTabs" />. Kept
///     separate from <see cref="IModuleContext" /> so the creation-time surface (context, capability
///     query, logging) is distinct from the runtime surface (clock + state).
/// </summary>
public interface IModuleHost
{
    /// <summary>The runtime read-only context the module's tabs subscribe to.</summary>
    IModuleContext Context { get; }

    /// <summary>Whether this module was granted a capability.</summary>
    bool HasCapability(string capability);

    /// <summary>Routes a message to the shell's Output panel channel.</summary>
    void Log(ModuleLogLevel level, string message);
}
