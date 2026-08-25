#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Frames;

/// <summary>
///     Replays one or more committed <see cref="SceneFixture" />s as a frame source.
///     <para>
///         The point is that a benchmark and a golden can be driven from the <b>same JSON</b> as the
///         thing they are supposed to measure, with no demo, no parser and no tracker — which is what
///         makes the budget lane runnable on a CI container. <c>TrackerFrameSource</c> (C1) is the
///         real-demo counterpart and shares this interface.
///     </para>
///     <para>
///         A single-fixture source deliberately reports a <see cref="FrameCount" /> of one and lets the
///         caller loop: replaying one static frame is exactly the steady state the allocation budget is
///         about, and inventing motion here would measure the fake.
///     </para>
/// </summary>
public sealed class FixtureFrameSource : ISceneFrameSource
{
    private readonly SceneFixture[] _fixtures;

    /// <summary>Creates a source over fixtures, in order.</summary>
    /// <param name="fixtures">At least one fixture.</param>
    public FixtureFrameSource(params SceneFixture[] fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        if (fixtures.Length == 0)
        {
            throw new ArgumentException("at least one fixture is required", nameof(fixtures));
        }

        _fixtures = fixtures;
    }

    /// <inheritdoc />
    public int FrameCount => _fixtures.Length;

    /// <summary>The camera the first fixture was captured with — what a golden re-render must use.</summary>
    public ViewportTransform Camera => _fixtures[0].Camera;

    /// <summary>The map name the first fixture carries, or null.</summary>
    public string? MapName => _fixtures[0].MapName;

    /// <inheritdoc />
    public SceneTime TimeAt(int frameIndex) => _fixtures[Index(frameIndex)].Time;

    /// <inheritdoc />
    public Scene2DFrame FrameAt(int frameIndex) => _fixtures[Index(frameIndex)].Frame;

    private int Index(int frameIndex) => Math.Clamp(frameIndex, 0, _fixtures.Length - 1);
}
