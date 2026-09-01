#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     An App-layer accessor for the currently-loaded parsed demo. Implemented by the concrete
///     <see cref="ModuleContext" /> (which already references the parser) so a FIRST-PARTY module, one
///     that lives in the App assembly and may reference Parser/Analysis, can evaluate a ruleset against
///     the loaded demo, WITHOUT putting a Parser type on the deliberately-minimal
///     <see cref="Abstractions.IModuleContext" /> (its csproj keeps that boundary). A module reaches it by
///     testing <c>context is ICurrentDemoSource</c> in its <c>OnActivated</c>; test doubles opt in the
///     same way. Null until a demo is loaded.
/// </summary>
public interface ICurrentDemoSource
{
    /// <summary>The currently-loaded demo, or null when none is loaded.</summary>
    ParsedDemo? CurrentDemo { get; }
}
