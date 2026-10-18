namespace DemoViewer.NET.Services.DemoProcessing.Collectors;

/// <summary>
///     Where a collector's own throw is reported: the registering extension's guard, outside tests. Kept as
///     its own interface, not the guard type itself, so the collector types that hold one stay Avalonia-free;
///     AnalysisBench links them by source and cannot reference it.
/// </summary>
internal interface ICollectorFaultReporter
{
    void Report(string site, Exception exception);
}
