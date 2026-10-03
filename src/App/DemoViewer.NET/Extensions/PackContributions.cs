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
    private readonly List<CommandDescriptor> _commands = [];
    private readonly List<RulesetContribution> _rulesets = [];
    private readonly List<IPlaybackContribution> _playback = [];

    /// <summary>The pack these contributions belong to.</summary>
    public IFeaturePack Pack { get; } = pack;

    /// <summary>Rulesets the pack owns, in contribution order.</summary>
    public IReadOnlyList<RulesetContribution> Rulesets => _rulesets;

    /// <summary>Modules, in contribution order.</summary>
    public IReadOnlyList<IWorkspaceModule> Modules => _modules;

    /// <summary>Evaluators, in contribution order.</summary>
    public IReadOnlyList<EvaluatorContribution> Evaluators => _evaluators;

    /// <summary>Job kinds, in contribution order.</summary>
    public IReadOnlyList<JobKindDescriptor> JobKinds => _jobKinds;

    /// <summary>Commands, in contribution order. Named apart from the <see cref="Commands(IEnumerable{CommandDescriptor})" /> method the interface declares.</summary>
    public IReadOnlyList<CommandDescriptor> ContributedCommands => _commands;

    /// <summary>2D Playback contributions, in contribution order.</summary>
    public IReadOnlyList<IPlaybackContribution> PlaybackContributions => _playback;

    /// <inheritdoc />
    public void Module(IWorkspaceModule workspaceModule)
    {
        ArgumentNullException.ThrowIfNull(workspaceModule);
        _modules.Add(workspaceModule);
    }

    /// <inheritdoc />
    public void Evaluator(string id, Func<IDemoEvaluator> factory, params string[] after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        _evaluators.Add(new EvaluatorContribution(id, factory, [.. after]));
    }

    /// <inheritdoc />
    public void JobKind(JobKindDescriptor kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        _jobKinds.Add(kind);
    }

    /// <inheritdoc />
    public void Commands(IEnumerable<CommandDescriptor> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands.AddRange(commands);
    }

    /// <inheritdoc />
    public void Ruleset(string rulesetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesetId);
        _rulesets.Add(new RulesetContribution(rulesetId));
    }

    /// <inheritdoc />
    public void Playback(IPlaybackContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _playback.Add(contribution);
    }
}
