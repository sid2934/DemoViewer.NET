#region

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;
using SysAssembly = System.Reflection.Assembly;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>How a target member was reached, so a read can be told from a write and a raise from a wire-up.</summary>
internal enum IlAccess
{
    None,

    /// <summary><c>call</c> / <c>callvirt</c>.</summary>
    Call,

    /// <summary><c>newobj</c>.</summary>
    New,

    /// <summary><c>ldfld</c> / <c>ldsfld</c> / <c>ldflda</c> / <c>ldsflda</c>.</summary>
    LoadField,

    /// <summary><c>stfld</c> / <c>stsfld</c>.</summary>
    StoreField
}

/// <summary>One reference from a production method to a member the caller asked about.</summary>
/// <param name="Assembly">The assembly holding the CALLER.</param>
/// <param name="Type">The caller's declaring type, fully qualified.</param>
/// <param name="Method">The caller's method name.</param>
/// <param name="TargetType">The referenced member's declaring type, fully qualified.</param>
/// <param name="TargetMember">The referenced member's name.</param>
/// <param name="Access">Which instruction reached it.</param>
internal readonly record struct IlSite(
    string Assembly,
    string Type,
    string Method,
    string TargetType,
    string TargetMember,
    IlAccess Access)
{
    public override string ToString() => $"{Type}::{Method} -{Access}-> {TargetType}::{TargetMember}";
}

/// <summary>
///     The shared machinery behind four architecture guards. Each asks a <b>whole-graph reachability</b>
///     question (is this event subscribed, is this command bound, is this setting written, is this seam
///     supplied), and every one of them is invisible to a unit test by construction, because a unit
///     test's job is to instantiate the thing directly and hand it what it needs.
///     <para>
///         <b>Two lenses, deliberately.</b> Anything expressible in IL is read from IL (below), never from
///         source text: a source grep for an event name also matches the <c>&lt;see cref&gt;</c> in the doc
///         comment that describes the missing half. Only two questions IL cannot answer fall back to
///         source, and those strip doc comments first: "does an <c>.axaml</c> string binding name this
///         command?" and "does this call site mention this constructor parameter?" (the C# compiler
///         materialises omitted optional arguments at the call site, so IL cannot tell an omission from
///         an explicit <c>null</c>).
///     </para>
/// </summary>
internal static class Playback2DWholeGraph
{
    // The App head, the render Core and the Pipeline. Everything a Playback2D consumer could live in:
    // Desktop/Browser only set AppHostHooks, and LiveSync cannot see this module at all.
    private static readonly Lazy<SysAssembly[]> _production = new(() =>
    [
        typeof(AppSettings).Assembly,
        typeof(Scene2DFrame).Assembly,
        typeof(SceneFrameBuilder).Assembly
    ]);

    private static readonly Lazy<List<SourceFile>> _sources = new(LoadProductionSources);

    /// <summary>The three assemblies a production consumer of this module can be in.</summary>
    public static IReadOnlyList<SysAssembly> ProductionAssemblies => _production.Value;

    /// <summary>
    ///     Production <c>.cs</c> and <c>.axaml</c> under <c>src/</c> and <c>tools/</c>, doc comments stripped.
    /// </summary>
    public static IReadOnlyList<SourceFile> ProductionSources => _sources.Value;

    /// <summary>
    ///     Every type of the module, across the three assemblies. Namespace-based rather than
    ///     directory-based so a type that moves file keeps its membership.
    ///     <para>
    ///         <c>DemoViewer.NET.Services.Export</c> is in it explicitly: <c>ExportJobService</c> and
    ///         <c>SceneExportRunner</c> are the 2D module's services in every sense except the folder they
    ///         landed in.
    ///     </para>
    /// </summary>
    public static IEnumerable<Type> ModuleTypes =>
        ProductionAssemblies.SelectMany(SafeTypes).Where(t => IsModuleNamespace(t.Namespace));

    /// <summary>Whether a namespace belongs to the Playback2D module. See <see cref="ModuleTypes" />.</summary>
    public static bool IsModuleNamespace(string? ns) =>
        ns is not null
        && (ns.Contains("Playback2D", StringComparison.Ordinal)
            || string.Equals(ns, "DemoViewer.NET.Services.Export", StringComparison.Ordinal));

    /// <summary>The repo root, or a skip: a source-reading guard says nothing about a stray binary.</summary>
    public static string RepoRoot() =>
        DemoTestHelper.FindRepoRoot()
        ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");

