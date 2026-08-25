#region

using System.Reflection;
using SysAssembly = System.Reflection.Assembly;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Determinism, enforced by test rather than by convention (design §5.1). A wall clock, a stopwatch
///     or an RNG anywhere in the render path means an export cannot be reproduced and a golden image is
///     not a gate — so the ban is checked against the compiled metadata, where a call cannot hide behind
///     a helper method or a lambda.
/// </summary>
public class BannedApiTests
{
    // Fully-qualified type name → the members banned on it. An empty set bans the whole type.
    private static readonly Dictionary<string, HashSet<string>> _banned = new(StringComparer.Ordinal)
    {
        ["System.DateTime"] = ["get_Now", "get_UtcNow", "get_Today"],
        ["System.DateTimeOffset"] = ["get_Now", "get_UtcNow"],
        ["System.Diagnostics.Stopwatch"] = [],
        ["System.Random"] = [],
        ["System.Environment"] = ["get_TickCount", "get_TickCount64"],
        ["System.Threading.Thread"] = ["Sleep"]
    };

    [Test]
    public async Task Core_ContainsNo_DateTimeNow_Stopwatch_Or_Random() =>
        await Assert.That(ScanForBannedUses(typeof(Scene2DFrame).Assembly)).IsEmpty();

    [Test]
    public async Task Pipeline_ContainsNo_DateTimeNow_Stopwatch_Or_Random() =>
        await Assert.That(ScanForBannedUses(typeof(SceneFrameBuilder).Assembly)).IsEmpty();

    private static List<string> ScanForBannedUses(SysAssembly assembly)
    {
        List<string> offenders = [];
        using FileStream stream = File.OpenRead(assembly.Location);
        using PEReader pe = new(stream);
        MetadataReader reader = pe.GetMetadataReader();

        // Member references catch every call and field access into another assembly.
        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            string typeName = FullName(reader, (TypeReferenceHandle)member.Parent);
            if (!_banned.TryGetValue(typeName, out HashSet<string>? members))
            {
                continue;
            }

            string memberName = reader.GetString(member.Name);
            if (members.Count == 0 || members.Contains(memberName))
            {
                offenders.Add($"{typeName}::{memberName}");
            }
        }

        // Type references catch a banned type used only as a field or local — e.g. a Stopwatch field
        // constructed via a generic helper, which leaves no direct member reference.
        foreach (TypeReferenceHandle handle in reader.TypeReferences)
        {
            string typeName = FullName(reader, handle);
            if (_banned.TryGetValue(typeName, out HashSet<string>? members) && members.Count == 0)
            {
                offenders.Add(typeName);
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }

    private static string FullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string ns = reader.GetString(type.Namespace);
        string name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}
