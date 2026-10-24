#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A minimal <see cref="IModuleContext" /> that also carries a loaded demo via
///     <see cref="ICurrentDemoSource" />, the first-party demo-access seam. Only the members the
///     Workbench actually touches (Players / CurrentPlayers / Advanced / the demo) are functional; the
///     rest are unused stubs.
/// </summary>
// Not Playback2DFakeContext: that one is compiled into third-party extension test projects, and
// ICurrentDemoSource is a host-internal seam those must not see.
internal sealed class FakeDemoContext(ParsedDemo demo) : IModuleContext, ICurrentDemoSource
{
    public ParsedDemo? CurrentDemo { get; } = demo;
    public IReadOnlyList<PlayerRosterEntry> Players => [];
    public IReadOnlyList<IPlayerState> CurrentPlayers => [];

    public event Action<IPlaybackSnapshot>? Advanced
    {
        add { }
        remove { }
    }

    public bool HasDemo => true;
    public string? DemoPath => null;
    public int TickRate => 64;
    public double CurtimeSeconds(int tick) => 0;
    public int CurrentFrameIndex => 0;
    public int CurrentTick => 0;
    public bool IsPlaying => false;
    public double Speed => 1;

    public void RequestSeekToFrame(int frameIndex)
    {
    }

    public void RequestSeekToTick(int tick)
    {
    }

    public void RequestPlay()
    {
    }

    public void RequestPause()
    {
    }

    public IReadOnlyEntityView Entities => throw new NotSupportedException("unused by the Workbench");
}
