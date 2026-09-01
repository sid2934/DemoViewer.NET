namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     The contribution contract. A module returns one or more
///     <see cref="WorkspaceTabDescriptor" />s. The descriptor, not the module, is the unit of
///     placement. Implemented by the first-party <c>BuiltInTabsModule</c> (the four existing tabs) and,
///     later, by third-party runtime-loaded modules (the 2D pilot).
/// </summary>
public interface IWorkspaceModule
{
    /// <summary>
    ///     Stable, unique id (reverse-DNS recommended, e.g. <c>"net.demoviewer.playback2d"</c>). Used
    ///     for registration de-dup, session-persistence keys, and capability grants.
    /// </summary>
    string Id { get; }

    /// <summary>Human title shown if the module's own tabs don't override it.</summary>
    string DisplayName { get; }

    /// <summary>Semantic version of the CONTRACT this module was built against.</summary>
    Version ContractVersion { get; }

    /// <summary>
    ///     Produces the tabs this module contributes. Usually one; may be many. Called once at shell
    ///     init (first-party) or post-load (third-party).
    /// </summary>
    IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host);
}
