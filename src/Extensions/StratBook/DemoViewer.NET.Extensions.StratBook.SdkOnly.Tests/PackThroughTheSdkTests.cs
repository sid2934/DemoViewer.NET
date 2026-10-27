#region

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;

#endregion

namespace SdkOnlyHost;

/// <summary>
///     The Strat Book pack loaded and composed through the published surface alone: the manifest a host
///     reads, the switches it lists in Settings, the services it files in a container, and the ids it hands
///     back from <see cref="IExtension.Contribute" />. Nothing here reaches an internal, so a contribution
///     that works only because the pack and the host share an internals grant fails here while the pack's
///     own suite, which references the app and holds three such grants, stays green.
///     <para>
///         The ids are literals, not the pack's own constants. A constant compared against itself asserts
///         nothing; a literal is what a user's stored settings, a host's layout and the docs carry, so
///         renaming one is a break and this is where it surfaces.
///     </para>
/// </summary>
[NotInParallel(nameof(PackThroughTheSdkTests))]
public class PackThroughTheSdkTests
{
    private const string PackId = "net.demoviewer.pack.stratbook";
    private const string MasterSwitch = "pack.stratbook";

    /// <summary>
    ///     The pack plus the four libraries Directory.Build.targets publishes. Scene and
    ///     Modules.Abstractions arrive through the SDK rather than by name, so a type of theirs is as much
    ///     part of the contract as one of the SDK's own.
    /// </summary>
    private static readonly string[] _packAndPublishedLibraries =
    [
        "DemoViewer.NET.Extensions.StratBook",
        "DemoViewer.NET.Extensions.Sdk",
        "DemoViewer.NET.Extensions.Sdk.Ui",
        "DemoViewer.NET.Modules.Abstractions",
        "DemoViewer.NET.Playback2D.Scene"
    ];

    /// <summary>
    ///     The loader's own route in: read the manifest beside the DLL, resolve its <c>entryType</c> and
    ///     construct it through its public parameterless constructor. A pack whose entry type went internal,
    ///     lost that constructor or stopped matching the manifest's id cannot be loaded by any host, and a
    ///     test holding a direct reference to the type would not notice.
    /// </summary>
    [Test]
    public async Task Manifest_BesideThePack_NamesAnEntryTypeAHostCanConstruct()
    {
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "extension.json");
        await Assert.That(File.Exists(manifestPath)).IsTrue()
            .Because("the SDK's targets copy the stamped manifest into every referencing project's output");

        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        JsonElement root = manifest.RootElement;

        await Assert.That(root.GetProperty("id").GetString()).IsEqualTo(PackId);
        await Assert.That(root.GetProperty("assembly").GetString())
            .IsEqualTo("DemoViewer.NET.Extensions.StratBook.dll");
        await Assert.That(root.GetProperty("requiresHost").GetString()).IsEqualTo("^2.1")
            .Because("the pack calls IExtensionFolder.DirectoryExists and SetLastWriteTimeUtc, which 2.0 hosts lack");
        // The template's literal placeholder: an unstamped manifest fails to parse host-side on purpose.
        await Assert.That(root.GetProperty("version").GetString()).IsNotEqualTo("{version}");

        string entryTypeName = root.GetProperty("entryType").GetString()!;
        Assembly packAssembly = typeof(StratBookPack).Assembly;
        Type? entryType = packAssembly.GetType(entryTypeName, throwOnError: false);

        await Assert.That(entryType).IsNotNull()
            .Because($"the manifest names {entryTypeName}, which a host resolves out of {packAssembly.GetName().Name}");
        await Assert.That(typeof(IExtension).IsAssignableFrom(entryType)).IsTrue();

