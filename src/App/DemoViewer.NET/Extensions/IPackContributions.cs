#region

using System.Diagnostics.CodeAnalysis;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     A processing-queue job kind a pack owns: how its rows label, rank and schedule. The owning pack is
///     implicit (the pack registered it).
/// </summary>
/// <param name="Id">Stable kind id, e.g. <c>"stratbook.mining"</c>.</param>
/// <param name="Label">Row label in the queue UI.</param>
/// <param name="Rank">Scheduling rank among kinds; lower runs first.</param>
/// <param name="IsLight">True when the job needs no heavy slot and may run beside a parse.</param>
public sealed record JobKindDescriptor(string Id, string Label, int Rank, bool IsLight);

/// <summary>An evaluator a pack adds to the demo fan-out, ordered after the evaluator ids it reads from.</summary>
/// <param name="Factory">Builds or resolves the evaluator; invoked once when the fan-out is composed.</param>
/// <param name="After">Evaluator ids whose writes this one reads in the same pass.</param>
public sealed record EvaluatorContribution(Func<IDemoEvaluator> Factory, IReadOnlyList<string> After);

/// <summary>
///     What a pack may hand the shell from <see cref="IFeaturePack.Contribute" />. Every contribution
///     carries the pack's umbrella id implicitly; the shell shows one only while that id and any narrower
///     id it names both resolve on.
/// </summary>
public interface IPackContributions
{
    /// <summary>A workspace module: strip tabs and hosted sections.</summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Module is the extension design's name for a workspace module contribution.")]
    void Module(IWorkspaceModule workspaceModule);

    /// <summary>An evaluator on the demo fan-out, ordered after <paramref name="after" />.</summary>
    void Evaluator(Func<IDemoEvaluator> factory, params string[] after);

    /// <summary>A processing-queue job kind the pack owns.</summary>
    void JobKind(JobKindDescriptor kind);

    /// <summary>
    ///     The pack's keymap commands. The composition root checks this against <see cref="IFeaturePack.Commands" />
    ///     (the DI-free source every non-composed consumer reads) so the two cannot drift.
    /// </summary>
    void Commands(IEnumerable<CommandDescriptor> commands);
}
