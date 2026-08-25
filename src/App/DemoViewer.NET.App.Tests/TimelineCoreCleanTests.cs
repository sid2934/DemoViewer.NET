#region

using System.Reflection;
using DemoViewer.NET.Modules.Playback2D.Timeline;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Architecture gate for the timeline CONTRACT set. <c>ITimelineTrack</c> and friends live app-side only
///     because <c>DemoViewer.NET.Playback2D.Core</c> does not exist yet; B1 moves the folder with a
///     namespace rewrite and nothing else. That move is only mechanical while these types touch no Avalonia,
///     no <c>Modules.Abstractions</c>, no parser, and no ambient clock — so it is asserted, not hoped for.
///     <para>
///         <b>B1 deletes this class</b> and replaces it with Core's own reference test.
///     </para>
/// </summary>
public class TimelineCoreCleanTests
{
    // The app-side boundary types that deliberately live in the same namespace and do NOT move.
    private static readonly HashSet<string> _appSideAllowList = new(StringComparer.Ordinal)
    {
        "ModuleTimelineData",
        "Playback2DTimelineViewModel",
        "TimelineMarkerViewModel",
        "TimelineBandViewModel",
        "TimelineTrackToggle"
    };

    private static readonly string[] _forbiddenNamespaces =
    [
        "Avalonia",
        "DemoViewer.NET.Modules.Abstractions",
        "CS2DemoKit"
    ];

    private static readonly string[] _forbiddenTypes =
    [
        "System.DateTime",
        "System.DateTimeOffset",
        "System.Diagnostics.Stopwatch",
        "System.Random"
    ];

    [Test]
    public async Task ContractTypes_ReferenceNoHostRendererOrClockTypes()
    {
        List<string> violations = [];
        List<string> inspected = [];

        foreach (Type type in ContractTypes())
        {
            inspected.Add(type.Name);

            foreach (Type referenced in ReferencedTypes(type))
            {
                string full = referenced.FullName ?? referenced.Name;

                foreach (string ns in _forbiddenNamespaces)
                {
                    if (full.StartsWith(ns + ".", StringComparison.Ordinal))
                    {
                        violations.Add($"{type.Name} references {full}");
                    }
                }

                foreach (string forbidden in _forbiddenTypes)
                {
                    if (string.Equals(full, forbidden, StringComparison.Ordinal))
                    {
                        violations.Add($"{type.Name} references {full}");
                    }
                }
            }
        }

        Console.WriteLine($"[timeline-core-clean] inspected: {string.Join(", ", inspected.Order())}");

        // Vacuous-pass guard: if the namespace were renamed the loop above would find nothing and pass.
        await Assert.That(inspected).Contains("ITimelineTrack");
        await Assert.That(inspected).Contains("RoundTrack");
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task ContractAssembly_HasAllSixTrackMembers()
    {
        // Every implementer ships all six, so B2's AnnotationTrack cannot land a three-member sketch.
        string[] members = [.. typeof(ITimelineTrack).GetMembers().Select(m => m.Name)];

        foreach (string required in new[]
                 {
                     "Id", "DisplayName", "IsAvailable", "BuildMarkers", "BuildBands"
                 })
        {
            await Assert.That(members).Contains(required);
        }

        await Assert.That(typeof(ITimelineTrack).GetEvent("MarkersChanged")).IsNotNull();

        foreach (Type track in new[]
                 {
                     typeof(RoundTrack), typeof(KillTrack), typeof(BombTrack)
                 })
        {
            await Assert.That(typeof(ITimelineTrack).IsAssignableFrom(track)).IsTrue();
        }
    }

    [Test]
    public async Task TrackIds_AreBareWords()
    {
        await Assert.That(new RoundTrack().Id).IsEqualTo("round");
        await Assert.That(new KillTrack().Id).IsEqualTo("kill");
        await Assert.That(new BombTrack().Id).IsEqualTo("bomb");
    }

    private static IEnumerable<Type> ContractTypes() =>
        typeof(ITimelineTrack).Assembly
            .GetTypes()
            .Where(t => t.Namespace == typeof(ITimelineTrack).Namespace)
            .Where(t => !_appSideAllowList.Contains(RootName(t)))
            // Compiler-generated closures / iterator classes are nested inside their owner; the owner's
            // own signature surface is what the move has to keep clean.
            .Where(t => !t.Name.Contains('<', StringComparison.Ordinal));

    private static string RootName(Type type)
    {
        Type root = type;
        while (root.DeclaringType is { } declaring)
        {
            root = declaring;
        }

        return root.Name;
    }

    // Everything the type's own signature surface mentions: base + interfaces, field types, property
    // types, method parameter and return types, and generic arguments of each.
    private static List<Type> ReferencedTypes(Type type)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                                    | BindingFlags.Instance | BindingFlags.Static
                                                    | BindingFlags.DeclaredOnly;

        List<Type> found = [];

        if (type.BaseType is { } baseType)
        {
            found.Add(baseType);
        }

        found.AddRange(type.GetInterfaces());
        found.AddRange(type.GetFields(All).Select(f => f.FieldType));
        found.AddRange(type.GetProperties(All).Select(p => p.PropertyType));

        foreach (MethodInfo method in type.GetMethods(All))
        {
            found.Add(method.ReturnType);
            found.AddRange(method.GetParameters().Select(p => p.ParameterType));
        }

        foreach (ConstructorInfo ctor in type.GetConstructors(All))
        {
            found.AddRange(ctor.GetParameters().Select(p => p.ParameterType));
        }

        foreach (Type found1 in found.ToArray())
        {
            if (found1.IsGenericType)
            {
                found.AddRange(found1.GetGenericArguments());
            }

            if (found1.HasElementType && found1.GetElementType() is { } element)
            {
                found.Add(element);
            }
        }

        return found;
    }
}
