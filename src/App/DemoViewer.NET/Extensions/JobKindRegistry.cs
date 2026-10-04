#region

using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Resolves a <see cref="QueueJobKind" />'s label, scheduling rank, light-slot flag and owner tag.
///     <see cref="CoreDescriptors" /> covers the kinds core owns outright; a pack adds the rest through
///     <see cref="IFeaturePack.JobKinds" />, read with no DI (the same shape <c>CommandRegistry</c> uses
///     for keybinds), so building the queue can never re-enter the container through a pack's
///     <see cref="IFeaturePack.Contribute" />. <see cref="Build" /> is where completeness is checked: every
///     <see cref="QueueJobKind" /> member must resolve to exactly one descriptor, so a kind nobody
///     described fails the composition root rather than a queue row discovering it has no label at runtime.
/// </summary>
public sealed class JobKindRegistry
{
    /// <summary>
    ///     The kinds core owns outright: each one is submitted by more than one module, or by none of the
    ///     compiled-in packs, so no single pack's owner tag would describe it. Values pin the switches
    ///     item 13 replaced (<c>DemoProcessingQueue</c>'s old <c>KindRank</c>/<c>IsLight</c>,
    ///     <c>DemoQueueRowViewModel</c>'s old <c>KindLabel</c>); a change here is a deliberate change to
    ///     queue scheduling or the flyout's chip text.
    /// </summary>
    public static IReadOnlyList<JobKindDescriptor> CoreDescriptors { get; } =
    [
        new(QueueJobKind.DemoProcessing, "", 0, false),
        new(QueueJobKind.PackExport, "pack export", 0, false),
        new(QueueJobKind.SidecarMigration, "migration", 1, false),
        new(QueueJobKind.HeapCompaction, "memory", 4, false),
        new(QueueJobKind.StoreSave, "save", 4, true),
        new(QueueJobKind.StoreLoad, "load", 4, true),
        new(QueueJobKind.SectionCompute, "section", 4, true),
        new(QueueJobKind.LibraryScan, "library", 4, false),
        new(QueueJobKind.ExtensionUpdate, "extension update", 4, true),
        new(QueueJobKind.DemoOpen, "open", 4, false)
    ];

    private readonly IReadOnlyDictionary<QueueJobKind, JobKindDescriptor> _byKind;

    private JobKindRegistry(IReadOnlyDictionary<QueueJobKind, JobKindDescriptor> byKind) => _byKind = byKind;

    /// <summary>Built once from the compatible compiled-in packs (<see cref="FeaturePacks.Compatible" />).</summary>
    public static JobKindRegistry Default { get; } = Build(FeaturePacks.Compatible);

    /// <summary>
    ///     Composes every pack's <see cref="IFeaturePack.JobKinds" /> over <see cref="CoreDescriptors" />
    ///     and checks the result covers every <see cref="QueueJobKind" /> member exactly once. Pure: no DI,
    ///     no <c>PackContributionSet</c>, so a test proves a missing or colliding descriptor with its own
    ///     fake pack instead of touching <see cref="Default" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     A pack redeclared a kind core or another pack already owns, or a kind has no descriptor at all.
    /// </exception>
    public static JobKindRegistry Build(IReadOnlyList<IFeaturePack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);

        Dictionary<QueueJobKind, JobKindDescriptor> byKind = new();
        foreach (JobKindDescriptor core in CoreDescriptors)
        {
            byKind[core.Kind] = core;
        }

        foreach (IFeaturePack pack in packs)
        {
            foreach (JobKindDescriptor contributed in pack.JobKinds)
            {
                if (!byKind.TryAdd(contributed.Kind, contributed))
                {
                    throw new InvalidOperationException(
                        $"Pack '{pack.Id}' job kind '{contributed.Kind}' already has a descriptor; a pack " +
                        "cannot redeclare a core kind or a kind another pack already contributed.");
                }
            }
        }

        foreach (QueueJobKind kind in Enum.GetValues<QueueJobKind>())
        {
            if (!byKind.ContainsKey(kind))
            {
                throw new InvalidOperationException(
                    $"No job-kind descriptor for '{kind}'. Add it to JobKindRegistry.CoreDescriptors, " +
                    "or have the owning pack contribute it through IFeaturePack.JobKinds.");
            }
        }

        return new JobKindRegistry(byKind);
    }

    /// <summary>The descriptor for <paramref name="kind" />. Never missing once built: <see cref="Build" /> checked.</summary>
    public JobKindDescriptor Descriptor(QueueJobKind kind) => _byKind[kind];

    /// <summary>The queue row's chip text for <paramref name="kind" />.</summary>
    public string Label(QueueJobKind kind) => Descriptor(kind).Label;

    /// <summary>Scheduling rank among kinds; lower runs first.</summary>
    public int Rank(QueueJobKind kind) => Descriptor(kind).Rank;

    /// <summary>True when the kind needs no heavy slot and may run beside a parse.</summary>
    public bool IsLight(QueueJobKind kind) => Descriptor(kind).IsLight;

    /// <summary>The owner tag every job of this kind carries, or null for a core kind with no single owner.</summary>
    public string? Owner(QueueJobKind kind) => Descriptor(kind).Owner;
}
