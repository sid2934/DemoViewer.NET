namespace DemoViewer.NET.Testing.Tiers;

/// <summary>Constraint keys for <c>[NotInParallel]</c>, shared across test assemblies.</summary>
/// <remarks>
///     Key on the resource, not the class. A class whose tests merely must not overlap each other
///     wants <c>[NotInParallel(nameof(TheClass))]</c> and needs nothing from here; a lane below
///     exists only where several classes contend for one process-wide thing.
/// </remarks>
public static class TestLanes
{
    /// <summary>
    ///     The single headless Avalonia dispatcher, and the application statics the classes that drive
    ///     it share. One key for all of them: per-class keys would let those classes overlap on the
    ///     shared dispatcher and the shared statics, and they would re-serialize on the one UI
    ///     thread anyway.
    /// </summary>
    public const string HeadlessUi = "headless-ui";
}
