#region

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The pack draws through the published scene package. What it still binds in the unpublished Playback2D
///     assemblies, and in the app itself, is read from its metadata, not its source: the scene package declares
///     the same namespaces as Core, so only the assembly reference tells the two apart.
/// </summary>
public class PackPlayback2DBindingTests
{
    private const string Export = "video and clip export stays first-party";
    private const string ReviewQueue = "the review queue stays first-party";
    private const string DemoFrames = "demo-to-frame adaptation stays first-party";

    private const string Seam = "the first-party seam the container hands the pack";
    private const string StratCanvas = "the strat canvas mounts the 2D tab's scene host, timeline and ink";

    private static readonly string[] UnpublishedAssemblies =
        ["DemoViewer.NET.Playback2D.Core", "DemoViewer.NET.Playback2D.Pipeline"];

    /// <summary>Every app type the pack may bind, with the reason. Anything else of the app goes through the SDK.</summary>
    private static readonly (string Type, string Reason)[] AllowedApp =
    [
        ("DemoViewer.NET.Configuration.AppSettings", Export),
        ("DemoViewer.NET.Configuration.Playback2DSettings", Export),
        ("DemoViewer.NET.Configuration.SettingsService", Export),
        ("DemoViewer.NET.Extensions.FirstPartyExports", Seam),
        ("DemoViewer.NET.Extensions.FirstPartyHost", Seam),
        ("DemoViewer.NET.Extensions.IFirstPartyExportChips", Seam),
        ("DemoViewer.NET.Extensions.IFirstPartyShellState", Seam),
        ("DemoViewer.NET.Extensions.ScenePointer", StratCanvas),
        ("DemoViewer.NET.Modules.Playback2D.Annotations.AnnotationSessionController", StratCanvas),
        ("DemoViewer.NET.Modules.Playback2D.IAnnotationSurface", StratCanvas),
        ("DemoViewer.NET.Modules.Playback2D.IGuidesHost", StratCanvas),
        ("DemoViewer.NET.Modules.Playback2D.ISceneFrameHost", StratCanvas),
        ("DemoViewer.NET.Modules.Playback2D.ITokenEditingHost", StratCanvas),
        ("DemoViewer.NET.Modules.Playback2D.Playback2DKeymapProfile", StratCanvas),
        ("DemoViewer.NET.Modules.Playback2D.Scene2DHost", StratCanvas),
        ("DemoViewer.NET.Modules.Playback2D.ScenePaletteFactory", Export),
        ("DemoViewer.NET.Modules.Playback2D.Timeline.Playback2DTimelineViewModel", StratCanvas),
        ("DemoViewer.NET.Modules.Playback2D.Timeline.TimelineBandRow", StratCanvas),
        ("DemoViewer.NET.Services.Dependencies.FfmpegDependency", Export),
        ("DemoViewer.NET.Services.Export.ExportEncoding", Export),
        ("DemoViewer.NET.Services.Export.ExportJobService", Export),
        ("DemoViewer.NET.Services.Export.ExportRefusedException", Export),
        ("DemoViewer.NET.Services.Export.ExportSceneSetup", Export),
        ("DemoViewer.NET.Services.Export.IExportJobService", Export),
        ("DemoViewer.NET.Services.Export.IExportRunner", Export),
        ("DemoViewer.NET.Services.Export.SceneExportRunner", Export),
        ("DemoViewer.NET.Services.HeavyJobGate", Export),
        ("DemoViewer.NET.Services.Review.ReviewQueue", ReviewQueue),
        ("DemoViewer.NET.ViewModels.Playback2D.AnnotationsPanelViewModel", StratCanvas),
        ("DemoViewer.NET.ViewModels.Playback2D.ExportDialogScene", Export),
        ("DemoViewer.NET.ViewModels.Playback2D.ExportRangeOption", Export),
        ("DemoViewer.NET.ViewModels.Playback2D.ExportSizeOption", Export),
        ("DemoViewer.NET.ViewModels.Playback2D.FfmpegAcquire", Export),
        ("DemoViewer.NET.ViewModels.Playback2D.Playback2DExportDialogViewModel", Export),
        ("DemoViewer.NET.ViewModels.Playback2D.Playback2DExportStatusViewModel", Export),
        ("DemoViewer.NET.Views.Playback2D.TimelineControl", StratCanvas),
    ];

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
        ("DemoViewer.NET.Playback2D.Pipeline", "DemoViewer.NET.Services.Export.Pack.PackClip", Export),
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
