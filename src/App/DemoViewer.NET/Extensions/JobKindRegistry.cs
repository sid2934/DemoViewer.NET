#region

using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Resolves a job's label, scheduling rank and light-slot flag. <see cref="CoreDescriptors" /> covers every
///     <see cref="QueueJobKind" />; an extension's jobs run as <see cref="QueueJobKind.Extension" /> under a kind
///     id it declares in <see cref="IExtension.JobKinds" />, read with no DI so building the queue never
///     re-enters the container through an extension's <see cref="IExtension.Contribute" />.
/// </summary>
public sealed class JobKindRegistry
{
    // Declared first: Default below reads it during static initialization.
    /// <summary>The SDK's built-in kind ids and the core kinds they run as.</summary>
    public static IReadOnlyDictionary<string, QueueJobKind> BuiltInExtensionKinds { get; } =
        new Dictionary<string, QueueJobKind>(StringComparer.Ordinal)
        {
            [BuiltInJobKinds.Compute] = QueueJobKind.SectionCompute,
            [BuiltInJobKinds.Save] = QueueJobKind.StoreSave,
            [BuiltInJobKinds.Load] = QueueJobKind.StoreLoad
        };

    /// <summary>
    ///     One row per core kind. A change here is a deliberate change to queue scheduling or the flyout's
    ///     chip text.
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
        new(QueueJobKind.DemoOpen, "open", 4, false),
        new(QueueJobKind.Extension, "extension", 4, false),
        new(QueueJobKind.Scheduling, "scheduling", 4, true),
        new(QueueJobKind.RecordPass, "records", 4, true),
        new(QueueJobKind.LibraryListing, "library", 4, true)
    ];

    private readonly IReadOnlyDictionary<QueueJobKind, JobKindDescriptor> _byKind;
    private readonly IReadOnlyDictionary<string, JobKindDescriptor> _byExtensionKind;
    private readonly IReadOnlyDictionary<string, string> _ownerOf;

    private JobKindRegistry(IReadOnlyDictionary<QueueJobKind, JobKindDescriptor> byKind,
        IReadOnlyDictionary<string, JobKindDescriptor> byExtensionKind, IReadOnlyDictionary<string, string> ownerOf)
    {
        _byKind = byKind;
        _byExtensionKind = byExtensionKind;
        _ownerOf = ownerOf;
    }

    /// <summary>Built once from the compatible compiled-in packs (<see cref="FeaturePacks.Compatible" />).</summary>
    // Lazy: the loader builds registries to check a third-party extension before FeaturePacks is set, and
    // reading FeaturePacks then would freeze it empty.
    private static readonly Lazy<JobKindRegistry> _default = new(() => Build(FeaturePacks.Compatible));

    public static JobKindRegistry Default => _default.Value;

    /// <summary>
    ///     Composes every extension's <see cref="IExtension.JobKinds" /> beside <see cref="CoreDescriptors" />
    ///     and checks every <see cref="QueueJobKind" /> has a row. Pure: no DI, no <c>PackContributionSet</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     An extension declared a kind id the host or another extension already owns, or a core kind has no row.
    /// </exception>
    public static JobKindRegistry Build(IReadOnlyList<IExtension> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);

        Dictionary<QueueJobKind, JobKindDescriptor> byKind = new();
        foreach (JobKindDescriptor core in CoreDescriptors)
        {
            byKind[core.Kind] = core;
        }

        foreach (QueueJobKind kind in Enum.GetValues<QueueJobKind>())
        {
            if (!byKind.ContainsKey(kind))
            {
                throw new InvalidOperationException(
                    $"No job-kind descriptor for '{kind}'. Add it to JobKindRegistry.CoreDescriptors.");
            }
        }

        Dictionary<string, JobKindDescriptor> byExtensionKind = new(StringComparer.Ordinal);
        Dictionary<string, string> ownerOf = new(StringComparer.Ordinal);
        foreach (IExtension pack in packs)
        {
            foreach (ExtensionJobKind declared in pack.JobKinds)
            {
                if (BuiltInExtensionKinds.ContainsKey(declared.Id)
                    || !byExtensionKind.TryAdd(declared.Id,
                        new JobKindDescriptor(QueueJobKind.Extension, declared.Label, declared.Rank, declared.IsLight, declared.Id)))
                {
                    throw new InvalidOperationException(
                        $"Extension '{pack.Id}' job kind '{declared.Id}' is already declared by the host or another extension.");
                }

                ownerOf[declared.Id] = pack.Id;
            }
        }

        return new JobKindRegistry(byKind, byExtensionKind, ownerOf);
    }

    /// <summary>
    ///     The descriptor for a job. <paramref name="extensionKind" /> names a declared kind for
    ///     <see cref="QueueJobKind.Extension" />; an unknown one falls back to the generic extension row.
    /// </summary>
    public JobKindDescriptor Descriptor(QueueJobKind kind, string? extensionKind = null) =>
        kind == QueueJobKind.Extension && extensionKind is not null
                                       && _byExtensionKind.TryGetValue(extensionKind, out JobKindDescriptor? declared)
            ? declared
            : _byKind[kind];

    /// <summary>True when <paramref name="extensionKind" /> is a kind an extension declared.</summary>
    public bool IsDeclared(string extensionKind) => _byExtensionKind.ContainsKey(extensionKind);

    /// <summary>True when the extension <paramref name="extensionId" /> declared <paramref name="extensionKind" />.</summary>
    public bool IsDeclaredBy(string extensionKind, string extensionId) =>
        _ownerOf.TryGetValue(extensionKind, out string? owner) && string.Equals(owner, extensionId, StringComparison.Ordinal);

    /// <summary>The queue row's chip text.</summary>
    public string Label(QueueJobKind kind, string? extensionKind = null) => Descriptor(kind, extensionKind).Label;

    /// <summary>Scheduling rank among kinds; lower runs first.</summary>
    public int Rank(QueueJobKind kind, string? extensionKind = null) => Descriptor(kind, extensionKind).Rank;

    /// <summary>True when the kind needs no heavy slot and may run beside a parse.</summary>
    public bool IsLight(QueueJobKind kind, string? extensionKind = null) => Descriptor(kind, extensionKind).IsLight;
}
