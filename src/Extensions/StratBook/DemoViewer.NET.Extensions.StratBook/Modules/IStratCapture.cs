#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     Whether "Create Strat From Round" has anything to capture: the open demo's parse. Resolved through
///     <see cref="IModuleContext.GetService{T}" />; no registration at all (the pack off, or not built)
///     means the same as a null host.
/// </summary>
public interface IStratCapture
{
    /// <summary>The open demo's parse, or null while none is loaded.</summary>
    Func<ParsedDemo?> Demo { get; }
}
