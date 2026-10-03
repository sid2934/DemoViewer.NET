#region

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
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
///     A settings page a pack contributes: rendered under Extensions, beneath the pack's master switch and
///     feature rows, hidden while <paramref name="FeatureId" /> resolves off. The View is built explicitly
///     (not through <c>ViewLocator</c>'s reflection), but the VM is still typed <see cref="ViewModelBase" />
///     so a future bare <c>ContentControl</c> binding falls back to a real view, never a "Not Found" string.
/// </summary>
/// <param name="Id">Stable id (e.g. <c>"stratbook.grenade-index"</c>). Never shown; a lookup key for tests.</param>
/// <param name="Header">The section header text, in the house ALL-CAPS style.</param>
/// <param name="Order">Sort key among a pack's own contributed pages.</param>
/// <param name="Keywords">Fed into the existing settings search alongside the built-in section keywords.</param>
/// <param name="ViewModelFactory">Builds the page's VM once, when Settings opens.</param>
/// <param name="ViewFactory">Builds the page's View once; its DataContext is set to the built VM.</param>
/// <param name="FeatureId">The gate id the page shows under; null takes the owning pack's id.</param>
public sealed record SettingsPageContribution(
    string Id,
    string Header,
    int Order,
    string Keywords,
    Func<ViewModelBase> ViewModelFactory,
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

/// <summary>
///     What a pack's Settings "N demos will be re-indexed" notice (architecture doc §8) counts against.
///     <see cref="PackFeatureId" /> ties the estimate to the one pack toggle Settings watches for the
///     notice, without <c>SettingsViewModel</c> naming the pack's type.
/// </summary>
public interface IPackReindexEstimate
{
    /// <summary>The pack id whose toggle this estimate answers for.</summary>
    string PackFeatureId { get; }

    /// <summary>Counts demos the pack's evaluators would re-index if it came back on right now.</summary>
    Task<int> CountAsync();
}

/// <summary>Which app-data root a <see cref="StoreDescriptor" />'s paths resolve against.</summary>
public enum StoreRoot
{
    /// <summary>
    ///     <c>AppPaths.ConfigRoot</c>: the consolidated per-user config root (<c>settings.json</c>'s
    ///     directory), where a pack's own user-truth stores and drop-in folders live.
    /// </summary>
    Config,

    /// <summary><c>AppPaths.DemoCacheDir</c>: the demo cache root, where the pack's regenerable caches live.</summary>
    Cache
}

/// <summary>
///     One pack store or cache path, for "delete extension data" (architecture doc §7.4, §8). A path in
///     <paramref name="Paths" /> is either a literal file or directory relative to <paramref name="Root" />,
///     or a demo-sidecar pattern of the form <c>"demos/*&lt;suffix&gt;"</c>: everything in <c>demos/</c>
///     under the root whose file name ends with <c>&lt;suffix&gt;</c>, matched by ordinal string comparison,
///     never by a filesystem glob (<c>demos/</c> also holds the core record sidecars, which must survive).
/// </summary>
/// <param name="Id">Stable id, unique within the pack. A lookup key, never shown.</param>
/// <param name="Label">The store's name in the delete confirmation and its size line.</param>
/// <param name="Root">Which root <paramref name="Paths" /> resolve against.</param>
/// <param name="Paths">One or more paths or demo-sidecar patterns, relative to <paramref name="Root" />.</param>
/// <param name="IsUserWork">
///     True when the store holds something the user wrote or tuned by hand, not a derived cache: named by
///     label in the delete confirmation. False for a store that re-fills itself from the library.
/// </param>
public sealed record StoreDescriptor(string Id, string Label, StoreRoot Root, IReadOnlyList<string> Paths, bool IsUserWork);

/// <summary>What <see cref="IPackDataRemoval.InventoryAsync" /> found of one <see cref="StoreDescriptor" />.</summary>
/// <param name="Descriptor">The store this counts.</param>
/// <param name="FileCount">Files that exist under it right now.</param>
/// <param name="Bytes">Their combined size.</param>
public sealed record StoreInventoryItem(StoreDescriptor Descriptor, int FileCount, long Bytes);

/// <summary>A pack's on-disk footprint at one point in time, one entry per declared store.</summary>
/// <param name="Items">One entry per <see cref="StoreDescriptor" /> the pack declared, in declaration order.</param>
public sealed record PackDataInventory(IReadOnlyList<StoreInventoryItem> Items)
{
    /// <summary>An inventory of nothing: every store reports zero, used when the real count could not run.</summary>
    public static readonly PackDataInventory Empty = new([]);

    /// <summary>Every file the inventory found, across every store.</summary>
    public long TotalBytes => Items.Sum(i => i.Bytes);

    /// <summary>The stores named <see cref="StoreDescriptor.IsUserWork" />, the ones the confirmation calls out by name.</summary>
    public IEnumerable<StoreInventoryItem> UserWorkItems => Items.Where(i => i.Descriptor.IsUserWork);
}

/// <summary>What <see cref="IPackDataRemoval.DeleteAsync" /> did.</summary>
/// <param name="Ran">
///     False when the queue dropped the item before it ran (the pack was switched back on first, which
///     cancels every item owned by its feature id): nothing was touched.
/// </param>
/// <param name="Removed">What was actually deleted, one entry per store.</param>
/// <param name="RecordsUpdated">Demo cache records whose pack payload and stamps were stripped.</param>
public sealed record PackDataRemovalResult(bool Ran, PackDataInventory Removed, int RecordsUpdated)
{
    /// <summary>The queue dropped the item: nothing ran.</summary>
    public static readonly PackDataRemovalResult NotRun = new(false, PackDataInventory.Empty, 0);
}

/// <summary>
///     What a pack hands Settings for "delete extension data" (architecture doc §8, item 24): available
///     whether the pack is on or off, so the Settings VM never has to resolve the pack's own stores or
///     gate state itself.
/// </summary>
public interface IPackDataRemoval
{
    /// <summary>The pack id the Settings row is filed under (for the row's label, read off <c>FeatureCatalog</c>).</summary>
    string PackFeatureId { get; }

    /// <summary>Counts what is on disk right now, for the confirmation's size line. Never deletes anything.</summary>
    Task<PackDataInventory> InventoryAsync();

    /// <summary>
    ///     Deletes every declared store's files, strips the pack's payload and stamps from the demo cache,
    ///     and drops whatever the pack's own stores still hold in memory. If the pack is on, turns it off
    ///     first and waits for its release to finish, so nothing it is still writing is deleted out from
    ///     under it; the pack stays off afterward.
    /// </summary>
    Task<PackDataRemovalResult> DeleteAsync();
}

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

    /// <summary>A settings page rendered under Extensions; see <see cref="SettingsPageContribution" />.</summary>
    void SettingsPage(SettingsPageContribution page);

    /// <summary>A status-chip slot on the strip; see <see cref="StatusChipContribution" />.</summary>
    void StatusChip(StatusChipContribution chip);

    /// <summary>The pack's answer for the Settings re-index notice; see <see cref="IPackReindexEstimate" />.</summary>
    void ReindexEstimate(IPackReindexEstimate estimate);

    /// <summary>
    ///     A 2D Playback contribution: attached to every 2D tab while the pack is on, detached when it goes
    ///     off. See <see cref="IPlaybackContribution" />.
    /// </summary>
    void Playback(IPlaybackContribution contribution);

    /// <summary>One store or cache path the pack owns, for "delete extension data"; see <see cref="StoreDescriptor" />.</summary>
    void Store(StoreDescriptor store);

    /// <summary>The pack's "delete extension data" action; see <see cref="IPackDataRemoval" />.</summary>
    void DataRemoval(IPackDataRemoval removal);
}
