#region

using System.Reflection;
using SysAssembly = System.Reflection.Assembly;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>One event contract and what production actually does with it.</summary>
/// <param name="Key">The contract's identity: the interface event where there is one, else the concrete event.</param>
/// <param name="Implementers">Every concrete declaring type in the group.</param>
/// <param name="Raisers">Production methods that load the backing field, i.e. that can raise it.</param>
/// <param name="Subscribers">Production methods that call an <c>add_</c> accessor for it.</param>
internal sealed record EventContract(
    string Key,
    IReadOnlyList<string> Implementers,
    IReadOnlyList<string> Raisers,
    IReadOnlyList<string> Subscribers)
{
    public bool IsWired => Raisers.Count > 0 && Subscribers.Count > 0;

    public string Describe() =>
        $"{Key} — raised by {Count(Raisers)}, subscribed by {Count(Subscribers)}";

    private static string Count(IReadOnlyList<string> sites) =>
        sites.Count == 0 ? "NOTHING" : $"{sites.Count} ({string.Join(", ", sites.Take(3))})";
}

/// <summary>
///     <b>Every public event in the module has a production raiser AND a production subscriber.</b>
///     A unit test subscribes directly, so it proves nothing about whether production does; a producer
///     wired to no consumer passes every one of them.
///     <para>
///         <b>Asked of the CONTRACT, not the implementation.</b> The four <c>MarkersChanged</c>
///         implementations are one <c>ITimelineTrack.MarkersChanged</c>, and three of them (round, kill,
///         bomb) legitimately never raise it because their data is fixed for the whole demo.
///     </para>
///     <para>
///         <b>Read from IL, not source.</b> A grep for an event name in production also matches the doc
///         comment describing a subscriber that does not exist. The subscriber is a call to <c>add_X</c>;
///         the raiser is a method that loads the event's backing field and is not the compiler's own
///         <c>add_X</c>/<c>remove_X</c> — neither can be faked by a comment.
///     </para>
/// </summary>
public class Playback2DEventWiringTests
{
    /// <summary>
    ///     The guard. Every event contract in the module — App-side view-models and services, Core, and
    ///     Pipeline — must have both halves, with no exceptions: no event may be allow-listed out instead
    ///     of wired.
    /// </summary>
    [Test]
    public async Task EveryModuleEvent_HasAProductionRaiser_AndAProductionSubscriber()
    {
        (List<EventContract> contracts, List<string> unanalysable) =
            Analyse(Playback2DWholeGraph.ModuleTypes, Playback2DWholeGraph.ProductionAssemblies);

        foreach (EventContract contract in contracts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"[event-wiring] {(contract.IsWired ? "ok  " : "DEAD")} {contract.Describe()}");
        }

        // An event with hand-written accessors has no backing field to watch, so this scan cannot see its
        // raise. None exists today; if one is added, the guard must grow rather than silently stop asking.
        await Assert.That(string.Join(", ", unanalysable)).IsEqualTo("")
            .Because("an event with custom add/remove accessors is invisible to this scan — extend the "
                     + "guard in the same commit that adds one");

