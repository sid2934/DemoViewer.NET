#region

using Avalonia.Media;
using Avalonia.Styling;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Theming;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     Resolves the scene's 31 colours from the app's theme tokens into a <see cref="ScenePalette" />.
///     <para>
///         The renderer's colours are TOKENS (<c>Pb2dCanvas*</c> and <c>Pb2dTeamT/Ct</c>) looked up in
///         the theme dictionaries for a variant, so <i>any</i> theme — built-in or a user drop-in —
///         colours the radar with no code change. This runs <b>once per theme change</b>, never per
///         frame: the render hot path reads the resolved record.
///     </para>
///     <para>
///         The hex fallbacks are the Dark values and are identical, colour for colour and in the same
///         order, to <see cref="ScenePalette.Dark" /> — which is what lets a headless render (goldens,
///         export, the CLI) produce the same picture with no theme system loaded at all.
///         <c>ScenePaletteFactoryTests</c> asserts the two agree.
///     </para>
/// </summary>
public static class ScenePaletteFactory
{
    /// <summary>Resolves the palette for a theme variant.</summary>
    /// <param name="variant">The control's actual theme variant, or null for the app default.</param>
    public static ScenePalette Build(ThemeVariant? variant)
    {
        return new ScenePalette(
            C("Pb2dCanvasBg", "#15181C"),
            C("Pb2dCanvasMinorGrid", "#22272E"),
            C("Pb2dCanvasMajorGrid", "#2E3742"),
            C("Pb2dCanvasLabel", "#9AA4AF"),
            C("Pb2dTeamT", "#E0A030"),
            C("Pb2dTeamCt", "#4A90D9"),
            C("Pb2dCanvasNeutral", "#888888"),
            C("Pb2dCanvasSightlineT", "#70E0A030"),
            C("Pb2dCanvasSightlineCt", "#704A90D9"),
            C("Pb2dCanvasConeT", "#3CE0A030"),
            C("Pb2dCanvasConeCt", "#3C4A90D9"),
            C("Pb2dCanvasConeNeutral", "#2C888888"),
            C("Pb2dCanvasRingShooting", "#FFD400"),
            C("Pb2dCanvasRingDamage", "#F44336"),
            C("Pb2dCanvasRingBlinded", "#FFFFFFFF"),
            C("Pb2dCanvasRingDead", "#555B62"),
            C("Pb2dCanvasBomb", "#F03A2E"),
            C("Pb2dCanvasBombTrack", "#40FFFFFF"),
            C("Pb2dCanvasBombDetonation", "#FF5040"),
            C("Pb2dCanvasBombDefuse", "#40C4FF"),
            C("Pb2dCanvasSmoke", "#66AEB6BD"),
            C("Pb2dCanvasSmokeStroke", "#88C8CED4"),
            C("Pb2dCanvasFire", "#78FF6A1A"),
            C("Pb2dCanvasTrailHe", "#FF5252"),
            C("Pb2dCanvasTrailFlash", "#FFE082"),
            C("Pb2dCanvasTrailSmoke", "#B0BEC5"),
            C("Pb2dCanvasTrailMolotov", "#FF7043"),
            C("Pb2dCanvasTrailDecoy", "#81C784"),
            C("Pb2dCanvasMarkerRingT", "#C8881F"),
            C("Pb2dCanvasMarkerRingCt", "#357ABD"),
            C("Pb2dCanvasMarkerRingNeutral", "#666666"),
            SceneStrokeWidths.Default);

        SKColor C(string key, string fallbackHex) => ToSkia(ThemeColors.Get(key, variant, fallbackHex));
    }

    /// <summary>Converts an Avalonia colour to a Skia one.</summary>
    /// <param name="colour">The Avalonia colour.</param>
    public static SKColor ToSkia(Color colour) => new(colour.R, colour.G, colour.B, colour.A);
}