        IExtension pack = (IExtension)Activator.CreateInstance(entryType!)!;
        await Assert.That(pack.Id).IsEqualTo(PackId);
        await Assert.That(pack.FeatureId).IsEqualTo(MasterSwitch);
    }

    /// <summary>
    ///     The switch tree Settings draws, read the way a host reads it: before any container exists. Every
    ///     id is persisted with the user's choice, and a parent naming a feature nobody declares is a switch
    ///     the user can never reach.
    /// </summary>
    [Test]
    public async Task Features_DeclareOneMasterSwitch_AndEveryParentIsDeclared()
    {
        IExtension pack = new StratBookPack();
        ExtensionFeature[] features = [.. pack.Features];

        ExtensionFeature[] masters = [.. features.Where(f => f.Kind == ExtensionFeatureKind.Extension)];
        await Assert.That(masters.Select(m => m.Id).ToList()).IsEquivalentTo([MasterSwitch]);
        await Assert.That(masters[0].ParentId).IsNull();
        await Assert.That(pack.FeatureId).IsEqualTo(MasterSwitch);
        await Assert.That(pack.FeatureId.StartsWith("pack.", StringComparison.Ordinal)).IsTrue();

        HashSet<string> declared = new(features.Select(f => f.Id), StringComparer.Ordinal);
        await Assert.That(declared.Count).IsEqualTo(features.Length);

        // A sub-feature may hang off a host tab's own feature as well as off the pack's, which is how the
        // pack docks a pane inside 2D Playback.
        HashSet<string> parents = new(declared, StringComparer.Ordinal);
        parents.UnionWith(HostPublishedIds());

        string[] orphans =
        [
            .. features.Where(f => f.Kind != ExtensionFeatureKind.Extension)
                .Where(f => f.ParentId is null || !parents.Contains(f.ParentId))
                .Select(f => $"{f.Id} -> {f.ParentId ?? "(none)"}")
        ];
        await Assert.That(orphans).IsEmpty();

        // The other DI-free lists a host reads at the same moment, each keyed by an id it stores.
        string[] commands = [.. pack.Commands.Select(c => c.Id)];
        string[] jobKinds = [.. pack.JobKinds.Select(k => k.Id)];
        string[] stores = [.. pack.Stores.Select(s => s.Id)];
        await Assert.That(commands.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(commands.Length);
        await Assert.That(jobKinds.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(jobKinds.Length);
        await Assert.That(stores.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(stores.Length);

        // CommandDescriptor's two rules, both of which the host enforces by dropping the offender with a
        // log line: the id carries the extension's id as a prefix, and the scope is one of the host's two
        // or one the same extension declares.
        await Assert.That(pack.Commands
            .Where(c => !c.Id.StartsWith(PackId + ".", StringComparison.Ordinal))
            .Select(c => c.Id).ToList()).IsEmpty();

        HashSet<string> scopes = new(pack.CommandScopes.Select(s => s.Id), StringComparer.Ordinal)
        {
            "playback2d",
            "playback2d.tool"
        };
        await Assert.That(pack.Commands.Where(c => !scopes.Contains(c.Scope))
            .Select(c => $"{c.Id} in scope {c.Scope}").ToList()).IsEmpty();
    }

    /// <summary>
    ///     What <see cref="IExtension.Register" /> puts in a host's container. A pack may file its own types
    ///     and the published libraries'; a service type out of any other assembly of this repo is the pack
    ///     reaching past the contract into whatever host it happened to be built beside.
    /// </summary>
    [Test]
    [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance",
        Justification = "A host holds IExtension and nothing narrower; calling through it is the point.")]
    public async Task Register_FilesOnlyThePacksOwnAndTheSdksServiceTypes()
    {
        IExtension pack = new StratBookPack();
        ServiceCollection services = new();
        pack.Register(services);

        await Assert.That(services.Count).IsGreaterThan(0);

        string[] foreign =
        [
            .. services.Select(d => d.ServiceType)
                .Select(t => (Type: t, Assembly: t.Assembly.GetName().Name ?? ""))
                .Where(x => x.Assembly.StartsWith("DemoViewer.NET", StringComparison.Ordinal)
                            && !_packAndPublishedLibraries.Contains(x.Assembly, StringComparer.Ordinal))
                .Select(x => $"{x.Type.FullName} from {x.Assembly}")
                .Distinct(StringComparer.Ordinal)
        ];
        foreach (string row in foreign)
        {
            Console.WriteLine(row);
        }

        await Assert.That(foreign).IsEmpty();

        // The lifecycle hook is keyed on the extension's id, which is how the host picks it out among every
        // loaded extension's. Keyed on anything else it is never called.
        ServiceDescriptor[] lifecycle = [.. services.Where(d => d.ServiceType == typeof(IExtensionLifecycle))];
        await Assert.That(lifecycle.Length).IsEqualTo(1);
        await Assert.That(lifecycle[0].IsKeyedService).IsTrue();
        await Assert.That(lifecycle[0].ServiceKey as string).IsEqualTo(PackId);
    }

    /// <summary>
    ///     The pack composed off an SDK-only host with every switch off: the ids the host then draws, runs
    ///     and stores under. Each pinned one is a persisted or cross-assembly key, so renaming it is a
    ///     break: the hub a section docks into, the pass order the scheduler reads, the ruleset names the
    ///     engine opens, the settings pages a search finds and the Match Overview action.
    /// </summary>
    [Test]
    public async Task Contribute_FilesTheHubTabsPassesAndRulesetsTheHostDraws()
    {
        StratBookPack pack = new();
        SdkOnlyContext host = new(PackId, MasterSwitch);
        ServiceCollection services = new();
        pack.Register(services);
        services.AddKeyedSingleton<IExtensionContext>(typeof(StratBookPack), host);

        await using ServiceProvider provider = services.BuildServiceProvider();
        RecordedContributions recorded = new(host);
        pack.Contribute(recorded, provider);

        await Assert.That(recorded.HubTabs.Select(h => h.Id).ToList()).IsEquivalentTo([HostIds.StratBookHub]);
        await Assert.That(recorded.HubTabs[0].FeatureId).IsEqualTo(MasterSwitch);
        await Assert.That(recorded.HubTabs[0].RailLabel).IsEqualTo("STRAT BOOK");

        // Registration order is the order the rail draws the sections in.
        await Assert.That(recorded.Modules.Select(m => m.Id).ToList()).IsEquivalentTo(
        [
            "net.demoviewer.situations",
            "net.demoviewer.teams",
            "net.demoviewer.review",
            "net.demoviewer.suggested",
            "net.demoviewer.roundtagger",
            "net.demoviewer.stratbook",
            "net.demoviewer.utilitybook",
            "net.demoviewer.dossier"
        ], CollectionOrdering.Matching);

        // Each pass declares what it reads, so the schedule is a property of these pairs, not of call order.
        await Assert.That(recorded.Passes.Select(p => $"{p.Id} after {string.Join(",", p.After)}").ToList())
            .IsEquivalentTo(
            [
                "roundindex after roundfacts",
                "grenades after library",
                "suggestedtags after roundindex",
                "lineupclips after grenades"
            ], CollectionOrdering.Matching);

        await Assert.That(recorded.RecordPasses.Select(p => p.Id).ToList())
            .IsEquivalentTo(["teams.sides", "suggestedtags"], CollectionOrdering.Matching);

        // The manifest's `rulesets` is read without loading the extension, so safe mode keeps the pack's
        // rulesets apart from the highlights. It has to name exactly what Contribute files, and nothing
        // inside the pack reconciles the two.
        string[] rulesetIds = [.. recorded.Rulesets.Select(r => r.Id)];
        await Assert.That(rulesetIds.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(rulesetIds.Length);
        await Assert.That(rulesetIds).IsEquivalentTo(await ManifestRulesets());

        // A ruleset or a collector whose owner is not one of the filed passes never runs.
        HashSet<string> passIds = new(recorded.Passes.Select(p => p.Id), StringComparer.Ordinal);
        await Assert.That(recorded.Rulesets.Where(r => r.ReadBy is not null && !passIds.Contains(r.ReadBy))
            .Select(r => $"{r.Id} read by {r.ReadBy}").ToList()).IsEmpty();
        await Assert.That(recorded.Collectors.Where(c => !passIds.Contains(c.PassId))
            .Select(c => $"{c.Id} on {c.PassId}").ToList()).IsEmpty();
        await Assert.That(recorded.Collectors.Count).IsGreaterThan(0);

        await Assert.That(recorded.SettingsPages.Select(p => p.Id).ToList())
            .IsEquivalentTo(["stratbook.suggested-tags-tuning"]);
        await Assert.That(recorded.SettingsSchemas.Select(s => s.Id).ToList())
            .IsEquivalentTo(["stratbook.grenade-index"]);
        await Assert.That(recorded.DemoActions.Select(a => a.Id).ToList())
            .IsEquivalentTo(["stratbook.index-grenades"]);

        // The surfaces the host draws that carry no id of their own. A count is all the published surface
        // exposes, and losing one is what this notices.
        await Assert.That(recorded.LibraryContributions.Count).IsEqualTo(2);
        await Assert.That(recorded.PlaybackContributions.Count).IsEqualTo(3);
        await Assert.That(recorded.ReindexEstimates.Count).IsEqualTo(1);
        await Assert.That(recorded.DataDeletedHandlers).IsEqualTo(1);
    }

    /// <summary>
    ///     Every gate a contribution names is one the pack declares or one the host publishes. A
    ///     contribution gated on an id nobody declares is drawn by no host and reachable by no user, which
    ///     is the shape a dead surface takes.
    /// </summary>
    [Test]
    public async Task EveryContributedFeatureId_IsOneThePackDeclaresOrOneTheHostPublishes()
    {
        StratBookPack pack = new();
        SdkOnlyContext host = new(PackId, MasterSwitch);
        ServiceCollection services = new();
        pack.Register(services);
        services.AddKeyedSingleton<IExtensionContext>(typeof(StratBookPack), host);

        await using ServiceProvider provider = services.BuildServiceProvider();
        RecordedContributions recorded = new(host);
        pack.Contribute(recorded, provider);

        HashSet<string> known = new(pack.Features.Select(f => f.Id), StringComparer.Ordinal);
        known.UnionWith(HostPublishedIds());

        List<(string Where, string? FeatureId)> gates = [];
        gates.AddRange(recorded.HubTabs.Select(h => ($"hub {h.Id}", h.FeatureId)));
        gates.AddRange(recorded.SettingsPages.Select(p => ($"settings page {p.Id}", p.FeatureId)));
        gates.AddRange(recorded.SettingsSchemas.Select(s => ($"settings schema {s.Id}", s.FeatureId)));
        gates.AddRange(recorded.Rulesets.Select(r => ($"ruleset {r.Id}", r.FeatureId)));
        gates.AddRange(recorded.DemoActions.Select(a => ($"demo action {a.Id}", a.FeatureId)));
        gates.AddRange(recorded.StatusChips.Select(c => ($"status chip {c.Id}", c.FeatureId)));
        gates.AddRange(recorded.LibraryContributions
            .Select(c => ($"library {c.GetType().Name}", c.FeatureId)));
        gates.AddRange(recorded.ReindexEstimates
            .Select(e => ($"reindex estimate {e.GetType().Name}", (string?)e.FeatureId)));
        gates.AddRange(recorded.DataRemovals
            .Select(r => ($"data removal {r.GetType().Name}", (string?)r.FeatureId)));

        string[] undeclared =
        [
            .. gates.Where(g => g.FeatureId is not null && !known.Contains(g.FeatureId))
                .Select(g => $"{g.Where} gated on {g.FeatureId}")
        ];
        foreach (string row in undeclared)
        {
            Console.WriteLine(row);
        }

        await Assert.That(undeclared).IsEmpty();
    }

    /// <summary>The ruleset names in the manifest beside the pack.</summary>
    private static async Task<string[]> ManifestRulesets()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "extension.json");
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return
        [
            .. manifest.RootElement.GetProperty("rulesets").EnumerateArray().Select(e => e.GetString()!)
        ];
    }

    /// <summary>
    ///     Every id <see cref="HostIds" /> publishes, read off the type rather than restated, so a host id
    ///     added to the SDK counts here the moment it ships.
    /// </summary>
    private static IEnumerable<string> HostPublishedIds() =>
        typeof(HostIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);
}
