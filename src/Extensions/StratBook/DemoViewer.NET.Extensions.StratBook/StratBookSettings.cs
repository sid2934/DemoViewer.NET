#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The Strat Book's settings, kept in its own settings file. Every key and default is here, so no reader
///     carries its own copy of a default. The keys are persisted: never rename one.
/// </summary>
public sealed class StratBookSettings
{
    /// <summary>Situations: index the whole library in the background.</summary>
    public const string SituationsBackgroundIndexKey = "situations.backgroundIndex";

    /// <summary>Situations: which name a place row carries. Part of the index fingerprint.</summary>
    public const string TokenSourceKey = "situations.tokenSource";

    /// <summary>Grenades: walk the whole library in the background.</summary>
    public const string GrenadesBackgroundIndexKey = "grenades.backgroundIndex";

    /// <summary>Grenades: keep every n-th moved sample of a flight.</summary>
    public const string TrajectoryStrideKey = "grenades.trajectoryStride";

    /// <summary>Grenades: render a clip of each repeated lineup.</summary>
    public const string RenderLineupClipsKey = "grenades.renderLineupClips";

    /// <summary>Grenades: the most the lineup clips may take on disk, in MB.</summary>
    public const string LineupClipsMaxMegabytesKey = "grenades.lineupClipsMaxMegabytes";

    /// <summary>The tag palette 2D Playback tags with.</summary>
    public const string TagPaletteIdKey = "tagPalette.id";

    /// <summary>Whether 2D Playback is in Review mode.</summary>
    public const string ReviewModeKey = "review.mode";

    /// <summary>Suggested Tags: sweep the whole library in the background.</summary>
    public const string SuggestedTagsBackgroundKey = "suggestedTags.background";

    /// <summary>The palette an unknown or missing id falls back to.</summary>
    public const string DefaultTagPaletteId = "cs2-default";

    private readonly IExtensionSettings _store;

    /// <param name="store">The extension's settings.</param>
    public StratBookSettings(IExtensionSettings store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    ///     Background library indexing for Situations, default on: the flagship needs coverage, and at one to
    ///     three seconds a demo the sweep is a third of the highlights scan. Off, only a demo the user retries is
    ///     indexed.
    /// </summary>
    public bool SituationsBackgroundIndex => _store.Get(SituationsBackgroundIndexKey, true);

    /// <summary>
    ///     Which string names a row's place: the pawn's (Valve's names, no asset) or the team's zones where a map
    ///     has them. Part of the index fingerprint, so changing it re-indexes the library.
    /// </summary>
    public RoundIndexTokenSource TokenSource => _store.Get(TokenSourceKey, RoundIndexTokenSource.Pawn);

    /// <summary>
    ///     Background grenade walk, default off: about two and a half seconds a demo is half an hour over a
    ///     700-demo library. Off, the open demo is still walked on its own parse.
    /// </summary>
    public bool GrenadesBackgroundIndex => _store.Get(GrenadesBackgroundIndexKey, false);

    /// <summary>Keep every n-th moved sample of a flight; bounce vertices are always kept. At least 1.</summary>
    public int TrajectoryStride => Math.Max(1, _store.Get(TrajectoryStrideKey, 4));

    /// <summary>Lineup clip render, default on.</summary>
    public bool RenderLineupClips => _store.Get(RenderLineupClipsKey, true);

    /// <summary>The most the lineup clips folder may hold, in MB; zero or less caps nothing.</summary>
    public int LineupClipsMaxMegabytes => _store.Get(LineupClipsMaxMegabytesKey, 1024);

    /// <summary>The palette 2D Playback tags with.</summary>
    public string TagPaletteId
    {
        get => _store.Get(TagPaletteIdKey, DefaultTagPaletteId);
        set => _store.Set(TagPaletteIdKey, value);
    }

    /// <summary>Whether 2D Playback is in Review mode. Off by default, so plain playback keeps the full player cards.</summary>
    public bool ReviewMode
    {
        get => _store.Get(ReviewModeKey, false);
        set => _store.Set(ReviewModeKey, value);
    }

    /// <summary>
    ///     Whether Suggested Tags sweeps the whole library in the background. Off by default: the open demo is
    ///     always evaluated on the parse its open paid for.
    /// </summary>
    public bool SuggestedTagsBackground
    {
        get => _store.Get(SuggestedTagsBackgroundKey, false);
        set => _store.Set(SuggestedTagsBackgroundKey, value);
    }

    /// <summary>Raised on the UI thread with the key that changed.</summary>
    public event Action<string>? Changed
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    /// <summary>True when a change to <paramref name="key" /> can change what a pass wants, so the library is asked again.</summary>
    /// <param name="key">The key that changed.</param>
    public static bool WidensPasses(string key) =>
        key is SituationsBackgroundIndexKey or TokenSourceKey or GrenadesBackgroundIndexKey or SuggestedTagsBackgroundKey;

    /// <summary>The Grenade Index settings page, drawn by the host.</summary>
    public static SettingsSchema GrenadeIndexSchema { get; } = new("stratbook.grenade-index", "GRENADE INDEX",
    [
        SettingDescriptor.Toggle(GrenadesBackgroundIndexKey, "Walk library grenades in the background", false,
            "About 2 s per demo. Off = the open demo's grenades, and any demo from Match Overview."),
        SettingDescriptor.Toggle(RenderLineupClipsKey, "Render lineup clips", true,
            "A GIF of each lineup thrown more than once, beside its setpos line, queued in Review."),
        SettingDescriptor.Number(LineupClipsMaxMegabytesKey, "Lineup clips folder limit (MB)", 1024, 0, 1_000_000, 64,
            "Past it the least recently used clips are deleted after a render. 0 caps nothing."),
        SettingDescriptor.Number(TrajectoryStrideKey, "Flight sample stride", 4, 1, 64, 1,
            "Keep every n-th moved sample of a flight. Read when a demo is walked.")
    ])
    {
        Order = 1,
        Keywords = "grenade index utility book lineup clip render walk background"
    };
}
