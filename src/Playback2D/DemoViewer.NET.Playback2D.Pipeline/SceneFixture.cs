#region

using System.Text.Json;
using DemoViewer.NET.Playback2D.Core;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline;

/// <summary>
///     A serialized scene: one <see cref="Scene2DFrame" /> plus the camera, size and time to render it
///     at. Fixtures are the design-iteration loop (<c>dv2d render --fixture …</c> re-renders in well
///     under a second, with no app launch and no demo parse) and the golden-test corpus at once: the
///     same artifact, which is why iteration and regression coverage cannot drift apart.
/// </summary>
public sealed record SceneFixture
{
    /// <summary>The current fixture schema version.</summary>
    public const string CurrentSchemaVersion = "playback2d-scene/1";

    /// <summary>Schema version of this fixture. Readers are tolerant; writers stamp the current one.</summary>
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The scene state.</summary>
    public required Scene2DFrame Frame { get; init; }

    /// <summary>The clock to render at. Independent of <see cref="Frame" />'s own time so a fixture can be re-timed.</summary>
    public SceneTime Time { get; init; }

    /// <summary>The camera to render through.</summary>
    public ViewportTransform Camera { get; init; }

    /// <summary>The default render / golden size.</summary>
    public SKSizeI Size { get; init; }

    /// <summary>Map identity for the asset-pipeline lookup that re-attaches radar images.</summary>
    public string? MapName { get; init; }

    /// <summary>The bundle's <c>mapVersion</c> CRC, so a re-baked asset invalidates the golden.</summary>
    public string? MapVersion { get; init; }

    /// <summary>
    ///     The annotation document, opaque until B2 gives it a DTO (decision D7). Preserved verbatim
    ///     across a read/write round trip either way.
    /// </summary>
    public JsonElement? Annotations { get; init; }

    /// <summary>Which demo this was captured from, for regeneration.</summary>
    public string? SourceDemoId { get; init; }

    /// <summary>Free-text note: what this fixture is for, and anything a reviewer should know.</summary>
    public string? Notes { get; init; }

    // Top-level members this build does not understand, carried through a read/write round trip so a
    // fixture written by a NEWER build is not silently truncated by an older one. Internal because it is
    // a transport detail of the format, not part of the scene.
    internal Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Reads a fixture from a file.</summary>
    /// <param name="path">Path to the <c>.scene.json</c> file.</param>
    public static SceneFixture Load(string path) => SceneFixtureSerializer.ReadFile(path);

    /// <summary>Writes this fixture to a file, creating the directory if needed.</summary>
    /// <param name="path">Path to the <c>.scene.json</c> file.</param>
    public void Save(string path) => SceneFixtureSerializer.WriteFile(this, path);
}
