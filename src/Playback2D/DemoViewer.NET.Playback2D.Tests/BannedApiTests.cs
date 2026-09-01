#region

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using SysAssembly = System.Reflection.Assembly;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Determinism, enforced by test rather than by convention (design §5.1). A wall clock, a stopwatch
///     or an RNG anywhere in the render path means an export cannot be reproduced and a golden image is
///     not a gate, so the ban is checked against compiled IL, where a call cannot hide behind a helper
///     method or a lambda.
///     <para>
///         <b>Offenders are attributed to the type that makes the call</b>, by walking each method's IL
///         rather than just listing the assembly's member references. That costs a little more code and
///         buys the one thing a reference list cannot give: an exemption that is scoped to a namespace
///         instead of switching the whole assembly off. B1's benchmark harness has to read a stopwatch
///         and stamp a report, that is its entire job, while <c>SceneFrameBuilder</c>, three
///         namespaces away in the same assembly, must never touch either.
///     </para>
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

    /// <summary>
    ///     The measurement harness. It exists to time the pipeline from OUTSIDE it, so a stopwatch and a
    ///     timestamp are the deliverable, not a leak, which is exactly why plan T16 puts it in Pipeline
    ///     rather than Core in the first place.
    /// </summary>
    private static readonly string[] _exemptNamespacePrefixes =
    [
        "DemoViewer.NET.Playback2D.Pipeline.Benchmarking.",

        // B4: SceneExportSession's progress report carries elapsed time, throughput and an ETA. Those are
        // wall-clock quantities by definition, a progress bar measuring scene time would be useless, and
        // none of them reaches a layer: frames advance on the injected SceneTime, which is what
        // ExportDeterminismTests pins.
        "DemoViewer.NET.Playback2D.Pipeline.Export."
    ];

    [Test]
    public async Task Core_ContainsNo_DateTimeNow_Stopwatch_Or_Random()
    {
        // Core has NO exemption: nothing in the render core has a legitimate reason to know the time.
        List<string> offenders = ScanForBannedUses(typeof(Scene2DFrame).Assembly, []);
        Report(offenders);
        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task Pipeline_ContainsNo_DateTimeNow_Stopwatch_Or_Random()
    {
        List<string> offenders = ScanForBannedUses(typeof(SceneFrameBuilder).Assembly,
            _exemptNamespacePrefixes);
        Report(offenders);
        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    ///     The exemptions are narrow on purpose: drop them, and the benchmark harness and the export
    ///     session's progress clock are the ONLY things that trip the scan. If this ever finds nothing,
    ///     the harness has stopped timing anything; if it finds something outside those two namespaces,
    ///     the exemption has silently widened to cover a real leak.
    /// </summary>
    [Test]
    public async Task TheBenchmarkExemption_IsActuallyLoadBearing()
    {
        List<string> unexempted = ScanForBannedUses(typeof(SceneFrameBuilder).Assembly, []);
        Console.WriteLine($"[banned] without the exemption: {string.Join(", ", unexempted)}");

        await Assert.That(unexempted).IsNotEmpty();
        await Assert.That(unexempted.TrueForAll(IsExempt)).IsTrue();
    }

    private static bool IsExempt(string offender)
    {
        foreach (string prefix in _exemptNamespacePrefixes)
        {
            if (offender.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void Report(List<string> offenders)
    {
        foreach (string offender in offenders)
        {
            Console.WriteLine($"[banned] {offender}");
        }
    }

    private static List<string> ScanForBannedUses(SysAssembly assembly, string[] exemptPrefixes)
    {
        using FileStream stream = File.OpenRead(assembly.Location);
        using PEReader pe = new(stream);
        MetadataReader reader = pe.GetMetadataReader();

        // Pass 1: precise. Every reference this assembly makes into another one is a MemberRef row, so
        // this finds the banned calls exactly, with no guessing about instruction boundaries.
        Dictionary<int, string> bannedTokens = [];
        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            string typeName = TypeName(reader, (TypeReferenceHandle)member.Parent);
            if (!_banned.TryGetValue(typeName, out HashSet<string>? members))
            {
                continue;
            }

            string memberName = reader.GetString(member.Name);
            if (members.Count == 0 || members.Contains(memberName))
            {
                bannedTokens[MetadataTokens.GetToken(handle)] = $"{typeName}::{memberName}";
            }
        }

        if (bannedTokens.Count == 0)
        {
            return [];
        }

        // Pass 2: attribution. Look for those exact token values in each method body, preceded by a
        // token-carrying opcode. Searching for KNOWN tokens rather than decoding every instruction is
        // what keeps this honest: an unattributed reference is still reported below, so the worst case
        // is a slightly over-broad name, never a missed call.
        List<string> offenders = [];
        HashSet<int> attributed = [];

        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            string typeName = TypeName(reader, type);

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue; // abstract, extern, or otherwise bodiless
                }

                byte[]? il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                if (il is null)
                {
                    continue;
                }

                for (int i = 1; i + 4 < il.Length; i++)
                {
                    // call / callvirt / newobj / ldfld / ldsfld: the five ways a banned member is reached.
                    if (il[i - 1] is not (0x28 or 0x6F or 0x73 or 0x7B or 0x7E))
                    {
                        continue;
                    }

                    int token = BitConverter.ToInt32(il, i);
                    if (!bannedTokens.TryGetValue(token, out string? member))
                    {
                        continue;
                    }

                    attributed.Add(token);
                    if (!Array.Exists(exemptPrefixes, p => typeName.StartsWith(p, StringComparison.Ordinal)))
                    {
                        offenders.Add($"{typeName}::{reader.GetString(method.Name)} -> {member}");
                    }
                }
            }
        }

        foreach ((int token, string member) in bannedTokens)
        {
            if (!attributed.Contains(token))
            {
                offenders.Add($"<unattributed> -> {member}");
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }

    private static string TypeName(MetadataReader reader, TypeDefinition type)
    {
        string ns = reader.GetString(type.Namespace);
        string name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string TypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string ns = reader.GetString(type.Namespace);
        string name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}
