#region

using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Shared activation helper for <see cref="Playback2DTabViewModel" /> over a recording context. Its
///     own file, linked into the extension test project too, so it is not pulled in alongside
///     <c>Playback2DActionDispatchTests</c>'s own tests.
/// </summary>
internal static class Playback2DActivation
{
    /// <param name="demoPath">
    ///     The demo the context is on, set BEFORE activation. Null (the default) is the shape every
    ///     pre-round-3A caller had. It matters because the tab's resync clears the follow target when the
    ///     path changes under it, so a test that assigns the path after activation has already staged a
    ///     demo swap without meaning to.
    /// </param>
    internal static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx) Activated(string? demoPath = null)
    {
        Playback2DFakeContext ctx = new()
        {
            Gate = new FakeModuleFeatureGate(),
            DemoPath = demoPath
        };
        ctx.AddPlayer(0, "Alpha", 2);
        ctx.AddPlayer(1, "Bravo", 2);
        ctx.AddPlayer(2, "Charlie", 3);
        ctx.Frames["round_freeze_end"] = [0, 300, 600];
        ctx.Timelines["player_death"] = [];

        Playback2DTabViewModel vm = new();
        vm.OnActivated(ctx);
        return (vm, ctx);
    }
}
