#region

using System.Reflection;
using System.Text.RegularExpressions;
using DemoViewer.NET.Configuration;
using SysAssembly = System.Reflection.Assembly;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>One settings key and who actually touches it, outside the settings plumbing.</summary>
/// <param name="Name">The property name.</param>
/// <param name="Readers">Production methods that call its getter.</param>
/// <param name="Writers">Production methods that call its setter.</param>
internal sealed record SettingConsumption(
    string Name,
    IReadOnlyList<string> Readers,
    IReadOnlyList<string> Writers)
{
    public bool IsConsumed => Readers.Count > 0 && Writers.Count > 0;

    public string Describe() =>
        $"{Name} — read by {Sites(Readers)}, written by {Sites(Writers)}";

    private static string Sites(IReadOnlyList<string> sites) =>
        sites.Count == 0 ? "NOTHING" : $"{sites.Count} ({string.Join(", ", sites.Take(2))})";
}

/// <summary>
///     <b>
///         D6 §4 guard 3 — every <see cref="Playback2DSettings" /> key has a production reader AND a
///         production writer.
///     </b>
///     <para>
///         <see cref="SettingsWasmRoundTripTests" /> tests transport, not consumption: it proves every
///         property survives a fileless round trip, never that anybody sends or receives it. A setting can
///         therefore ship read at runtime, with a <c>WriteInMemory</c> row, and with no writer and no UI.
///     </para>
///     <para>
///         <b>Read from IL.</b> A read is a call to <c>get_X</c> and a write a call to <c>set_X</c>,
///         attributed to the calling method, with everything in <c>DemoViewer.NET.Configuration</c>
///         excluded — that namespace is exactly <c>AppSettings.cs</c> and <c>SettingsService.cs</c>, whose
///         job is to move the value, not to mean anything by it. The binder's reflective writes are
///         invisible to IL, which is correct: <c>IConfiguration</c> filling a property is transport too.
///         The registry check below covers what reflection structurally cannot see — a key with no property
///         at all.
///     </para>
/// </summary>
public class Playback2DSettingsConsumptionTests
{
    /// <summary>
    ///     Keys knowingly missing a reader or a writer. The reason is the entry.
    ///     <para>
    ///         <c>AnnotationAutoSave</c>'s entry is gone: round 3A gave the key the writer it never had
    ///         (<c>AnnotationSessionController.WritePendingStyle</c>) and the UI it never had (the
    ///         annotation toolbar's Auto-save box), and moved the authoritative check from the schedule
    ///         to <c>SaveNowAsync</c> so a flush at shutdown honours it too.
    ///     </para>
    /// </summary>
    private static readonly Dictionary<string, string> _unconsumedByDesign = new(StringComparer.Ordinal)
    {
        ["LegacyViewport"] =
            "BY DESIGN, and the only entry here that is. Plan decision D-9: a parity escape hatch for one "
            + "release, deliberately NOT a FeatureCatalog id and deliberately with no UI — it is set by "
            + "hand-editing settings.json or by DV_PLAYBACK2D_RENDERER, and it is deleted with the old "
            + "control. It has a reader (Playback2DTabViewModel) and needs no writer."
    };

    /// <summary>
    ///     Registry §3.10 keys that the class does not carry. The reason is the entry.
    /// </summary>
    private static readonly Dictionary<string, string> _registryKeysNotYetBuilt = new(StringComparer.Ordinal)
    {
        ["RenderBackend"] =
            "The registry pins `RenderBackend` (auto|cpu|gpu) and the GPU stack behind it is built and "
            + "tested, but the key has no property: the app hard-codes CpuSurfaceProvider and the backend "
            + "is reachable only from `dv2d --backend`. Deliberately not added — see the "
            + "Playback2DSettings class doc for why nothing in the app can consume it yet. The landing "
            + "site is Playback2DTabViewModel.OpenExport's `surfaces:` argument. Delete this entry in the "
            + "commit that pins the export loop to one thread and gives the key a reader; §3.10 of the "
            + "overview wants the same amendment."
    };

    [Test]
    public async Task EveryPlayback2dSettingsKey_HasAProductionReader_AndAProductionWriter()
    {
        List<SettingConsumption> keys = Analyse(
            typeof(Playback2DSettings), Playback2DWholeGraph.ProductionAssemblies, IsPlumbing);

        foreach (SettingConsumption key in keys.OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"[settings-use] {(key.IsConsumed ? "ok  " : "DEAD")} {key.Describe()}");
        }

