#region

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The pack draws through the published scene package. What it still binds in the unpublished Playback2D
///     assemblies is read from its metadata, not its source: the scene package declares the same namespaces
///     as Core, so only the assembly reference tells the two apart.
/// </summary>
public class PackPlayback2DBindingTests
{
    private const string Export = "video and clip export stays first-party";
    private const string ReviewQueue = "the review queue stays first-party";
    private const string DemoFrames = "demo-to-frame adaptation stays first-party";

    private static readonly string[] UnpublishedAssemblies =
        ["DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Pipeline"];

    /// <summary>Every type the pack may bind in an unpublished Playback2D assembly, with the reason.</summary>
    private static readonly (string Assembly, string Type, string Reason)[] Allowed =
    [
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Export.CameraScript", Export),
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Export.CameraScript+Fixed", Export),
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Export.CameraScript+FollowPlayer", Export),
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Export.ExportProgress", Export),
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Export.ExportRequest", Export),
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Export.IFrameSink", Export),
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Export.ISceneFrameSource", Export),
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Rendering.CpuSurfaceProvider", Export),
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Rendering.IRenderSurfaceProvider", Export),
        ("DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Core.Rendering.RenderSurfaceProviderFactory", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Annotations.AnnotationStore", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Assets.LoadedMapAsset", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Assets.MapAssetPipeline", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Export.SceneExportSession", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Ffmpeg.EncoderProbeCache", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Ffmpeg.EncoderSelection", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Ffmpeg.EncoderSelector", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Ffmpeg.FfmpegLocation", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Ffmpeg.FfmpegLocator", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Ffmpeg.IEncoderProbe", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Frames.TrackerFrameSource", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Frames.TrackerSceneSnapshot", DemoFrames),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Playback2D.Pipeline.Headless.SceneLayerCatalog", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.IPackClipRenderer", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.IPackEncoder", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackClip", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackExporter", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackPlan", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackPlanner", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackProgress", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackResult", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackSegment", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackSettings", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackSkip", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackTitleCard", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Scene2DExportRequest", Export),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Review.ReviewEntry", ReviewQueue),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Review.ReviewEntryKind", ReviewQueue),
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Review.ReviewHighlightRef", ReviewQueue),
    ];

    [Test]
    public async Task ThePack_BindsOnlyTheAllowedTypes_InTheUnpublishedPlayback2DAssemblies()
    {
        SortedSet<string> bound = BoundTypes(typeof(StratBookPack).Assembly.Location);
        SortedSet<string> allowed = new(Allowed.Select(a => $"{a.Assembly}|{a.Type}"), StringComparer.Ordinal);

        string[] unexpected = [.. bound.Except(allowed)];
        string[] stale = [.. allowed.Except(bound)];

        await Assert.That(unexpected).IsEmpty()
            .Because("the pack binds these unpublished Playback2D types:\n" + string.Join("\n", unexpected));
        await Assert.That(stale).IsEmpty()
            .Because("these allowed types are no longer bound, so drop them from the list:\n" + string.Join("\n", stale));
    }

    // "Assembly|Namespace.Type" for each type reference whose outermost scope is an unpublished assembly;
    // nested types read "Outer+Inner".
    private static SortedSet<string> BoundTypes(string assemblyPath)
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
            if (UnpublishedAssemblies.Contains(assembly))
            {
                bound.Add($"{assembly}|{md.GetString(type.Namespace)}.{name}");
            }
        }

        return bound;
    }
}
