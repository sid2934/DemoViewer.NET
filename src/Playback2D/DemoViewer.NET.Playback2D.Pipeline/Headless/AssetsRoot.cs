namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>Where a resolved asset root came from. Reported in every <c>--json</c> payload.</summary>
internal enum AssetsRootSource
{
    /// <summary>A path the caller named outright.</summary>
    Flag,

    /// <summary>An environment variable the caller read.</summary>
    Env,

    /// <summary>A walk-up probe found an <c>assets/</c> directory.</summary>
    Probe,

    /// <summary>Map art is deliberately not loaded.</summary>
    Disabled,

    /// <summary>Nothing was found.</summary>
    NotFound
}

/// <summary>
///     A resolved (or deliberately absent) map-asset root: the baked directory that
///     <c>tools/DemoViewer.NET.AssetBaker</c> writes, one subdirectory per map holding
///     <c>bundle.json</c> plus its radar PNGs.
///     <para>
///         The winning rung is carried, not just the path: a golden failure caused by a different asset
///         root has to be diagnosable from a CI log alone.
///     </para>
/// </summary>
/// <param name="Path">The directory holding one subdirectory per map, or null.</param>
/// <param name="Source">How it was resolved.</param>
/// <param name="Probed">The candidates that were considered, for the caller's error message.</param>
internal sealed record AssetsRoot(string? Path, AssetsRootSource Source, IReadOnlyList<string> Probed)
{
    /// <summary>No root was looked for. <see cref="Path" /> null, so no map art is loaded.</summary>
    public static AssetsRoot None { get; } = new(null, AssetsRootSource.NotFound, []);

    /// <summary>The lowercase token that appears as <c>assets_source</c> in JSON payloads.</summary>
    public string SourceToken => Source switch
    {
        AssetsRootSource.Flag => "flag",
        AssetsRootSource.Env => "env",
        AssetsRootSource.Probe => "probe",
        AssetsRootSource.Disabled => "disabled",
        _ => "not-found"
    };
}