        await Assert.That(keys.Count).IsGreaterThan(30)
            .Because("the section carries the registry §3.10 key set, not a stub");

        List<string> unconsumed = keys
            .Where(k => !k.IsConsumed && !_unconsumedByDesign.ContainsKey(k.Name))
            .Select(k => k.Describe())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(string.Join("; ", unconsumed)).IsEqualTo("")
            .Because("a key with no writer is a preference the user cannot express, and one with no "
                     + "reader is a preference the app does not honour — neither is visible to a "
                     + "round-trip test, which only proves the value can travel");
    }

    /// <summary>
    ///     The registry is the design authority for which keys EXIST (§3.10, "one section, one class").
    ///     A key named there and absent from the class is a feature the plan believes shipped.
    /// </summary>
    [Test]
    public async Task EveryRegistryKey_ExistsOnTheSettingsClass()
    {
        List<string> registry = RegistryKeys();
        HashSet<string> declared = typeof(Playback2DSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Console.WriteLine($"[settings-registry] §3.10 names {registry.Count} keys; the class declares "
                          + $"{declared.Count}");

        await Assert.That(registry.Count).IsGreaterThan(20)
            .Because("a parse that found a handful of keys has found the wrong block, and would pass "
                     + "whatever the class contains");

        List<string> missing = registry
            .Where(k => !declared.Contains(k) && !_registryKeysNotYetBuilt.ContainsKey(k))
            .ToList();

        await Assert.That(string.Join(", ", missing)).IsEqualTo("")
            .Because("§3.10 is the persisted-key contract; a key it pins and the class lacks is a "
                     + "setting the plan, the docs and the reader all believe exists");
    }

    /// <summary>The allow-lists must be load-bearing, and each entry must say why.</summary>
    [Test]
    public async Task BothAllowLists_NameExactlyWhatWouldFail()
    {
        HashSet<string> failing = Analyse(
                typeof(Playback2DSettings), Playback2DWholeGraph.ProductionAssemblies, IsPlumbing)
            .Where(k => !k.IsConsumed)
            .Select(k => k.Name)
            .ToHashSet(StringComparer.Ordinal);

        Console.WriteLine($"[settings-use] without the allow-list: {string.Join(", ", failing)}");

        await Assert.That(string.Join(", ", _unconsumedByDesign.Keys.Where(k => !failing.Contains(k))))
            .IsEqualTo("")
            .Because("an entry for a key that IS consumed now is dead weight");

        HashSet<string> declared = typeof(Playback2DSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(string.Join(", ", _registryKeysNotYetBuilt.Keys.Where(declared.Contains)))
            .IsEqualTo("")
            .Because("the key landed — delete the entry so the reader/writer guard starts asking about it");

        await Assert.That(_unconsumedByDesign.Values.Concat(_registryKeysNotYetBuilt.Values)
                .All(r => r.Length > 40)).IsTrue()
            .Because("§4: an allow-list entry must carry WHY, not just a name");
    }

    /// <summary>
    ///     The self-check. Three canary properties on a type in THIS assembly — read and written, read
    ///     only, written only — must be classified correctly. A scan that could not tell them apart would
    ///     make every assertion above either a false alarm or a rubber stamp.
    /// </summary>
    [Test]
    public async Task TheScan_SeparatesAReadOnlyKeyFromAWriteOnlyKeyFromAWiredOne()
    {
        SysAssembly self = typeof(Playback2DSettingsConsumptionTests).Assembly;
        List<SettingConsumption> canaries = Analyse(
            typeof(SettingsGuardCanary),
            [.. Playback2DWholeGraph.ProductionAssemblies, self],
            IsPlumbing);

        foreach (SettingConsumption canary in canaries)
        {
            Console.WriteLine($"[settings-canary] {canary.Describe()}");
        }

        Dictionary<string, SettingConsumption> byName =
            canaries.ToDictionary(c => c.Name, StringComparer.Ordinal);

        await Assert.That(byName.Count).IsEqualTo(3);
        await Assert.That(byName["ReadAndWritten"].IsConsumed).IsTrue();
        await Assert.That(byName["ReadNeverWritten"].Readers).IsNotEmpty();
        await Assert.That(byName["ReadNeverWritten"].Writers).IsEmpty()
            .Because("this is AnnotationAutoSave's exact shape, and the guard must see it");
        await Assert.That(byName["WrittenNeverRead"].Writers).IsNotEmpty();
        await Assert.That(byName["WrittenNeverRead"].Readers).IsEmpty();
    }

    // ── The analysis ────────────────────────────────────────────────────────────────────────────────

    private static List<SettingConsumption> Analyse(
        Type settings, IEnumerable<SysAssembly> scope, Func<string, bool> isPlumbing)
    {
        PropertyInfo[] properties = settings
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

        HashSet<string> accessors = properties
            .SelectMany(p => new[]
            {
                "get_" + p.Name, "set_" + p.Name
            })
            .ToHashSet(StringComparer.Ordinal);

        string owner = settings.FullName!;
        List<IlSite> sites = Playback2DWholeGraph.Scan(scope,
            (type, member) => string.Equals(type, owner, StringComparison.Ordinal)
                              && accessors.Contains(member));

        List<SettingConsumption> result = [];
        foreach (PropertyInfo property in properties)
        {
            List<string> readers = [];
            List<string> writers = [];

            foreach (IlSite site in sites)
            {
                if (site.Access != IlAccess.Call || isPlumbing(site.Type))
                {
                    continue;
                }

                string caller = $"{Short(site.Type)}::{site.Method}";
                if (string.Equals(site.TargetMember, "get_" + property.Name, StringComparison.Ordinal))
                {
                    readers.Add(caller);
                }
                else if (string.Equals(site.TargetMember, "set_" + property.Name, StringComparison.Ordinal))
                {
                    writers.Add(caller);
                }
            }

            result.Add(new SettingConsumption(property.Name,
                [.. readers.Distinct(StringComparer.Ordinal)],
                [.. writers.Distinct(StringComparer.Ordinal)]));
        }

        return result;
    }

    // AppSettings.cs and SettingsService.cs are the whole of this namespace, and between them they are the
    // transport: binding, flattening, the fileless WriteInMemory table. Their touches are not consumption.
    private static bool IsPlumbing(string callerType) =>
        callerType.StartsWith("DemoViewer.NET.Configuration.", StringComparison.Ordinal);

    // The backticked identifiers in §3.10's AppSettings.Playback2D paragraph. Type names, enum spellings
    // and qualified references are filtered out by shape: a key is a bare PascalCase identifier.
    private static List<string> RegistryKeys()
    {
        string path = Path.Combine(Playback2DWholeGraph.RepoRoot(),
            "docs", "playback2d-v2", "plans", "00-overview.md");
        string text = File.ReadAllText(path);

        int start = text.IndexOf("**`AppSettings.Playback2D`", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"§3.10's AppSettings.Playback2D paragraph was not found in {path} — the registry moved, "
                + "and this guard is reading nothing.");
        }

        int end = text.IndexOf("\n---", start, StringComparison.Ordinal);
        string block = end < 0 ? text[start..] : text[start..end];

        // BLOCKQUOTE lines are commentary, not registry lines, and are dropped BEFORE the shape filter.
        // §3.10 carries `>` callouts whose prose names types in backticks — every one a bare PascalCase
        // identifier, and so a "key" this guard would otherwise demand the settings class declare.
        block = string.Join('\n', block.Split('\n')
            .Where(l => !l.TrimStart().StartsWith('>')));

        return Regex.Matches(block, "`([^`]+)`")
            .Select(m => m.Groups[1].Value)
            .Where(k => Regex.IsMatch(k, "^[A-Z][A-Za-z0-9]*$"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string Short(string fullName) => fullName[(fullName.LastIndexOf('.') + 1)..];
}

/// <summary>
///     The canary for
///     <see cref="Playback2DSettingsConsumptionTests.TheScan_SeparatesAReadOnlyKeyFromAWriteOnlyKeyFromAWiredOne" />.
/// </summary>
internal sealed class SettingsGuardCanary
{
    /// <summary>Both halves — the shape a healthy key has.</summary>
    public int ReadAndWritten { get; set; }

    /// <summary><c>AnnotationAutoSave</c>'s shape: honoured at runtime, expressible by nobody.</summary>
    public int ReadNeverWritten { get; set; }

    /// <summary>The mirror image: a preference stored and never honoured.</summary>
    public int WrittenNeverRead { get; set; }
}

/// <summary>Gives <see cref="SettingsGuardCanary" /> its three different wirings.</summary>
internal static class SettingsGuardCanaryConsumer
{
    public static int Use(SettingsGuardCanary canary)
    {
        canary.WrittenNeverRead = 1;
        canary.ReadAndWritten = canary.ReadAndWritten + 1;
        return canary.ReadNeverWritten;
    }
}
