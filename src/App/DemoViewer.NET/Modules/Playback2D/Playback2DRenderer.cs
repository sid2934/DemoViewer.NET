#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>The two things that can sit in the Playback2D tab's viewport slot.</summary>
public enum Playback2DRendererKind
{
    /// <summary>The v2 compositor host. The default.</summary>
    Scene,

    /// <summary>
    ///     The pre-v2 <see cref="Playback2DViewport" />. A parity escape hatch, deleted the release AFTER
    ///     v2 ships; <c>docs/playback2d-v2/old-control-removal.md</c> carries the trigger conditions.
    /// </summary>
    Legacy
}

/// <summary>
///     Both surfaces satisfy this, so the view's mode selector, follow funnel and Fit button drive
///     either one without knowing which is mounted.
/// </summary>
public interface IPlayback2DSurface
{
    /// <summary>The active camera mode.</summary>
    CameraMode Mode { get; set; }

    /// <summary>The followed roster slot; -1 clears. Setting it implies follow mode.</summary>
    int FollowSlot { set; }

    /// <summary>Re-frames to the observed extent and clears manual overrides.</summary>
    void FitToExtent();
}

/// <summary>
///     The level half of the viewport contract: what the level strip drives and reads.
///     <para>
///         Deliberately <b>separate</b> from <see cref="IPlayback2DSurface" /> and implemented only by
///         the v2 host. The pre-v2 <c>Playback2DViewport</c> has no level identity at all: its cameras
///         are keyed by array index, so giving it stub members would let the strip appear over a
///         surface that cannot honour a single one of them. Under the
///         legacy escape hatch the strip simply does not bind, exactly as it does not on a
///         single-floor map.
///     </para>
/// </summary>
public interface ILevelSurface
{
    /// <summary>The resolved level set. Live; subscribe to <see cref="LevelStateChanged" />.</summary>
    MapSpace Levels { get; }

    /// <summary>Stacked bands, or one pane showing <see cref="ActiveLevelId" />.</summary>
    LevelDisplayMode DisplayMode { get; set; }

    /// <summary>Whether the shown level tracks the followed player.</summary>
    bool AutoLevelFollow { get; set; }

    /// <summary>The level a single-pane layout is showing.</summary>
    MapLevelId ActiveLevelId { get; }

    /// <summary>Pins a level, switching to a single pane and turning AutoFollow off.</summary>
    /// <param name="id">The level to show.</param>
    void PickLevel(MapLevelId id);

    /// <summary>Raised when the level set, the active level or the display mode changed.</summary>
    event Action? LevelStateChanged;
}

/// <summary>
///     The ANNOTATION half of the viewport contract: what the toolbar, the keymap's tool-scoped rows and
///     the ink gestures need from the mounted surface. Implemented only by the v2 host.
///     <para>
///         <b>
///             A gate says whether the user is ALLOWED to draw; this says whether there is anything to
///             draw ON.
///         </b>
///         Binding the toolbar's visibility to the feature gate instead renders the whole
///         docked tool row over a surface with no router, no ink layer and no gesture to cancel. Picking
///         Draw then flips <c>IsDrawingToolActive</c>, so the keymap's <c>WhenToolActive</c> scope wins
///         and <c>Space</c>/<c>Esc</c> resolve to handlers that fall through a concrete-type check and
///         return without setting <c>Handled</c>: play/pause dies with no visible cause. This is a
///         CAPABILITY the mounted surface either satisfies or does not, asked once at bind time.
///     </para>
/// </summary>
internal interface IAnnotationSurface
{
    /// <summary>Selects the active pointer tool.</summary>
    /// <param name="kind">The tool.</param>
    void SetActiveTool(ToolKind kind);

    /// <summary>Hold-to-pan. The view sets it from the pan key.</summary>
    /// <param name="held">Whether the pan key is down.</param>
    void SetSpacePanHeld(bool held);

    /// <summary>Abandons whatever gesture is in flight.</summary>
    void CancelActiveGesture();
}

/// <summary>
///     Chooses which surface the tab mounts.
///     <para>
///         <b>Deliberately not a <c>FeatureCatalog</c> id.</b> Catalog ids are permanent persisted keys,
///         and this toggle exists to be deleted with the legacy control. It is an environment variable
///         for CI and bisecting, over a developer-mode-only setting.
///     </para>
///     <para>
///         Resolved <b>once per process</b>: a mid-session flip would leave two surfaces disagreeing
///         about camera state, and the escape hatch is there for a clean A/B.
///     </para>
/// </summary>
public static class Playback2DRenderer
{
    /// <summary>The environment variable, honoured on desktop and absent on WASM.</summary>
    public const string EnvironmentVariable = "DV_PLAYBACK2D_RENDERER";

    private static Playback2DRendererKind? _forced;
    private static Playback2DRendererKind? _resolved;

    /// <summary>
    ///     Which surface to mount, in order: the environment variable, then
    ///     <c>AppSettings.Playback2D.LegacyViewport</c>, then <see cref="Playback2DRendererKind.Scene" />.
    /// </summary>
    public static Playback2DRendererKind Selected => _forced ?? (_resolved ??= Resolve());

    /// <summary>Test hook: pins the selection, or clears the pin when null.</summary>
    /// <param name="forced">The kind to force, or null to restore normal resolution.</param>
    internal static void ResetForTest(Playback2DRendererKind? forced)
    {
        _forced = forced;
        _resolved = null;
    }

    private static Playback2DRendererKind Resolve()
    {
        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.Equals(env, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            return Playback2DRendererKind.Legacy;
        }

        if (string.Equals(env, "scene", StringComparison.OrdinalIgnoreCase))
        {
            return Playback2DRendererKind.Scene;
        }

        // Settings are optional here on purpose: the surface is constructed by a control, which a
        // headless test builds with no container at all. A missing service means the default, and a
        // settings layer that cannot bind must never stop the tab from opening.
        try
        {
            if (App.Services?.GetService<SettingsService>()?.Current.Playback2D.LegacyViewport == true)
            {
                return Playback2DRendererKind.Legacy;
            }
        }
        catch (Exception)
        {
            // fall through to the default
        }

        return Playback2DRendererKind.Scene;
    }
}