        List<string> dead = contracts
            .Where(c => !c.IsWired)
            .Select(c => c.Describe())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(string.Join("; ", dead)).IsEqualTo("")
            .Because("an event with no production subscriber is a feature that computes its answer and "
                     + "throws it away — and one with no production raiser is a subscriber that will "
                     + "never run");
    }

    /// <summary>
    ///     The self-check. <see cref="Playback2DKeymapTests.FindConflicts_DetectsADuplicateGestureAndAShellCollision" />'s
    ///     rule: a guard that has never been shown to fail is a guard nobody has any reason to believe.
    ///     Three canary events in THIS assembly — one wired, one raised but never subscribed, one
    ///     subscribed but never raised — must be classified correctly, which pins both directions of the
    ///     scan at once.
    /// </summary>
    [Test]
    public async Task TheScan_DetectsAnUnsubscribedEvent_AndClearsAWiredOne()
    {
        SysAssembly self = typeof(Playback2DEventWiringTests).Assembly;
        (List<EventContract> contracts, List<string> _) = Analyse(
            [typeof(EventGuardCanary)],
            [.. Playback2DWholeGraph.ProductionAssemblies, self]);

        Dictionary<string, EventContract> byName = contracts.ToDictionary(
            c => c.Key[(c.Key.LastIndexOf('.') + 1)..], StringComparer.Ordinal);

        foreach (EventContract contract in contracts)
        {
            Console.WriteLine($"[event-canary] {contract.Describe()}");
        }

        await Assert.That(byName.Count).IsEqualTo(3);
        await Assert.That(byName["WiredCanary"].IsWired).IsTrue()
            .Because("raised in Raise() and subscribed from a different type — the shape a real event has");
        await Assert.That(byName["RaisedNeverSubscribedCanary"].Raisers).IsNotEmpty();
        await Assert.That(byName["RaisedNeverSubscribedCanary"].Subscribers).IsEmpty()
            .Because("this is exactly ExportJobService.StatusChanged's shape, and the guard must see it");
        await Assert.That(byName["SubscribedNeverRaisedCanary"].Subscribers).IsNotEmpty();
        await Assert.That(byName["SubscribedNeverRaisedCanary"].Raisers).IsEmpty();
    }

    /// <summary>
    ///     Guards the guard's REACH: if the module type set ever collapses (a namespace rename, a moved
    ///     assembly), every assertion above passes over an empty list. A floor, not a count.
    /// </summary>
    [Test]
    public async Task TheModuleSurface_IsActuallyBeingScanned()
    {
        (List<EventContract> contracts, List<string> _) =
            Analyse(Playback2DWholeGraph.ModuleTypes, Playback2DWholeGraph.ProductionAssemblies);

        Console.WriteLine($"[event-wiring] module types={Playback2DWholeGraph.ModuleTypes.Count()} "
                          + $"event contracts={contracts.Count}");

        await Assert.That(Playback2DWholeGraph.ModuleTypes.Count()).IsGreaterThan(100);
        await Assert.That(contracts.Count).IsGreaterThanOrEqualTo(12);

        // StatusChanged and MarkersChanged, pinned by key: a rename that silently drops them from the
        // scan is caught here rather than by the absence of a failure.
        await Assert.That(contracts.Any(c => c.Key.EndsWith(".StatusChanged", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(contracts.Any(c => c.Key.EndsWith(".MarkersChanged", StringComparison.Ordinal)))
            .IsTrue();
    }

    // ── The analysis ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Groups the public events declared by <paramref name="types" /> into contracts, then attributes
    ///     every raise and subscribe found in <paramref name="scope" /> to one.
    /// </summary>
    private static (List<EventContract> Contracts, List<string> Unanalysable) Analyse(
        IEnumerable<Type> types, IEnumerable<SysAssembly> scope)
    {
        List<string> unanalysable = [];

        // key -> (implementer type names, types whose add_ accessor counts, backing-field owners)
        Dictionary<string, (HashSet<string> Implementers, HashSet<string> AddOwners, string Name)> groups =
            new(StringComparer.Ordinal);

        foreach (Type type in types.Where(t => t is { IsInterface: false }))
        {
            foreach (EventInfo evt in type.GetEvents(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
            {
                if (evt.GetAddMethod(false) is null)
                {
                    continue; // not public
                }

                if (type.GetField(evt.Name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.Static | BindingFlags.DeclaredOnly) is null)
                {
                    unanalysable.Add($"{type.FullName}.{evt.Name}");
                    continue;
                }

                Type contract = InterfaceDeclaring(type, evt) ?? type;
                string key = contract.FullName + "." + evt.Name;

                if (!groups.TryGetValue(key, out (HashSet<string>, HashSet<string>, string) group))
                {
                    group = (new HashSet<string>(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal) { contract.FullName! },
                        evt.Name);
                    groups[key] = group;
                }

                group.Item1.Add(type.FullName!);
                group.Item2.Add(type.FullName!);
            }
        }

        if (groups.Count == 0)
        {
            return ([], unanalysable);
        }

        HashSet<string> addNames = groups.Values.Select(g => "add_" + g.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> fieldNames = groups.Values.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> addOwners = groups.Values.SelectMany(g => g.AddOwners).ToHashSet(StringComparer.Ordinal);
        HashSet<string> fieldOwners =
            groups.Values.SelectMany(g => g.Implementers).ToHashSet(StringComparer.Ordinal);

        List<IlSite> sites = Playback2DWholeGraph.Scan(scope, (type, member) =>
            (addNames.Contains(member) && addOwners.Contains(type))
            || (fieldNames.Contains(member) && fieldOwners.Contains(type)));

        List<EventContract> contracts = [];
        foreach ((string key, (HashSet<string> implementers, HashSet<string> owners, string name)) in groups)
        {
            List<string> raisers = [];
            List<string> subscribers = [];

            foreach (IlSite site in sites)
            {
                bool isSubscribe = site.Access == IlAccess.Call
                                   && string.Equals(site.TargetMember, "add_" + name, StringComparison.Ordinal)
                                   && owners.Contains(site.TargetType);

                // The compiler's own accessors load the backing field too; they are the plumbing, not a
                // raise. Everything else that loads it is about to Invoke it.
                bool isRaise = site.Access == IlAccess.LoadField
                               && string.Equals(site.TargetMember, name, StringComparison.Ordinal)
                               && implementers.Contains(site.TargetType)
                               && site.Method != "add_" + name
                               && site.Method != "remove_" + name;

                if (isSubscribe)
                {
                    subscribers.Add($"{Short(site.Type)}::{site.Method}");
                }
                else if (isRaise)
                {
                    raisers.Add($"{Short(site.Type)}::{site.Method}");
                }
            }

            contracts.Add(new EventContract(key,
                [.. implementers.OrderBy(s => s, StringComparer.Ordinal)],
                [.. raisers.Distinct(StringComparer.Ordinal)],
                [.. subscribers.Distinct(StringComparer.Ordinal)]));
        }

        return (contracts, unanalysable);
    }

    // The interface whose event THIS event implements, or null. Resolved through the interface map rather
    // than by name, so a coincidental name match on an unrelated interface cannot merge two contracts.
    private static Type? InterfaceDeclaring(Type type, EventInfo evt)
    {
        MethodInfo? add = evt.GetAddMethod(true);
        if (add is null)
        {
            return null;
        }

        foreach (Type iface in type.GetInterfaces())
        {
            if (iface.GetEvent(evt.Name)?.GetAddMethod(true) is not { } ifaceAdd)
            {
                continue;
            }

            InterfaceMapping map = type.GetInterfaceMap(iface);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i] == ifaceAdd && map.TargetMethods[i] == add)
                {
                    return iface;
                }
            }
        }

        return null;
    }

    private static string Short(string fullName) => fullName[(fullName.LastIndexOf('.') + 1)..];
}

/// <summary>
///     The canary for <see cref="Playback2DEventWiringTests.TheScan_DetectsAnUnsubscribedEvent_AndClearsAWiredOne" />.
///     Three events with three different wirings, so the scan is shown to separate them rather than merely
///     to report nothing.
/// </summary>
internal sealed class EventGuardCanary
{
    /// <summary>Raised below, subscribed from <see cref="EventGuardCanaryHost" />. Must come back clean.</summary>
    public event Action? WiredCanary;

    /// <summary><c>ExportJobService.StatusChanged</c>'s shape: faithfully raised, nothing listening.</summary>
    public event Action? RaisedNeverSubscribedCanary;

    // CS0067 is the compiler noticing the very thing this canary exists to be: an event with no raise
    // anywhere in its declaring type. Suppressed here and NOWHERE else — a real one must stay loud.
#pragma warning disable CS0067
    /// <summary>The mirror image: a handler attached to something that can never fire.</summary>
    public event Action? SubscribedNeverRaisedCanary;
#pragma warning restore CS0067

    public void Raise()
    {
        WiredCanary?.Invoke();
        RaisedNeverSubscribedCanary?.Invoke();
    }
}

/// <summary>
///     Subscribes from a DIFFERENT type on purpose: <c>+=</c> inside the declaring class compiles to a
///     direct <c>Delegate.Combine</c> on the backing field and never calls <c>add_</c>, so a self-wired
///     canary would prove nothing about how a real subscriber is detected.
/// </summary>
internal sealed class EventGuardCanaryHost
{
    public static void Wire(EventGuardCanary canary)
    {
        canary.WiredCanary += Noop;
        canary.SubscribedNeverRaisedCanary += Noop;
    }

    private static void Noop()
    {
    }
}
