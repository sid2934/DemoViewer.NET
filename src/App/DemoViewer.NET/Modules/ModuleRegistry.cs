#region

using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     First-party static module registry. Populated by the composition root before the shell
///     is constructed; a plain list (no MS.DI, modules don't come from the container). De-dups by
///     <see cref="IWorkspaceModule.Id" />.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly List<IWorkspaceModule> _modules = new();

    /// <summary>The registered modules, in registration order.</summary>
    public IReadOnlyList<IWorkspaceModule> Modules => _modules;

    /// <summary>Registers a module. Ignores a duplicate <see cref="IWorkspaceModule.Id" />.</summary>
    public void Register(IWorkspaceModule module)
    {
        if (_modules.Any(m => m.Id == module.Id))
        {
            return;
        }

        _modules.Add(module);
    }
}
