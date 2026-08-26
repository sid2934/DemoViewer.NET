#region

using System.Reflection;
using System.Text.RegularExpressions;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Playback2D.Core.Rendering;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <b>D6 finding 25 — the <c>RenderBackend</c> settings key, and the decision NOT to add it.</b>
///     <para>
///         The registry (<c>00-overview.md</c> §3.10) pins the key, <c>AppSettings</c>' own class doc used
///         to promise that "C2 (render backend) … ADD properties here", and the whole GPU stack —
///         <c>RenderBackendPreference</c>, its parser, <c>RenderSurfaceProviderFactory</c>,
///         <c>GpuSurfaceProvider</c>, <c>RenderSurfaceProbe</c> — is built and tested. The key does not
///         exist, and round 3A deliberately did not add it, because <b>nothing in the app can consume
///         one</b>: the interactive host draws through Avalonia's own compositor and asks for no
///         <c>IRenderSurfaceProvider</c> at all, and <c>SceneExportSession</c> refuses every provider whose
///         backend is not <c>CpuRaster</c> (design §0 O2 — its loop crosses threads between frames while
///         the GPU provider is bound to the thread that made it).
///     </para>
///     <para>
///         A key added today would therefore be a preference whose every value behaves identically —
///         except <c>gpu</c>, which would turn every export into a validation failure. That is the audit's
///         own defect class one layer in, so this suite pins the honest state instead: the key is absent,
///         the reason is written where the next person to add it will read it, and the composition site it
///         will land on names its provider explicitly rather than inheriting a default.
///     </para>
///     <para>
///         <b>This suite is meant to be deleted</b>, by the commit that pins the export loop to one thread
///         and gives the key a real consumer.
///     </para>
/// </summary>
public class Playback2DRenderBackendTests
{
    /// <summary>
    ///     The key is absent AND the class doc says why. Absence alone is indistinguishable from an
    ///     oversight — which is exactly how it read before, when the same paragraph promised the key was
    ///     coming and no commit in the C2 track had anywhere to put it.
    /// </summary>
    [Test]
    public async Task TheRenderBackendKey_IsAbsent_AndTheClassDocSaysWhy()
    {
        bool declared = typeof(Playback2DSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => string.Equals(p.Name, "RenderBackend", StringComparison.Ordinal));

        await Assert.That(declared).IsFalse()
            .Because("adding it before a consumer exists ships a preference that cannot do anything — "
                     + "and whose one non-default value breaks export outright");

        string source = File.ReadAllText(Path.Combine(Playback2DWholeGraph.RepoRoot(),
            "src", "App", "DemoViewer.NET", "Configuration", "AppSettings.cs"));

        // The class doc, not just the file: the promise that had to go lived in Playback2DSettings' own
        // <summary>, beside the list of phases that "ADD properties here".
        int at = source.IndexOf("public sealed class Playback2DSettings", StringComparison.Ordinal);
        await Assert.That(at).IsGreaterThan(0);
        string doc = source[..at];

        Console.WriteLine($"[render-backend] AppSettings.cs mentions RenderBackend "
                          + $"{Regex.Count(doc, "RenderBackend")} time(s) above the class");

        await Assert.That(doc).Contains("RenderBackend")
            .Because("a key the registry pins and the class lacks must be explained where the class is");
        await Assert.That(doc).Contains("SceneExportSession")
            .Because("the reason is a specific refusal in a specific type, not a vague 'not yet'");
    }

    /// <summary>
    ///     The landing site. G1 is that C# materialises an omitted optional argument at the CALL SITE, so
    ///     an omission is invisible in IL and nothing distinguishes "this composition chose the default"
    ///     from "this composition forgot the parameter" — which is how the app ended up CPU-only by
    ///     accident while the GPU stack was fully built. The sole production composition now names its
    ///     surface provider, so the day the refusal lifts, the key has one argument to land on.
    /// </summary>
    [Test]
    public async Task TheAppsExportComposition_NamesItsSurfaceProvider()
    {
        string source = File.ReadAllText(Path.Combine(Playback2DWholeGraph.RepoRoot(),
            "src", "App", "DemoViewer.NET", "Modules", "Playback2D", "Playback2DTabViewModel.cs"));

        Match construction = Regex.Match(source, @"new SceneExportRunner\((?s).*?\),\s*\r?\n\s*host\.Gate");
        await Assert.That(construction.Success).IsTrue()
            .Because("the sole production composition of the export runner must be findable, or this "
                     + "suite is asserting over nothing");

        Console.WriteLine("[render-backend] composition names: "
                          + string.Join(", ", Regex.Matches(construction.Value, @"(\w+):\s")
                              .Select(m => m.Groups[1].Value)));

        await Assert.That(construction.Value).Contains("surfaces:")
            .Because("naming it is what makes CPU a decision rather than the constructor's default");
    }

    /// <summary>
    ///     And the value it names is the only one that works. If this ever stops being true the refusal in
    ///     <c>SceneExportSession</c> has moved, which is precisely the commit that should be adding the
    ///     settings key — and deleting this suite.
    /// </summary>
    [Test]
    public async Task TheProviderTheAppNames_IsTheOnlyBackendExportAccepts()
    {
        using IRenderSurfaceProvider provider = RenderSurfaceProviderFactory.CreateCpu();

        Console.WriteLine($"[render-backend] app export provider = {provider.Backend}");
        await Assert.That(provider.Backend).IsEqualTo(RenderBackend.CpuRaster);
    }
}
