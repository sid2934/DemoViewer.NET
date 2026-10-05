#region

using System.ComponentModel;
using Avalonia.Controls;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels;
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
/// <param name="ExtensionKind">For <see cref="QueueJobKind.Extension" />, the extension's kind id; null for a core kind.</param>
public sealed record JobKindDescriptor(QueueJobKind Kind, string Label, int Rank, bool IsLight, string? ExtensionKind = null);

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
///     What a contributed status chip shows right now: the built <see cref="StatusChipViewModel" />, or null
///     before the owner has anything to mount, and whether it belongs on the strip at all (running, or a
///     finished result not yet dismissed). The shell adds the owning <see cref="StatusChipContribution.FeatureId" />
///     on top: a slot that answers true while its owning pack is off still shows nothing.
/// </summary>
public interface IContributedStatusChip : INotifyPropertyChanged
{
    /// <summary>The chip to show, or null before anything is mounted.</summary>
    StatusChipViewModel? Chip { get; }

    /// <summary>True while <see cref="Chip" /> belongs on the strip.</summary>
    bool IsShown { get; }
}

/// <summary>
///     A status-chip slot a pack reserves on the strip. Unlike <see cref="HostTabContribution" /> the chip
///     itself may not exist yet (a 2D export job builds lazily, on the first Export), so the contribution
///     carries the SLOT, not the chip: <paramref name="Source" /> raises <see cref="INotifyPropertyChanged" />
///     once something mounts into it.
/// </summary>
/// <param name="Id">Stable id (e.g. <c>"stratbook.export"</c>). A lookup key, never shown.</param>
/// <param name="Order">Sort key among contributed chips. Reserved for a future ordered strip; unread today.</param>
/// <param name="Source">The pack-owned slot the shell watches.</param>
/// <param name="FeatureId">The gate id the chip shows under; null takes the owning pack's id.</param>
public sealed record StatusChipContribution(
    string Id,
    int Order,
    IContributedStatusChip Source,
    string? FeatureId = null);

/// <summary>An extension's Match Overview action and the feature it shows under.</summary>
public sealed record GatedDemoAction(DemoAction Action, string FeatureId)
{
    /// <summary>
    ///     Runs a host handler for <see cref="DemoAction.Changed" /> on the UI thread, since an extension may
    ///     call <see cref="DemoAction.NotifyChanged" /> from any thread. Null runs it on the raising thread.
    /// </summary>
    public Action<Action>? ToUiThread { get; init; }
}

/// <summary>
///     What a first-party extension can contribute beyond the SDK: surfaces whose types are the app's own and
///     not part of the public contract. The host's contribution collector implements it; a first-party
///     extension reaches it by casting the <see cref="IExtensionContributions" /> it is handed.
/// </summary>
internal interface IFirstPartyContributions : IExtensionContributions
{
    /// <summary>A strip tab that hosts sections.</summary>
    void HostTab(HostTabContribution host);

    /// <summary>An evaluator with the app's forward-pass and opportunistic hooks.</summary>
    void FirstPartyEvaluator(string id, Func<IDemoEvaluator> factory, params string[] after);

    /// <summary>A ruleset the merged background build runs only while the extension is on.</summary>
    void Ruleset(string rulesetId);

    /// <summary>A status-chip slot on the shell's strip.</summary>
    void StatusChip(StatusChipContribution chip);

    /// <summary>A 2D Playback contribution against the app's own surface: keymap scopes, scene frames, core tracks.</summary>
    void FirstPartyPlayback(IPlaybackContribution contribution);
}
