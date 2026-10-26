#region

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The pack draws through the published scene package and reaches the host's services through the SDK.
///     What it still binds in the unpublished Playback2D assembly, and in the app itself, is read from its
///     metadata, not its source: the scene package and Pipeline declare the same namespaces, so only the
///     assembly reference tells the two apart.
///     <para>
///         Both lists being empty does not say the pack compiles without those references. A const is
///         invisible here, because its value is inlined at the call site and no type reference is emitted,
///         so a const-only class never shows up however heavily it is read. The <c>ProjectReference</c>
///         being gone from the pack's csproj is what proves that, and this reads what is left.
///     </para>
/// </summary>
public class PackPlayback2DBindingTests
{
    private static readonly string[] UnpublishedAssemblies = ["DemoViewer.NET.Playback2D.Pipeline"];

    /// <summary>
    ///     Every app type the pack may bind, with the reason: none. Everything it reaches in the host goes
    ///     through the SDK, and the pack no longer references the app assembly at all.
    /// </summary>
    private static readonly (string Type, string Reason)[] AllowedApp = [];

    /// <summary>
    ///     What the pack may bind in the unpublished Playback2D assembly: nothing. Video and clip export,
    ///     the review queue and the pack export all go through the SDK.
    /// </summary>
    private static readonly (string Assembly, string Type, string Reason)[] Allowed = [];

    [Test]
    public async Task ThePack_BindsOnlyTheAllowedTypes_InTheApp()
    {
        SortedSet<string> bound = BoundTypes(typeof(StratBookPack).Assembly.Location, ["DemoViewer.NET"]);
        SortedSet<string> allowed = new(AllowedApp.Select(a => $"DemoViewer.NET|{a.Type}"), StringComparer.Ordinal);

        string[] unexpected = [.. bound.Except(allowed)];
        string[] stale = [.. allowed.Except(bound)];

        await Assert.That(unexpected).IsEmpty()
            .Because("the pack binds these app types outside the first-party seam:\n" + string.Join("\n", unexpected));
        await Assert.That(stale).IsEmpty()
            .Because("these allowed app types are no longer bound, so drop them from the list:\n" + string.Join("\n", stale));
    }

    [Test]
    public async Task ThePack_BindsOnlyTheAllowedTypes_InTheUnpublishedPlayback2DAssemblies()
    {
        SortedSet<string> bound = BoundTypes(typeof(StratBookPack).Assembly.Location, UnpublishedAssemblies);
        SortedSet<string> allowed = new(Allowed.Select(a => $"{a.Assembly}|{a.Type}"), StringComparer.Ordinal);

        string[] unexpected = [.. bound.Except(allowed)];
        string[] stale = [.. allowed.Except(bound)];

        await Assert.That(unexpected).IsEmpty()
            .Because("the pack binds these unpublished Playback2D types:\n" + string.Join("\n", unexpected));
        await Assert.That(stale).IsEmpty()
            .Because("these allowed types are no longer bound, so drop them from the list:\n" + string.Join("\n", stale));
    }

    // "Assembly|Namespace.Type" for each type reference whose outermost scope is one of the assemblies;
    // nested types read "Outer+Inner".
    private static SortedSet<string> BoundTypes(string assemblyPath, string[] assemblies)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new(stream);
        MetadataReader md = pe.GetMetadataReader();

        SortedSet<string> bound = new(StringComparer.Ordinal);
        foreach (TypeReferenceHandle handle in md.TypeReferences)
        {
            TypeReference type = md.GetTypeReference(handle);
            string name = md.GetString(type.Name);
            while (type.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                type = md.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
                name = md.GetString(type.Name) + "+" + name;
            }

            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference)
            {
                continue;
            }

            string assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
            if (assemblies.Contains(assembly))
            {
                bound.Add($"{assembly}|{md.GetString(type.Namespace)}.{name}");
            }
        }

        return bound;
    }
}
