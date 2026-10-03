#region

using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     A processing-queue job kind's label, scheduling rank, light-slot flag and owner tag, resolved by
///     <see cref="JobKindRegistry" />. The kind's identity stays the <see cref="QueueJobKind" /> enum
///     member (what a job carries); this is only its metadata.
/// </summary>
/// <param name="Kind">The kind this describes.</param>
/// <param name="Label">Row label in the queue UI.</param>
/// <param name="Rank">Scheduling rank among kinds; lower runs first.</param>
/// <param name="IsLight">True when the job needs no heavy slot and may run beside a parse.</param>
/// <param name="Owner">
///     The owner tag every job of this kind carries, so <see cref="IDemoProcessingQueue.CancelOwned(string)" />
///     and a future owner column agree with the kind. Null for a core kind, whose jobs carry whichever
///     owner tag the submitting module names.
/// </param>
public sealed record JobKindDescriptor(QueueJobKind Kind, string Label, int Rank, bool IsLight, string? Owner = null);

/// <summary>An evaluator a pack adds to the demo fan-out, ordered after the evaluator ids it reads from.</summary>
/// <param name="Id">
///     The id the built evaluator reports through <see cref="IDemoEvaluator.Id" />. Declared up front so the
///     fan-out order can be sorted and validated without constructing a disabled pack's evaluators.
/// </param>
/// <param name="Factory">Builds or resolves the evaluator; invoked only while the owning pack is enabled.</param>
/// <param name="After">Evaluator ids whose writes this one reads in the same pass.</param>
public sealed record EvaluatorContribution(string Id, Func<IDemoEvaluator> Factory, IReadOnlyList<string> After);

/// <summary>
///     A ruleset in the rules directories that a pack owns. The merged background build runs it only while
///     the owning pack is on, and it never enters the highlights fingerprint: the pack stamps its own rows
///     with its own identity, so toggling the pack re-runs nothing of the core's.
/// </summary>
/// <param name="RulesetId">The <c>id:</c> of the ruleset as the rules directories spell it.</param>
public sealed record RulesetContribution(string RulesetId);

/// <summary>
///     A pack-owned ruleset as <see cref="DemoViewer.NET.Modules.Highlights.MergedRulesBuild" /> reads it: the
///     id and the live answer to "is the owning pack on".
/// </summary>
public sealed record GatedRuleset(string RulesetId, Func<bool> Enabled);

/// <summary>
///     A strip tab a pack contributes to host sections (the Strat Book hub). Sections name it by
///     <paramref name="HostId" /> through <see cref="WorkspaceTabDescriptor.HostId" />; the shell shows the
///     tab only while <paramref name="FeatureId" /> resolves on AND at least one hosted section does, and
///     lands on Library when the tab the user is on goes away.
/// </summary>
/// <param name="HostId">The id sections name, e.g. <c>"stratbook.hub"</c>. A persisted key.</param>
/// <param name="TabId">The strip tab's own id, the session's active-tab key when no section is selected.</param>
/// <param name="Header">The strip header.</param>
/// <param name="Order">The strip position among the Main-placement tabs.</param>
/// <param name="RailLabel">The band over the host's section rail.</param>
/// <param name="ViewModelFactory">Builds the host VM once, when the shell builds the strip.</param>
/// <param name="ViewFactory">Realizes the host's view on each activation.</param>
/// <param name="FeatureId">The gate id the tab shows under; null takes the owning pack's id.</param>
public sealed record HostTabContribution(
    string HostId,
    string TabId,
    string Header,
    int Order,
    string RailLabel,
    Func<IHostTabViewModel> ViewModelFactory,
    Func<Control> ViewFactory,
    string? FeatureId = null);

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

    /// <summary>A strip tab that hosts sections; see <see cref="HostTabContribution" />.</summary>
    void HostTab(HostTabContribution host);

    /// <summary>
    ///     An evaluator on the demo fan-out, reporting <paramref name="id" /> through
    ///     <see cref="IDemoEvaluator.Id" />, ordered after <paramref name="after" />.
    /// </summary>
    void Evaluator(string id, Func<IDemoEvaluator> factory, params string[] after);

    /// <summary>A processing-queue job kind the pack owns.</summary>
    void JobKind(JobKindDescriptor kind);

    /// <summary>
    ///     The pack's keymap commands. The composition root checks this against <see cref="IFeaturePack.Commands" />
    ///     (the DI-free source every non-composed consumer reads) so the two cannot drift.
    /// </summary>
    void Commands(IEnumerable<CommandDescriptor> commands);

    /// <summary>A ruleset in the rules directories the pack owns; see <see cref="RulesetContribution" />.</summary>
    void Ruleset(string rulesetId);
}
