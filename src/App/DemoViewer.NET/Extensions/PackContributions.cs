#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The composition root's collector for one pack's contributions, in the order the pack made them.
///     Each pack gets its own instance so the shell knows which pack contributed what.
/// </summary>
internal sealed class PackContributions(IFeaturePack pack) : IPackContributions
{
    private readonly List<IWorkspaceModule> _modules = [];
    private readonly List<EvaluatorContribution> _evaluators = [];
    private readonly List<JobKindDescriptor> _jobKinds = [];

    /// <summary>The pack these contributions belong to.</summary>
    public IFeaturePack Pack { get; } = pack;

    /// <summary>Modules, in contribution order.</summary>
    public IReadOnlyList<IWorkspaceModule> Modules => _modules;

    /// <summary>Evaluators, in contribution order.</summary>
    public IReadOnlyList<EvaluatorContribution> Evaluators => _evaluators;

    /// <summary>Job kinds, in contribution order.</summary>
    public IReadOnlyList<JobKindDescriptor> JobKinds => _jobKinds;

    /// <inheritdoc />
    public void Module(IWorkspaceModule workspaceModule)
    {
        ArgumentNullException.ThrowIfNull(workspaceModule);
        _modules.Add(workspaceModule);
    }

    /// <inheritdoc />
    public void Evaluator(Func<IDemoEvaluator> factory, params string[] after)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _evaluators.Add(new EvaluatorContribution(factory, [.. after]));
    }

    /// <inheritdoc />
    public void JobKind(JobKindDescriptor kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        _jobKinds.Add(kind);
    }
}