    /// <summary>
    ///     Every reference made from a method body in <paramref name="assemblies" /> to a member
    ///     <paramref name="isTarget" /> accepts, attributed to the method that makes it.
    ///     <para>
    ///         Two passes, following <c>BannedApiTests</c>: collect the metadata tokens that name an
    ///         interesting member (a <c>MemberRef</c> for a cross-assembly member, a <c>MethodDef</c> /
    ///         <c>Field</c> for a same-assembly one), then look for those exact token values in each method
    ///         body behind a token-carrying opcode. Searching for KNOWN tokens rather than decoding every
    ///         instruction is what keeps the cost linear; the worst case is an over-broad attribution, never
    ///         a missed reference.
    ///     </para>
    /// </summary>
    /// <param name="assemblies">Assemblies to scan. Ones with no on-disk location are skipped.</param>
    /// <param name="isTarget">Predicate over (declaring type full name, member name).</param>
    public static List<IlSite> Scan(IEnumerable<SysAssembly> assemblies, Func<string, string, bool> isTarget)
    {
        List<IlSite> sites = [];

        foreach (SysAssembly assembly in assemblies)
        {
            string location = assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                continue; // single-file / in-memory: nothing to read
            }

            // ReadWrite|Delete, not File.OpenRead's bare Read: these assemblies are MAPPED by the running
            // test host, and several guards scan them concurrently. The most permissive share mode is the
            // only one that cannot turn into an intermittent IOException on Windows.
            using FileStream stream = new(location, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using PEReader pe = new(stream);
            MetadataReader reader = pe.GetMetadataReader();
            string assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);

            Dictionary<int, (string Type, string Member)> targets = [];

            // Cross-assembly: every member this assembly reaches into another one is a MemberRef row.
            foreach (MemberReferenceHandle handle in reader.MemberReferences)
            {
                MemberReference member = reader.GetMemberReference(handle);
                if (member.Parent.Kind != HandleKind.TypeReference)
                {
                    continue; // TypeSpec (generic instantiation) / ModuleRef: no target of ours is one
                }

                string type = TypeName(reader, (TypeReferenceHandle)member.Parent);
                string name = reader.GetString(member.Name);
                if (isTarget(type, name))
                {
                    targets[MetadataTokens.GetToken(handle)] = (type, name);
                }
            }

            // Same-assembly: the call site carries the MethodDef / Field token directly.
            foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(typeHandle);
                string typeName = TypeName(reader, type);

                foreach (MethodDefinitionHandle handle in type.GetMethods())
                {
                    string name = reader.GetString(reader.GetMethodDefinition(handle).Name);
                    if (isTarget(typeName, name))
                    {
                        targets[MetadataTokens.GetToken(handle)] = (typeName, name);
                    }
                }

                foreach (FieldDefinitionHandle handle in type.GetFields())
                {
                    string name = reader.GetString(reader.GetFieldDefinition(handle).Name);
                    if (isTarget(typeName, name))
                    {
                        targets[MetadataTokens.GetToken(handle)] = (typeName, name);
                    }
                }
            }

            if (targets.Count == 0)
            {
                continue;
            }

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

                    string methodName = reader.GetString(method.Name);
                    for (int i = 1; i + 4 <= il.Length; i++)
                    {
                        IlAccess access = OpcodeAccess(il[i - 1]);
                        if (access == IlAccess.None)
                        {
                            continue;
                        }

                        int token = BitConverter.ToInt32(il, i);
                        if (!targets.TryGetValue(token, out (string Type, string Member) target))
                        {
                            continue;
                        }

                        sites.Add(new IlSite(assemblyName, typeName, methodName,
                            target.Type, target.Member, access));
                    }
                }
            }
        }

        return sites;
    }

    /// <summary>Types this assembly can load: a missing optional dependency must not be fatal.</summary>
    private static IEnumerable<Type> SafeTypes(SysAssembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private static IlAccess OpcodeAccess(byte opcode) => opcode switch
    {
        0x28 or 0x6F => IlAccess.Call, // call, callvirt
        0x73 => IlAccess.New, // newobj
        0x7B or 0x7C or 0x7E or 0x7F => IlAccess.LoadField, // ldfld, ldflda, ldsfld, ldsflda
        0x7D or 0x80 => IlAccess.StoreField, // stfld, stsfld
        _ => IlAccess.None
    };

    private static string TypeName(MetadataReader reader, TypeDefinition type)
    {
        string name = reader.GetString(type.Name);
        if (type.IsNested)
        {
            // A nested type's own Namespace row is empty; qualify it by its enclosing chain so
            // "Foo+Bar" can never be confused with a top-level "Bar" in another namespace.
            TypeDefinition declaring = reader.GetTypeDefinition(type.GetDeclaringType());
            return TypeName(reader, declaring) + "+" + name;
        }

        string ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string TypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return TypeName(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
        }

        string ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    // ── The source corpus ───────────────────────────────────────────────────────────────────────────

    private static List<SourceFile> LoadProductionSources()
    {
        string root = RepoRoot();
        List<SourceFile> files = [];

        foreach (string top in new[]
                 {
                     "src", "tools"
                 })
        {
            string dir = Path.Combine(root, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            // IgnoreInaccessible so a build output directory being rewritten under the walk cannot turn
            // this into an intermittent failure. It cannot hide a real defect: the guards that read this
            // corpus assert a floor on its size, so a walk that lost the tree fails loudly instead.
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (string path in Directory.EnumerateFiles(dir, "*.*", options))
            {
                if (!IsProductionSourcePath(path))
                {
                    continue;
                }

                files.Add(new SourceFile(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    StripDocComments(ReadShared(path))));
            }
        }

        return files;
    }

    // Same share mode, and for the same reason, as the assembly reader above: an editor or a build can
    // hold a source file open while this walk reaches it.
    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    // Test projects, the screenshot harness and the shared test support are all EXCLUDED: each of them
    // constructs the module's types the way a test does (with hand-supplied collaborators), which is
    // exactly the evidence these guards must not accept. bin/obj are build output, and the source
    // generators' *.g.cs under obj/ would otherwise "use" every command they generate.
    private static bool IsProductionSourcePath(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "bin" or "obj"
                || segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".TestSupport", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".UiCapture", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // A `/// <see cref="StatusChanged" />` promising a subscriber that does not exist would satisfy a
    // source grep, so the corpus these guards search never contains one.
    private static string StripDocComments(string text)
    {
        if (!text.Contains("///", StringComparison.Ordinal))
        {
            return text;
        }

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
            {
                lines[i] = "";
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>A production source file: repo-relative path, and its text with doc comments removed.</summary>
    /// <param name="Path">Repo-relative, forward-slashed.</param>
    /// <param name="Text">File text; every <c>///</c> line blanked.</param>
    internal readonly record struct SourceFile(string Path, string Text);
}
